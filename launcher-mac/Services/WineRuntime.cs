using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DustoreLauncherV.Mac.Services;

public sealed record WineProgress(string Stage, double Fraction, long ReceivedBytes, long TotalBytes);

/// <summary>
/// Installs Wine for Windows games packaged by eX. One pinned build (Gcenx's macOS build of
/// Wine stable, LGPL) is downloaded from its GitHub release, verified against the published
/// SHA-256, unpacked with the system tar and linked as WineRuntime/current, which the eX wrapper
/// script searches first. No administrator rights are needed; nothing outside the user's
/// Application Support folder is changed.
/// </summary>
public static class WineRuntime
{
    public const string Version = "11.0_1";
    public const string DisplayVersion = "Wine 11.0";
    public const string ArchiveName = "wine-stable-11.0_1-osx64.tar.xz";
    public const string Url = "https://github.com/Gcenx/macOS_Wine_builds/releases/download/11.0_1/wine-stable-11.0_1-osx64.tar.xz";
    public const string Sha256 = "b50dc50ec7f41d58b115a6b685d4d1315ba3c797bd3aa0f49213f2703cb82388";
    public const long ArchiveBytes = 185303032;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Shared with the eX wrapper script: $HOME/Library/Application Support/DustoreX/WineRuntime.</summary>
    public static string Root => Environment.GetEnvironmentVariable("DUSTOREX_WINE_ROOT") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "DustoreX", "WineRuntime");
    public static string CurrentLink => Path.Combine(Root, "current");
    private static string Marker => Path.Combine(Root, "installed.json");

    public static string? WineBinary
    {
        get
        {
            foreach (string name in new[] { "wine", "wine64" })
            {
                string path = Path.Combine(CurrentLink, "bin", name);
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }

    public static bool IsInstalled => WineBinary is not null && File.Exists(Marker);
    public static bool IsAppleSilicon => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>Wine for macOS is x86_64; Apple Silicon runs it through Rosetta 2.</summary>
    public static async Task<bool> RosettaReadyAsync(CancellationToken cancellation = default)
    {
        if (!IsAppleSilicon) return true;
        var (code, _) = await RunAsync("/usr/bin/arch", new[] { "-x86_64", "/usr/bin/true" }, null, TimeSpan.FromSeconds(20), cancellation);
        return code == 0;
    }

    public static async Task<string> InstallAsync(IProgress<WineProgress>? progress, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Wine устанавливается только на macOS.");
        await Gate.WaitAsync(cancellation);
        try
        {
            if (IsInstalled) return WineBinary!;
            if (!await RosettaReadyAsync(cancellation))
                throw new InvalidOperationException("Wine для Mac работает через Rosetta 2, а она не установлена. Откройте Терминал и выполните: softwareupdate --install-rosetta — затем повторите.");
            Directory.CreateDirectory(Root);
            string archive = Path.Combine(Root, ArchiveName + ".partial");
            string staging = Path.Combine(Root, "staging-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadAsync(archive, progress, cancellation);
                progress?.Report(new WineProgress("Проверяю контрольную сумму…", 1, ArchiveBytes, ArchiveBytes));
                string actual;
                await using (var file = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellation)).ToLowerInvariant();
                if (actual != Sha256) throw new InvalidDataException("Скачанный Wine не совпал с опубликованной контрольной суммой. Файл удалён, ничего не установлено.");

                progress?.Report(new WineProgress("Распаковываю Wine…", 1, ArchiveBytes, ArchiveBytes));
                Directory.CreateDirectory(staging);
                var (code, output) = await RunAsync("/usr/bin/tar", new[] { "-xJf", archive, "-C", staging }, null, TimeSpan.FromMinutes(10), cancellation);
                if (code != 0) throw new IOException("Не удалось распаковать Wine: " + output.Trim());

                // Builds keep Wine inside "Wine Stable.app/Contents/Resources/wine"; find bin/wine wherever it is.
                string? bin = Directory.EnumerateFiles(staging, "wine*", SearchOption.AllDirectories)
                    .Where(f => Path.GetFileName(f) is "wine" or "wine64" && Path.GetFileName(Path.GetDirectoryName(f)) == "bin")
                    .OrderBy(f => f.Length).FirstOrDefault();
                if (bin is null) throw new InvalidDataException("В архиве Wine не найден исполняемый файл bin/wine.");
                string wineHome = Path.GetDirectoryName(Path.GetDirectoryName(bin)!)!;

                string versionDir = Path.Combine(Root, "wine-stable-" + Version);
                if (Directory.Exists(versionDir)) Directory.Delete(versionDir, recursive: true);
                Directory.Move(staging, versionDir);
                string target = Path.Combine(versionDir, Path.GetRelativePath(staging, wineHome));
                if (File.Exists(CurrentLink) || Directory.Exists(CurrentLink) || new FileInfo(CurrentLink).LinkTarget is not null) File.Delete(CurrentLink);
                File.CreateSymbolicLink(CurrentLink, target);
                if (WineBinary is null) throw new IOException("Wine распакован, но ссылка current/bin/wine не работает.");

                progress?.Report(new WineProgress("Проверяю запуск Wine…", 1, ArchiveBytes, ArchiveBytes));
                var (versionCode, versionText) = await RunAsync(WineBinary!, new[] { "--version" }, null, TimeSpan.FromMinutes(2), cancellation);
                if (versionCode != 0) throw new IOException("Wine установлен, но не запускается: " + versionText.Trim());
                await File.WriteAllTextAsync(Marker, JsonSerializer.Serialize(new
                {
                    version = Version, wine = versionText.Trim(), url = Url, sha256 = Sha256, installedAtUtc = DateTimeOffset.UtcNow
                }), cancellation);
                return WineBinary!;
            }
            finally
            {
                try { File.Delete(archive); } catch (IOException) { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            }
        }
        finally { Gate.Release(); }
    }

    public static async Task RemoveAsync(CancellationToken cancellation = default)
    {
        await Gate.WaitAsync(cancellation);
        try
        {
            if (new FileInfo(CurrentLink).LinkTarget is not null || File.Exists(CurrentLink)) File.Delete(CurrentLink);
            File.Delete(Marker);
            foreach (string dir in Directory.Exists(Root) ? Directory.GetDirectories(Root, "wine-stable-*") : Array.Empty<string>())
                Directory.Delete(dir, recursive: true);
        }
        finally { Gate.Release(); }
    }

    public static async Task<string> InstalledVersionTextAsync()
    {
        try { using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Marker)); return doc.RootElement.GetProperty("wine").GetString() ?? DisplayVersion; }
        catch (Exception) { return DisplayVersion; }
    }

    private static async Task DownloadAsync(string path, IProgress<WineProgress>? progress, CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DustoreLauncherV/5.2.7");
        using var response = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? ArchiveBytes;
        if (total != ArchiveBytes) throw new InvalidDataException("Размер архива Wine не совпадает с опубликованным.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        byte[] buffer = new byte[1 << 20];
        long received = 0; var lastReport = Stopwatch.StartNew();
        for (int read; (read = await input.ReadAsync(buffer, cancellation)) > 0;)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            received += read;
            if (received > ArchiveBytes) throw new InvalidDataException("Архив Wine больше опубликованного размера.");
            if (lastReport.ElapsedMilliseconds > 250) { progress?.Report(new WineProgress("Скачиваю " + DisplayVersion + "…", (double)received / total, received, total)); lastReport.Restart(); }
        }
        if (received != ArchiveBytes) throw new InvalidDataException("Архив Wine скачан не полностью.");
    }

    internal static async Task<(int Code, string Output)> RunAsync(string tool, IEnumerable<string> arguments, IDictionary<string, string>? environment,
        TimeSpan timeout, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить " + tool);
        process.StandardInput.Close(); // Wine must never wait for console input.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        var output = new System.Text.StringBuilder();
        // Wine starts wineserver, which inherits the pipes and outlives wine itself: read as data
        // arrives and never wait for end-of-file without a bound.
        var readers = Task.WhenAll(Pump(process.StandardOutput, output, limit.Token), Pump(process.StandardError, output, limit.Token));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{Path.GetFileName(tool)} не ответил за {timeout.TotalSeconds:0} с. Вывод: " + Tail(output));
        }
        await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None));
        lock (output) return (process.ExitCode, output.ToString());
    }

    private static async Task Pump(StreamReader reader, System.Text.StringBuilder output, CancellationToken cancellation)
    {
        char[] buffer = new char[4096];
        try
        {
            for (int read; (read = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0;)
                lock (output) output.Append(buffer, 0, read);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static string Tail(System.Text.StringBuilder output)
    {
        lock (output) { string text = output.ToString(); return text.Length > 800 ? text[^800..] : text; }
    }

    /// <summary>A packaged eX Wine wrapper declares a local.dustorex.wine.* bundle identifier.</summary>
    public static bool IsWineWrapper(string? app)
    {
        if (app is null) return false;
        string plist = Path.Combine(app, "Contents", "Info.plist");
        try { return File.Exists(plist) && File.ReadAllText(plist).Contains("local.dustorex.wine.", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }
}
