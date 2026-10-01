using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DustoreX.AutoConverter;

public static class CompatibilityPackager
{
    public static PackageResult Package(PackageRequest request)
    {
        if (request.Target != TargetPlatform.MacOS) throw new InvalidDataException("Wine-пакет предназначен для macOS.");
        SafeData.ValidateGameName(request.GameName);
        string input = Path.GetFullPath(request.InputPath), output = Path.GetFullPath(request.OutputPath);
        SafeData.RequireSeparateOutput(output, input);
        SafeData.RejectReparseAncestors(output);
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Выходной файл должен иметь расширение .zip.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Выходной путь уже существует.");
        bool single = File.Exists(input) && !SafeData.IsZip(input);
        string sourcePath = single ? Path.GetDirectoryName(input)! : input;
        using var data = SafeData.Open(sourcePath, false);
        if (single) SafeData.RequireSeparateOutput(output, sourcePath);
        var executables = data.Items.Where(i => !i.IsDirectory && i.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        DataItem executable;
        if (single) executable = executables.SingleOrDefault(i => i.Name == Path.GetFileName(input)) ?? throw new InvalidDataException("Исходный EXE не найден.");
        else
        {
            var candidates = executables.Where(i => !Path.GetFileName(i.Name).StartsWith("unins", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(i.Name).StartsWith("setup", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1) throw new InvalidDataException("В папке несколько EXE. Выберите исполняемый файл игры; соседние файлы будут включены автоматически.");
            executable = candidates[0];
        }
        using (var stream = executable.Open()) if (!BinaryKind.Read(stream).StartsWith("Windows PE", StringComparison.Ordinal)) throw new InvalidDataException("Файл не является Windows executable.");
        var warnings = new[] { "На Mac требуется установленный Wine с поддержкой архитектуры этой игры. Wine не включён в пакет.", "Этот пакет сохраняет Windows executable. Создание обёртки не подтверждает совместимость графики, DRM, античита и системных API.", "Сохранены все соседние файлы исходной папки. Не публикуйте личные данные и сохранения вместе с игрой." };
        string hash = data.Hash(), root = request.GameName + ".app/Contents/";
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string staging = output + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (var item in data.Items.Where(i => !i.IsDirectory))
                {
                    var entry = zip.CreateEntry(root + "Resources/game/" + item.Name, CompressionLevel.Optimal);
                    entry.ExternalAttributes = unchecked((int)(0x81a4U << 16));
                    using var read = item.Open(); using var write = entry.Open(); SafeData.CopyBounded(read, write, item.Length);
                }
                string parent = SafeData.ParentName(executable.Name).TrimEnd('/');
                string shellExe = Shell(Path.GetFileName(executable.Name));
                string workingDirectory = "\"$HERE/../Resources/game\"" + (parent.Length > 0 ? "/" + Shell(parent) : "");
                string script = "#!/bin/bash\nset -e\nHERE=\"$(cd -- \"$(dirname -- \"$0\")\" && pwd)\"\n" +
                    "if [ -n \"${DUSTOREX_WINE:-}\" ] && [ -x \"$DUSTOREX_WINE\" ]; then WINE=\"$DUSTOREX_WINE\"; " +
                    "elif command -v wine >/dev/null 2>&1; then WINE=\"$(command -v wine)\"; " +
                    "elif [ -x /opt/homebrew/bin/wine ]; then WINE=/opt/homebrew/bin/wine; " +
                    "elif [ -x /usr/local/bin/wine ]; then WINE=/usr/local/bin/wine; " +
                    "else /usr/bin/osascript -e 'display alert \"DustoreX: нужен Wine\" message \"Установите совместимый Wine на Mac, затем откройте игру снова. Инструкция находится в README.txt рядом с приложением.\"'; exit 1; fi\n" +
                    "export WINEPREFIX=\"$HOME/Library/Application Support/DustoreX/Wine/" + SafeId(request.GameName) + "\"\n" +
                    "mkdir -p -- \"$WINEPREFIX\"\ncd -- " + workingDirectory + "\nexec \"$WINE\" " + shellExe + " \"$@\"\n";
                Write(zip, root + "MacOS/launch", script, true);
                var plist = new XDocument(new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict",
                    new XElement("key", "CFBundleExecutable"), new XElement("string", "launch"),
                    new XElement("key", "CFBundleName"), new XElement("string", request.GameName),
                    new XElement("key", "CFBundleIdentifier"), new XElement("string", "local.dustorex.wine." + SafeId(request.GameName)),
                    new XElement("key", "CFBundlePackageType"), new XElement("string", "APPL"),
                    new XElement("key", "CFBundleVersion"), new XElement("string", "1.0"))));
                Write(zip, root + "Info.plist", plist.ToString());
                Write(zip, "README.txt", "DustoreX — Windows игра через Wine на macOS\n\n" + string.Join("\n", warnings) + "\n\nWine: https://www.winehq.org/\nОткройте .app. Если Wine лежит в нестандартном месте, запускайте Contents/MacOS/launch из Terminal с DUSTOREX_WINE=/абсолютный/путь/wine. На Apple Silicon требуется Wine, поддерживающий x86/x64 перевод.\n");
                Write(zip, "DustoreX-conversion.json", JsonSerializer.Serialize(new { method = "wine", status = "RequiresWine", testedOnTarget = false, sourceSha256 = hash, payloadSha256 = hash, warnings }, new JsonSerializerOptions { WriteIndented = true }));
            }
            MacZipMetadata.MarkUnixCreator(staging);
            File.Move(staging, output);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new(output, "RequiresWine", hash, hash, "", warnings);
    }
    private static string Shell(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string SafeId(string value) => SafeData.Hash(Encoding.UTF8.GetBytes(value))[..20];
    private static void Write(ZipArchive zip, string name, string value, bool executable = false)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.ExternalAttributes = unchecked((int)((executable ? 0x81edU : 0x81a4U) << 16));
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(value);
    }
}
