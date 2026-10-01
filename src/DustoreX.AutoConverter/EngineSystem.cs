using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DustoreX.AutoConverter;

public sealed record ConversionPlan(string InputPath, string Engine, string SourcePlatform, string Target, string Method,
    string Title, string Detail, bool CanConvert, string? RuntimeVersion, IReadOnlyList<string> Warnings);
public sealed record ConversionRequest(string InputPath, TargetPlatform Target, string Name, string? OutputPath = null,
    string? RuntimeVersion = null, string Architecture = "arm64", string? RuntimePath = null, string Method = "auto");

public static class ConversionEngine
{
    public static ConversionPlan Inspect(string input, TargetPlatform target, string architecture = "arm64")
    {
        if (!Enum.IsDefined(target)) throw new ArgumentException("Неизвестная целевая система.");
        ValidateArchitecture(architecture);
        input = Path.GetFullPath(input);
        if (!File.Exists(input) && !Directory.Exists(input)) throw new FileNotFoundException("Файл или папка игры не найдены.", input);
        var warnings = new List<string>();
        string engine = "Не определён", source = "Не определена", method = "unsupported";
        string? version = null;
        var godot = TryInspectBackend("GodotPackager", input, warnings);
        if (godot is not null) { engine = "Godot"; method = "godot"; version = ReadVersion(godot); warnings.AddRange(ReadWarnings(godot)); }
        else
        {
            var old = BuildAnalyzer.Analyze(input, target);
            source = old.DetectedPlatform;
            if (old.Engine == "LÖVE")
            {
                using var payload = LovePayload.Open(input);
                engine = "LÖVE"; method = "love"; version = payload.DeclaredVersion;
                warnings.AddRange(payload.Warnings);
            }
            else
            {
                var renpy = TryInspectBackend("RenPyPackager", input, warnings);
                var nw = renpy is null ? TryInspectBackend("NwPackager", input, warnings) : null;
                if (renpy is not null) { engine = "Ren’Py"; method = "renpy"; version = ReadVersion(renpy); warnings.AddRange(ReadWarnings(renpy)); }
                else if (nw is not null) { engine = "NW.js / RPG Maker / HTML5"; method = "nw"; version = ReadVersion(nw); warnings.AddRange(ReadWarnings(nw)); }
                else if (target == TargetPlatform.MacOS && ContainsWindowsBuild(input))
                { method = "wine"; engine = old.Engine == "Unknown" ? "Windows игра" : old.Engine; }
                else if (target == TargetPlatform.Windows && ContainsWindowsBuild(input)) method = "native";
                else { warnings.AddRange(old.Warnings); engine = old.Engine == "Unknown" ? engine : old.Engine; }
            }
        }
        if (source == "Не определена") source = DetectSourcePlatform(input);
        if ((target == TargetPlatform.Windows && source.StartsWith("Windows", StringComparison.Ordinal) && ContainsWindowsBuild(input))
            || (target == TargetPlatform.MacOS && source.StartsWith("macOS", StringComparison.Ordinal)))
            method = "native";
        if (version is null && method is "love" or "nw" or "renpy")
        {
            version = RuntimeCatalog.DefaultVersion(method);
            warnings.Add("Версия исходного движка не установлена. Выбрана " + version + "; совместимость этой версии нужно проверить запуском.");
        }
        string title = method switch { "wine" => "Windows игра через Wine на Mac", "native" => "Эта сборка уже предназначена для " + TargetName(target), "unsupported" => "Нужны переносимые данные или исходный проект", _ => "Перепаковка в движок для " + TargetName(target) };
        string detail = method switch
        {
            "wine" => "Создадим Mac-приложение с исходной Windows игрой. На Mac потребуется установленный Wine. Совместимость зависит от игры; Windows код остаётся внутри пакета. При выборе EXE по исходному пути включается вся его папка: используйте отдельную папку игры, чтобы не добавить личные файлы.",
            "native" => "Конвертация не требуется: сборка уже предназначена для " + TargetName(target) + ". Выберите другую систему для переноса.",
            "unsupported" => "У этой сборки не найдены данные поддерживаемого движка. Нативный Mac executable нельзя автоматически пересобрать под Windows без исходников и платформенных зависимостей.",
            _ => "Игровые данные сохранятся, а исполняемая часть будет взята из движка нужной системы. Движок загружается с официального источника один раз. Создание пакета не подтверждает его запуск на целевом компьютере."
        };
        return new(input, engine, source, TargetName(target), method, title, detail, method is not "unsupported" and not "native", version, warnings.Distinct().ToArray());
    }

    public static async Task<PackageResult> ConvertAsync(ConversionRequest request, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var plan = Inspect(request.InputPath, request.Target, request.Architecture);
        if (!plan.CanConvert) throw new InvalidDataException(plan.Detail);
        string method = request.Method == "auto" ? plan.Method : request.Method;
        if (method != plan.Method) throw new ArgumentException("Способ переноса не соответствует анализу сборки.");
        string name = string.IsNullOrWhiteSpace(request.Name) ? Path.GetFileNameWithoutExtension(request.InputPath.TrimEnd(Path.DirectorySeparatorChar)) : request.Name;
        SafeData.ValidateGameName(name);
        string output = request.OutputPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DustoreX Converted", name + "-" + (request.Target == TargetPlatform.Windows ? "windows" : "macos") + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        output = Path.GetFullPath(output);
        cancellation.ThrowIfCancellationRequested();
        progress?.Report("Способ: " + plan.Title);
        if (method == "wine") return await Task.Run(() => CompatibilityPackager.Package(new PackageRequest(plan.InputPath, "", output, request.Target, name)), cancellation);
        string version = request.RuntimeVersion ?? plan.RuntimeVersion ?? throw new InvalidDataException("Не удалось определить версию движка. Укажите её в дополнительных настройках.");
        if (plan.Method == "godot" && request.RuntimeVersion is not null && request.RuntimeVersion != plan.RuntimeVersion) throw new InvalidDataException("Версия Godot должна совпадать с версией игрового пакета.");
        var runtime = request.RuntimePath is not null ? Path.GetFullPath(request.RuntimePath) : await RuntimeCatalog.ResolveAsync(method, version, request.Target, request.Architecture, progress, cancellation);
        progress?.Report("Проверяю данные и собираю пакет…");
        var package = new PackageRequest(plan.InputPath, runtime, output, request.Target, name, version);
        var result = await Task.Run(() => PackageBackend(method, package), cancellation);
        if (request.RuntimePath is not null) result = result with { Warnings = result.Warnings.Concat(new[] { "Использован вручную выбранный runtime. Проверьте его происхождение, версию и архитектуру CPU для целевого компьютера." }).ToArray() };
        else result = result with
        {
            Warnings = result.Warnings.Where(w =>
                !w.StartsWith("The caller explicitly supplied the runtime.", StringComparison.Ordinal)
                && !w.StartsWith("The runtime was explicitly supplied by the caller.", StringComparison.Ordinal))
                .Append("Использован официальный runtime из каталога с проверенной контрольной суммой. Совместимость игры и возможностей движка требует запуска на целевой системе.").ToArray()
        };
        return result;
    }

    public static string TargetName(TargetPlatform target) => target == TargetPlatform.Windows ? "Windows" : "macOS";
    public static void ValidateArchitecture(string architecture) { if (architecture is not "arm64" and not "x64") throw new ArgumentException("Архитектура должна быть arm64 или x64."); }
    private static bool ContainsWindowsBuild(string path)
    {
        if (File.Exists(path) && !SafeData.IsZip(path))
        {
            using var file = File.OpenRead(path);
            return BinaryKind.Read(file).StartsWith("Windows PE", StringComparison.Ordinal);
        }
        using var data = SafeData.Open(path, true);
        foreach (var item in data.Items.Where(i => !i.IsDirectory && !i.IsSymlink && i.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            using var file = item.Open();
            if (BinaryKind.Read(file).StartsWith("Windows PE", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    private static string DetectSourcePlatform(string path)
    {
        if (File.Exists(path) && !SafeData.IsZip(path))
        {
            using var file = File.OpenRead(path);
            string kind = BinaryKind.Read(file);
            return kind.StartsWith("Windows PE", StringComparison.Ordinal) ? "Windows"
                : kind.StartsWith("macOS Mach-O", StringComparison.Ordinal) ? "macOS" : "Переносимые данные";
        }
        using var data = SafeData.Open(path, true);
        if (path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            || data.Items.Any(i => i.Name.Contains(".app/Contents/", StringComparison.OrdinalIgnoreCase))) return "macOS app bundle";
        return ContainsWindowsBuild(path) ? "Windows" : "Переносимые данные";
    }
    private static object? TryInspectBackend(string type, string path, List<string> warnings)
    {
        var backend = typeof(ConversionEngine).Assembly.GetType("DustoreX.AutoConverter." + type);
        var method = backend?.GetMethod("Inspect", BindingFlags.Public | BindingFlags.Static);
        if (method is null) return null;
        try { return method.Invoke(null, [path]); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException or IOException or ArgumentException or InvalidOperationException or System.Xml.XmlException or JsonException)
        {
            warnings.Add(type.Replace("Packager", "", StringComparison.Ordinal) + ": " + ex.InnerException.Message);
            return null;
        }
    }
    private static string? ReadVersion(object value) => (value.GetType().GetProperty("RuntimeVersion") ?? value.GetType().GetProperty("RequiredVersion") ?? value.GetType().GetProperty("EngineVersion"))?.GetValue(value)?.ToString();
    private static IEnumerable<string> ReadWarnings(object value) => value.GetType().GetProperty("Warnings")?.GetValue(value) as IEnumerable<string> ?? [];
    private static PackageResult PackageBackend(string method, PackageRequest request)
    {
        if (method == "love") return LovePackager.Package(request);
        string name = method switch { "godot" => "GodotPackager", "renpy" => "RenPyPackager", "nw" => "NwPackager", _ => throw new ArgumentException("Неизвестный адаптер.") };
        var backend = typeof(ConversionEngine).Assembly.GetType("DustoreX.AutoConverter." + name) ?? throw new InvalidOperationException("Адаптер отсутствует в этой сборке.");
        try { return (PackageResult)backend.GetMethod("Package", [typeof(PackageRequest)])!.Invoke(null, [request])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
}
