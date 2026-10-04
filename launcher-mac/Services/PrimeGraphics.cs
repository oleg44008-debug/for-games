using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Prime "maximum performance": Direct3D 10/11 straight to Metal through DXMT (v0.80, MIT) on a
/// CrossOver-based Wine (Gcenx's Game Porting Toolkit build, whose winemac exports the
/// macdrv_functions table DXMT needs), with optional MetalFX upscaling, a Metal FPS counter and
/// a frame cap. Apple's D3DMetal inside that archive is never unpacked. Games get their own
/// prefix here, separate from the standard Wine, so the two Wine versions never share one.
/// </summary>
public static class PrimeGraphics
{
    public const string WineArchiveUrl = "https://github.com/Gcenx/game-porting-toolkit/releases/download/Game-Porting-Toolkit-3.0-3/game-porting-toolkit-3.0-3.tar.xz";
    public const string WineSha256 = "d377683937340f914823dbb2e1252b329cbf834ff58907d0293db8cebf0e392e";
    public const long WineBytes = 239200808;
    public const string DxmtUrl = "https://github.com/3Shain/dxmt/releases/download/v0.80/dxmt-v0.80-builtin.tar.gz";
    public const string DxmtSha256 = "8f260e36b5739e68f3bad613381441385c4dc7b85b78ba8de653d5a6a264529d";
    public const long DxmtBytes = 18681669;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Root => Path.Combine(WineRuntime.Root, "prime");
    private static string Marker => Path.Combine(Root, "installed.json");
    private static string WineHome => Path.Combine(Root, "wine");
    public static string WineBinary => Path.Combine(WineHome, "bin", "wine64");
    public static string WineServer => Path.Combine(WineHome, "bin", "wineserver");
    public static bool IsInstalled => File.Exists(Marker) && File.Exists(WineBinary);

    public static async Task InstallAsync(IProgress<WineProgress>? progress, CancellationToken cancellation = default)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Режим максимальной производительности работает на macOS.");
        await Gate.WaitAsync(cancellation);
        try
        {
            if (IsInstalled) return;
            if (!await WineRuntime.RosettaReadyAsync(cancellation))
                throw new InvalidOperationException("Нужна Rosetta 2: откройте Терминал и выполните softwareupdate --install-rosetta.");
            Directory.CreateDirectory(Root);
            string staging = Path.Combine(Root, "staging-" + Guid.NewGuid().ToString("N"));
            string wineArchive = Path.Combine(Root, "wine.tar.xz.partial"), dxmtArchive = Path.Combine(Root, "dxmt.tar.gz.partial");
            try
            {
                await DownloadVerifiedAsync(WineArchiveUrl, WineBytes, WineSha256, wineArchive, "Скачиваю Wine для режима Metal…", progress, cancellation);
                await DownloadVerifiedAsync(DxmtUrl, DxmtBytes, DxmtSha256, dxmtArchive, "Скачиваю DXMT (Direct3D → Metal)…", progress, cancellation);
                progress?.Report(new WineProgress("Распаковываю…", 1, WineBytes, WineBytes));
                Directory.CreateDirectory(staging);
                // Apple's proprietary D3DMetal is not used and is left inside the archive.
                await Tar(new[] { "-xJf", wineArchive, "-C", staging, "--exclude", "*D3DMetal.framework*" }, cancellation);
                string? bin = Directory.EnumerateFiles(staging, "wine64", SearchOption.AllDirectories).FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == "bin");
                if (bin is null) throw new InvalidDataException("В архиве Wine нет bin/wine64.");
                string home = Path.GetDirectoryName(Path.GetDirectoryName(bin)!)!;
                string dxmt = Path.Combine(staging, "dxmt");
                Directory.CreateDirectory(dxmt);
                await Tar(new[] { "-xzf", dxmtArchive, "-C", dxmt }, cancellation);

                progress?.Report(new WineProgress("Подключаю DXMT к Wine…", 1, WineBytes, WineBytes));
                var installed = new List<string>();
                foreach (var (folder, files) in new[]
                {
                    ("x86_64-windows", new[] { "d3d11.dll", "dxgi.dll", "d3d10core.dll", "winemetal.dll" }),
                    ("i386-windows", new[] { "d3d11.dll", "dxgi.dll", "d3d10core.dll", "winemetal.dll" }),
                    ("x86_64-unix", new[] { "winemetal.so" })
                })
                {
                    string target = Path.Combine(home, "lib", "wine", folder);
                    if (!Directory.Exists(target)) continue;
                    foreach (string file in files)
                    {
                        string? source = Directory.EnumerateFiles(dxmt, file, SearchOption.AllDirectories)
                            .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f)) == folder);
                        if (source is null) continue;
                        File.Copy(source, Path.Combine(target, file), overwrite: true);
                        installed.Add(folder + "/" + file);
                    }
                }
                if (!installed.Contains("x86_64-windows/d3d11.dll") || !installed.Contains("x86_64-unix/winemetal.so"))
                    throw new InvalidDataException("DXMT не удалось подключить к Wine: " + string.Join(", ", installed));
                if (Directory.Exists(WineHome)) Directory.Delete(WineHome, true);
                Directory.Move(home, WineHome);
                var (code, version) = await WineRuntime.RunAsync(WineBinary, new[] { "--version" }, null, TimeSpan.FromMinutes(2), cancellation);
                if (code != 0) throw new IOException("Wine для режима Metal не запускается: " + version.Trim());
                await File.WriteAllTextAsync(Marker, JsonSerializer.Serialize(new
                {
                    wine = version.Trim(), wineUrl = WineArchiveUrl, wineSha256 = WineSha256, dxmt = "v0.80", dxmtSha256 = DxmtSha256,
                    libraries = installed, installedAtUtc = DateTimeOffset.UtcNow
                }), cancellation);
            }
            finally
            {
                foreach (string file in new[] { wineArchive, dxmtArchive }) try { File.Delete(file); } catch (IOException) { }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            }
        }
        finally { Gate.Release(); }
    }

    public static IReadOnlyList<string> InstalledLibraries()
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(Marker)); return doc.RootElement.GetProperty("libraries").EnumerateArray().Select(e => e.GetString() ?? "").ToArray(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>Starts an eX Wine package's game on the Metal path. Returns the started wine process.</summary>
    public static async Task<Process> LaunchAsync(string app, GameEntry entry, IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        string gameRoot = Path.Combine(app, "Contents", "Resources", "game");
        var exe = GameExecutableFinder.Find(gameRoot, entry.Name)
            ?? throw new InvalidOperationException("В пакете не найден исполняемый файл игры.");
        string id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Id.ToString("N")))).ToLowerInvariant()[..20];
        string prefix = Path.Combine(WineRuntime.Root, "..", "WineMetal", id);
        prefix = Path.GetFullPath(prefix);
        var environment = Environment(prefix, entry);
        if (!File.Exists(Path.Combine(prefix, "system.reg")))
        {
            Directory.CreateDirectory(prefix);
            await WineRuntime.RunAsync(WineBinary, new[] { "wineboot", "--init" }, environment, TimeSpan.FromMinutes(10), cancellation);
            await WineRuntime.RunAsync(WineServer, new[] { "-w" }, environment, TimeSpan.FromMinutes(10), cancellation);
        }
        await GameLaunchOptions.ReleaseDisplayCaptureAsync(WineBinary, prefix, cancellation);
        // DXMT's unix bridge is also looked up from system32 of the prefix.
        string system32 = Path.Combine(prefix, "drive_c", "windows", "system32");
        string bridge = Path.Combine(WineHome, "lib", "wine", "x86_64-windows", "winemetal.dll");
        if (Directory.Exists(system32) && File.Exists(bridge)) File.Copy(bridge, Path.Combine(system32, "winemetal.dll"), overwrite: true);

        string exePath = Path.Combine(gameRoot, exe.Path.Replace('/', Path.DirectorySeparatorChar));
        var start = new ProcessStartInfo(WineBinary)
        {
            UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)!,
            RedirectStandardOutput = false, RedirectStandardError = false
        };
        start.ArgumentList.Add(Path.GetFileName(exePath));
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) start.Environment[key] = value;
        var game = Process.Start(start) ?? throw new IOException("Не удалось запустить Wine.");
        // ULTRA: the Mac must not dim, sleep or nap while the game runs.
        if (entry.Ultra) try { Process.Start("/usr/bin/caffeinate", new[] { "-di", "-w", game.Id.ToString() }); } catch (Exception) { }
        return game;
    }

    public static Task StopAsync(GameEntry entry, CancellationToken cancellation)
    {
        string id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(entry.Id.ToString("N")))).ToLowerInvariant()[..20];
        string prefix = Path.GetFullPath(Path.Combine(WineRuntime.Root, "..", "WineMetal", id));
        return File.Exists(WineServer)
            ? WineRuntime.RunAsync(WineServer, new[] { "-k" }, new Dictionary<string, string> { ["WINEPREFIX"] = prefix }, TimeSpan.FromSeconds(20), cancellation)
            : Task.CompletedTask;
    }

    private static Dictionary<string, string> Environment(string prefix, GameEntry entry)
    {
        var env = new Dictionary<string, string>
        {
            ["WINEPREFIX"] = prefix, ["WINEDEBUG"] = "-all", ["WINEDLLOVERRIDES"] = "mscoree,mshtml=", ["WINEESYNC"] = "1", ["WINEMSYNC"] = "1"
        };
        var config = new List<string>();
        UltraMode.AddMetal(env, config);
        if (entry.FpsLimit is int limit and > 0 && !entry.Ultra) config.Add("d3d11.preferredMaxFrameRate=" + limit);
        if (config.Count > 0) env["DXMT_CONFIG"] = string.Join(";", config) + ";";
        if (entry.ShowFps) env["MTL_HUD_ENABLED"] = "1";
        return env;
    }

    private static async Task Tar(string[] arguments, CancellationToken cancellation)
    {
        var (code, output) = await WineRuntime.RunAsync("/usr/bin/tar", arguments, null, TimeSpan.FromMinutes(15), cancellation);
        if (code != 0) throw new IOException("Не удалось распаковать архив: " + output.Trim());
    }

    private static async Task DownloadVerifiedAsync(string url, long bytes, string sha256, string path, string stage, IProgress<WineProgress>? progress, CancellationToken cancellation)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DustoreLauncherV-Prime");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        await using (var input = await response.Content.ReadAsStreamAsync(cancellation))
        await using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
        {
            byte[] buffer = new byte[1 << 20]; long received = 0; var tick = Stopwatch.StartNew();
            for (int read; (read = await input.ReadAsync(buffer, cancellation)) > 0;)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                received += read;
                if (received > bytes) throw new InvalidDataException("Архив больше опубликованного размера.");
                if (tick.ElapsedMilliseconds > 250) { progress?.Report(new WineProgress(stage, (double)received / bytes, received, bytes)); tick.Restart(); }
            }
            if (received != bytes) throw new InvalidDataException("Архив скачан не полностью.");
        }
        await using var check = File.OpenRead(path);
        if (Convert.ToHexString(await SHA256.HashDataAsync(check, cancellation)).ToLowerInvariant() != sha256)
            throw new InvalidDataException("Архив не совпал с опубликованной контрольной суммой. Ничего не установлено.");
    }
}
