using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Signs only newly imported, simple Godot copies. Never edits a user source app or ZIP.</summary>
internal static class MacLocalSigner
{
    public static async Task SignOwnedGodotIfNeededAsync(string app, string ownedRoot, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) return;
        string appPath = Path.GetFullPath(app);
        string root = Path.GetFullPath(ownedRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!appPath.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Локальная подпись допустима только для новой копии в библиотеке.");
        var plan = await Task.Run(() => ConversionEngine.Inspect(appPath, TargetPlatform.Windows, "x64"), cancellation).ConfigureAwait(false);
        if (plan.Method != "godot") return;
        var entries = EnumerateWithoutLinks(appPath).ToArray();
        if (entries.Any(path => Path.GetExtension(path).ToLowerInvariant() is ".framework" or ".xpc" or ".appex" or ".bundle" or ".dylib" or ".so"
            || Path.GetFileName(path).Contains(".so.", StringComparison.OrdinalIgnoreCase)
            || path != appPath && path.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("У Godot-приложения найден дополнительный native код. Для этой сборки нужна отдельная подпись компонентов.");
        string executable = ReadExecutable(Path.Combine(appPath, "Contents", "Info.plist"));
        string binary = Path.Combine(appPath, "Contents", "MacOS", executable);
        string payload = Path.Combine(appPath, "Contents", "Resources", executable + ".pck");
        if (!File.Exists(binary) || !File.Exists(payload)
            || Directory.EnumerateFileSystemEntries(Path.Combine(appPath, "Contents", "MacOS")).Any(p => p != binary))
            throw new InvalidDataException("Локальная подпись поддерживает только простой Godot bundle с одним executable и соответствующим PCK.");
        if ((File.GetUnixFileMode(binary) & UnixFileMode.UserExecute) == 0) throw new InvalidDataException("У Godot executable отсутствует право запуска.");
        await RunAsync(["--force", "--sign", "-", "--preserve-metadata=entitlements", "--timestamp=none", appPath], cancellation).ConfigureAwait(false);
        await RunAsync(["--verify", "--deep", "--strict", "--verbose=2", appPath], cancellation).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateWithoutLinks(string directory)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("В Godot bundle найдена ссылка: автоматическая подпись этой сборки не поддерживается.");
            yield return path;
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (string nested in EnumerateWithoutLinks(path)) yield return nested;
        }
    }

    private static string ReadExecutable(string plist)
    {
        using var reader = XmlReader.Create(plist, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = XDocument.Load(reader);
        var nodes = document.Root?.Element("dict")?.Elements().ToArray() ?? throw new InvalidDataException("Некорректный Info.plist.");
        for (int i = 0; i + 1 < nodes.Length; i++)
        {
            if (nodes[i].Name.LocalName != "key" || nodes[i].Value != "CFBundleExecutable" || nodes[i + 1].Name.LocalName != "string") continue;
            string value = nodes[i + 1].Value;
            if (value.Length is 0 or > 255 || value is "." or ".." || value.Contains('/') || value.Contains('\\') || value.Any(char.IsControl))
                throw new InvalidDataException("Некорректный CFBundleExecutable.");
            return value;
        }
        throw new InvalidDataException("Info.plist не содержит CFBundleExecutable.");
    }

    private static async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("/usr/bin/codesign") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("macOS codesign не запустился.");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            string text = await error.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Не удалось локально подписать копию Godot для запуска: " + text.Trim());
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
