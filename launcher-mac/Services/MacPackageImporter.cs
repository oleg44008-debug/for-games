using System.IO.Compression;
using System.Text;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

/// <summary>Imports into a new owned directory. The archive and all original game files stay unchanged.</summary>
internal static class MacPackageImporter
{
    private sealed record Item(ZipArchiveEntry Entry, string Name, bool Directory, bool Symlink, int Mode, string? LinkTarget);

    public static string? ImportIfMacApp(string archivePath, string managedDirectory, CancellationToken cancellation)
    {
        using var input = File.OpenRead(archivePath);
        if (input.Length > SafetyLimits.MaxArchiveBytes) throw new InvalidDataException("ZIP превышает допустимый размер.");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count > SafetyLimits.MaxEntries) throw new InvalidDataException("В ZIP слишком много файлов.");
        // ZIPs without a Mac bundle are simply library sources and are never extracted automatically.
        string[] roots = archive.Entries.Select(e => AppRoot(e.FullName)).Where(s => s is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
        if (roots.Length == 0) return null;
        if (roots.Length != 1) throw new InvalidDataException("В ZIP должно быть одно приложение .app.");

        string appRoot = NormalizeName(roots[0]).TrimEnd('/') + "/";
        var items = new List<Item>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string name = NormalizeName(entry.FullName);
            bool directory = name.EndsWith('/');
            int unixMode = (int)((uint)entry.ExternalAttributes >> 16);
            int type = unixMode & 0xF000;
            bool symlink = type == 0xA000;
            if (type is not 0 and not 0x8000 and not 0x4000 and not 0xA000)
                throw new InvalidDataException("В ZIP найден специальный Unix-файл.");
            if (!names.Add(name.TrimEnd('/'))) throw new InvalidDataException("В ZIP повторяются имена файлов.");
            total = checked(total + entry.Length);
            if (total > SafetyLimits.MaxUncompressedBytes) throw new InvalidDataException("Распакованный ZIP превышает допустимый размер.");
            string? linkTarget = null;
            if (symlink)
            {
                if (directory || entry.Length > 4096) throw new InvalidDataException("Некорректная символическая ссылка в ZIP.");
                using var stream = entry.Open();
                using var bytes = new MemoryStream();
                CopyBounded(stream, bytes, entry.Length, cancellation);
                linkTarget = new UTF8Encoding(false, true).GetString(bytes.ToArray());
                ValidateLink(name, linkTarget, appRoot);
            }
            items.Add(new Item(entry, name, directory, symlink, unixMode, linkTarget));
        }
        var appItems = items.Where(i => i.Name.StartsWith(appRoot, StringComparison.Ordinal)).ToArray();
        if (!appItems.Any(i => i.Name == appRoot + "Contents/Info.plist" && !i.Directory && !i.Symlink)
            || !appItems.Any(i => i.Name.StartsWith(appRoot + "Contents/MacOS/", StringComparison.Ordinal) && !i.Directory && !i.Symlink))
            throw new InvalidDataException("В .app отсутствует Info.plist или исполняемый файл Contents/MacOS.");
        var byName = items.ToDictionary(i => i.Name.TrimEnd('/'), StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            // A regular file or link may never act as the parent of another entry.
            string parent = item.Name.TrimEnd('/');
            while ((parent = Parent(parent)).Length > 0)
                if (byName.TryGetValue(parent, out var ancestor) && !ancestor.Directory)
                    throw new InvalidDataException("Файл или ссылка использованы как папка в ZIP.");
        }

        managedDirectory = Path.GetFullPath(managedDirectory);
        RejectReparseAncestors(managedDirectory);
        Directory.CreateDirectory(managedDirectory);
        string staging = Path.Combine(managedDirectory, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var item in appItems.Where(i => !i.Symlink))
            {
                cancellation.ThrowIfCancellationRequested();
                string destination = Destination(staging, item.Name);
                if (item.Directory) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = item.Entry.Open();
                using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(source, target, item.Entry.Length, cancellation);
                if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                {
                    int permissions = item.Mode & 0x1FF;
                    if (permissions == 0) permissions = item.Name.StartsWith(appRoot + "Contents/MacOS/", StringComparison.Ordinal) ? 0x1ED : 0x1A4;
                    File.SetUnixFileMode(destination, (UnixFileMode)permissions);
                }
            }
            foreach (var item in appItems.Where(i => i.Symlink))
            {
                cancellation.ThrowIfCancellationRequested();
                if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
                    throw new PlatformNotSupportedException("Mac ZIP с символическими ссылками нужно распаковывать на macOS.");
                string destination = Destination(staging, item.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.CreateSymbolicLink(destination, item.LinkTarget!);
            }
            // Set directory modes after writing their children, preserving traversal permission.
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                foreach (var item in appItems.Where(i => i.Directory && (i.Mode & 0x1FF) != 0).OrderByDescending(i => i.Name.Length))
                    File.SetUnixFileMode(Destination(staging, item.Name), (UnixFileMode)(item.Mode & 0x1FF));
            string app = Destination(staging, appRoot.TrimEnd('/'));
            if (!Directory.Exists(app)) throw new InvalidDataException("Не удалось распаковать приложение .app.");
            return app;
        }
        catch
        {
            // Only this newly created, validated child is removed, never a source or an older package.
            string verified = Path.GetFullPath(staging);
            string allowed = managedDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!verified.StartsWith(allowed, StringComparison.Ordinal) || Path.GetFileName(verified).Length != 40)
                throw new IOException("Нельзя удалить неизвестную папку распаковки.");
            Directory.Delete(verified, recursive: true);
            throw;
        }
    }

    private static string NormalizeName(string name)
    {
        if (name.Length == 0 || name.Length > 2048 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')
            || name.Any(c => char.IsControl(c))) throw new InvalidDataException("Небезопасное имя файла в ZIP.");
        string[] parts = name.TrimEnd('/').Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new InvalidDataException("Небезопасный путь в ZIP.");
        return name.Normalize(NormalizationForm.FormC);
    }

    private static string? AppRoot(string name)
    {
        int end = name.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return null;
        return name[..(end + 4)];
    }

    private static string Destination(string root, string relative)
    {
        string result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Путь ZIP выходит за папку распаковки.");
        return result;
    }

    private static void ValidateLink(string name, string target, string root)
    {
        if (target.Length == 0 || target.StartsWith('/') || target.Contains('\\') || target.Contains(':') || target.Any(c => char.IsControl(c)))
            throw new InvalidDataException("Небезопасная символическая ссылка в ZIP.");
        var parts = Parent(name).Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string part in target.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Ссылка выходит за .app."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        string resolved = string.Join('/', parts).Normalize(NormalizationForm.FormC);
        if (!name.StartsWith(root, StringComparison.Ordinal) || !resolved.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidDataException("Ссылка выходит за .app.");
    }

    private static string Parent(string path)
    {
        int separator = path.LastIndexOf('/');
        return separator < 0 ? "" : path[..separator];
    }

    private static void RejectReparseAncestors(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Папка библиотеки содержит символическую ссылку.");
            current = current.Parent;
        }
    }

    private static void CopyBounded(Stream input, Stream output, long expected, CancellationToken cancellation)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            cancellation.ThrowIfCancellationRequested();
            total = checked(total + read);
            if (total > expected || total > SafetyLimits.MaxUncompressedBytes) throw new InvalidDataException("Некорректный размер файла в ZIP.");
            output.Write(buffer, 0, read);
        }
        if (total != expected) throw new InvalidDataException("Размер файла не совпадает с ZIP.");
    }
}
