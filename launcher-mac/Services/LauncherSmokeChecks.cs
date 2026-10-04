using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

public sealed record LauncherSmokeReport(bool Success, IReadOnlyList<string> Checks, string ProfileDirectory,
    string? InputPath, bool SourcePreserved, string? Error = null, string? PreparedMacAppPath = null);

public static class LauncherSmokeChecks
{
    public static async Task<LauncherSmokeReport> RunAsync(string? inputPath = null, string? profileDirectory = null,
        CancellationToken cancellation = default)
    {
        var checks = new List<string>();
        profileDirectory ??= Environment.GetEnvironmentVariable("DUSTOREV_PROFILE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(profileDirectory))
            throw new ArgumentException("Smoke checks require an explicitly isolated profile directory.", nameof(profileDirectory));
        profileDirectory = Path.GetFullPath(profileDirectory);
        bool sourcePreserved = false;
        string? prepared = null;
        try
        {
            var fakePlatform = new RecordingPlatform();
            var service = new LauncherServices(profileDirectory, fakePlatform);
            string fixtures = Path.Combine(profileDirectory, "smoke-fixtures-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixtures);
            string emptyProfile = Path.Combine(fixtures, "empty-startup-profile");
            var emptyService = new LauncherServices(emptyProfile, fakePlatform);
            var unityWindowed = GameLaunchOptions.Arguments(GameEngineKind.Unity, null, null, null);
            Check(unityWindowed.SequenceEqual(new[] { "-screen-fullscreen", "0", "-window-mode", "windowed", "-screen-width", "1280", "-screen-height", "720" }), "Unity games open in a 1280x720 window by default instead of the display's full size");
            Check(GameLaunchOptions.Arguments(GameEngineKind.Godot, GameLaunchOptions.Fullscreen, 1600, 900).SequenceEqual(new[] { "--fullscreen", "--resolution", "1600x900" }) && GameLaunchOptions.Arguments(GameEngineKind.Unity, GameLaunchOptions.GameDefault, 1600, 900).Count == 0, "Godot and game-default window options map to the right command line");
            UltraMode.Display = (1440, 900);
            var (ultraW, ultraH) = UltraMode.RenderSize();
            var ultraUnity = UltraMode.Arguments(GameEngineKind.Unity);
            Check(ultraW < 1440 && ultraH < 900 && ultraW % 2 == 0 && ultraUnity.Contains("-screen-width") && ultraUnity.Contains(ultraW.ToString()) && ultraUnity.Contains("-nolog"),
                "ULTRA renders Unity below the display resolution");
            Check(UltraMode.Arguments(GameEngineKind.Godot).Contains("--disable-vsync"), "ULTRA lifts the Godot vsync cap");
            string ultraDir = Path.Combine(Path.GetTempPath(), "dustore-ultra-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(ultraDir);
            var ultraEnv = new Dictionary<string, string>();
            UltraMode.AddDxvk(ultraEnv, ultraDir);
            string dxvkConf = File.ReadAllText(ultraEnv["DXVK_CONFIG_FILE"]);
            Check(dxvkConf.Contains("dxgi.syncInterval = 0") && dxvkConf.Contains("d3d11.relaxedBarriers = True") && ultraEnv["WINEDEBUG"] == "-all"
                && ultraEnv["ROSETTA_ADVERTISE_AVX"] == "1" && ultraEnv["MVK_CONFIG_FAST_MATH_ENABLED"] == "1", "ULTRA DXVK route drops vsync and safety work");
            var metalEnv = new Dictionary<string, string>(); var metalConfig = new List<string>();
            UltraMode.AddMetal(metalEnv, metalConfig);
            Check(metalConfig.Contains("d3d11.preferredMaxFrameRate=120") && (!UltraMode.AppleSilicon || metalEnv.ContainsKey("DXMT_METALFX_SPATIAL_SWAPCHAIN")),
                "ULTRA Metal route allows 120 FPS and MetalFX on Apple silicon");
            Directory.Delete(ultraDir, true);
            Check(!Directory.Exists(emptyProfile), "service construction performs no profile filesystem writes before the GUI");
            Check((await emptyService.LoadLibraryAsync(cancellation).ConfigureAwait(false)).Count == 0
                && Directory.Exists(emptyProfile), "an empty profile is created and loaded during async initialization");
            string blockedProfile = Path.Combine(fixtures, "blocked-profile-file");
            File.WriteAllText(blockedProfile, "Existing file must stay unchanged.");
            string blockedHash = HashSource(blockedProfile);
            var blockedService = new LauncherServices(blockedProfile, fakePlatform);
            await MustThrowAsync<IOException>(() => blockedService.LoadLibraryAsync(cancellation));
            Check(HashSource(blockedProfile) == blockedHash, "profile file collisions become async initialization errors without changing the file");
            var blockedParentService = new LauncherServices(Path.Combine(blockedProfile, "child"), fakePlatform);
            await MustThrowAsync<IOException>(() => blockedParentService.LoadLibraryAsync(cancellation));
            Check(HashSource(blockedProfile) == blockedHash, "an unwritable profile under a file parent is rejected after construction without changing data");
            var blankProfileService = new LauncherServices("", fakePlatform);
            await MustThrowAsync<InvalidDataException>(() => blankProfileService.LoadLibraryAsync(cancellation));
            Check(blankProfileService.DataDirectory == "" && blankProfileService.OutputDirectory == "", "a blank profile override surfaces an async error without falling back to another profile");
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                string restrictedParent = Path.Combine(fixtures, "permission-restricted-profile");
                Directory.CreateDirectory(restrictedParent);
                UnixFileMode prior = File.GetUnixFileMode(restrictedParent);
                try
                {
                    File.SetUnixFileMode(restrictedParent, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                    var restrictedService = new LauncherServices(Path.Combine(restrictedParent, "child"), fakePlatform);
                    await MustThrowAsync<IOException>(() => restrictedService.LoadLibraryAsync(cancellation));
                    Check(!Directory.Exists(Path.Combine(restrictedParent, "child")), "a non-writable profile parent produces a recoverable async error before any game data is changed");
                }
                finally { File.SetUnixFileMode(restrictedParent, prior); }
            }
            string portable = Path.Combine(fixtures, "Portable.love");
            WriteZip(portable, ("main.lua", Encoding.UTF8.GetBytes("function love.draw() love.graphics.print('DUSTORE', 20, 20) end"), 0));
            string selected = inputPath is null ? portable : Path.GetFullPath(inputPath);
            string before = HashSource(selected);

            var entry = await service.AddGameAsync(selected, cancellation).ConfigureAwait(false);
            Check(entry.SourcePath == selected, "library preserves the original source path");
            var duplicate = await service.AddGameAsync(selected, cancellation).ConfigureAwait(false);
            Check(duplicate.Id == entry.Id, "adding the same source preserves its library ID");
            var reloaded = await new LauncherServices(profileDirectory, fakePlatform).LoadLibraryAsync(cancellation).ConfigureAwait(false);
            Check(reloaded.Any(e => e.Id == entry.Id && e.SourcePath == selected), "library IDs and source paths survive reload");
            Check(File.Exists(service.LibraryPath), "library is saved in the isolated profile");

            var plan = await service.InspectAsync(selected, TargetPlatform.Windows, "x64", cancellation).ConfigureAwait(false);
            Check(plan.InputPath == selected, "pure conversion core analyzes the selected input");
            Check(!string.IsNullOrWhiteSpace(plan.Engine) && plan.Warnings is not null, "conversion analysis exposes engine and compatibility warnings");
            if (inputPath is not null) Check(plan.CanConvert && plan.Method == "godot", "PODIEZD Mac package supports the real reverse Windows route");
            prepared = entry.PreparedMacAppPath;
            if (inputPath is not null)
                Check(prepared is not null && Directory.Exists(prepared) && File.Exists(Path.Combine(prepared, "Contents", "Info.plist")), "Mac package is imported into a separate owned app bundle");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await MustThrowAsync<OperationCanceledException>(() => service.InspectAsync(portable, TargetPlatform.MacOS, "arm64", cancelled.Token));
            checks.Add("analysis respects cancellation before work starts");
            await MustThrowAsync<OperationCanceledException>(() => service.AddGameAsync(portable, cancelled.Token));
            checks.Add("cancelled library mutations do not write data");
            string output = service.NewOutputPath("../A:B\\C", TargetPlatform.MacOS);
            Check(Path.GetDirectoryName(output) == service.OutputDirectory && Path.GetExtension(output) == ".zip", "default output is a separate ZIP within the profile");
            Check(service.NewOutputPath("Game", TargetPlatform.Windows) != service.NewOutputPath("Game", TargetPlatform.Windows), "output names avoid collisions");

            string appZip = Path.Combine(fixtures, "Tiny-macOS.zip");
            WriteZip(appZip,
                ("Tiny.app/Contents/Info.plist", Encoding.UTF8.GetBytes("<?xml version='1.0'?><plist version='1.0'><dict><key>CFBundleExecutable</key><string>Tiny</string></dict></plist>"), 0x81A4),
                ("Tiny.app/Contents/MacOS/Tiny", new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 7, 0, 0, 1 }, 0x81ED));
            byte[] quarantine = Encoding.UTF8.GetBytes("0083;00000001;DUSTORE-smoke;");
            if (OperatingSystem.IsMacOS()) MacQuarantine.Write(appZip, quarantine);
            string archiveHash = HashSource(appZip);
            var imported = await service.AddGameAsync(appZip, cancellation).ConfigureAwait(false);
            Check(imported.CanLaunchOnMac && imported.PreparedMacAppPath is not null, "Mac ZIP import produces a launchable library shortcut");
            Check(HashSource(appZip) == archiveHash, "ZIP import preserves the source archive bytes");
            Check(imported.PreparedMacAppPath!.StartsWith(service.ManagedGamesDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal), "prepared apps are stored only in the managed games directory");
            if (OperatingSystem.IsMacOS())
                Check(MacQuarantine.Read(imported.PreparedMacAppPath!)?.SequenceEqual(quarantine) == true
                    && MacQuarantine.Read(appZip)?.SequenceEqual(quarantine) == true, "Mac import preserves quarantine on the copy and source archive");
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                Check((File.GetUnixFileMode(Path.Combine(imported.PreparedMacAppPath!, "Contents", "MacOS", "Tiny")) & UnixFileMode.UserExecute) != 0, "import preserves executable Unix permission");
            await service.LaunchAsync(imported, cancellation).ConfigureAwait(false);
            Check(fakePlatform.OpenedApps.SequenceEqual(new[] { imported.PreparedMacAppPath! }), "launch dispatches the exact prepared app through the platform adapter");
            var played = (await service.LoadLibraryAsync(cancellation).ConfigureAwait(false)).Single(e => e.Id == imported.Id);
            Check(played.LastPlayedUtc is not null, "successful launch records the real launch request time");
            await service.RevealAsync(appZip, cancellation).ConfigureAwait(false);
            Check(fakePlatform.RevealedPaths.SequenceEqual(new[] { appZip }), "Finder reveal uses the exact source path");
            await service.OpenUrlAsync("https://dustore.ru/explore", cancellation).ConfigureAwait(false);
            Check(fakePlatform.OpenedUrls.Single() == "https://dustore.ru/explore", "store URL is passed to the native platform adapter");
            await MustThrowAsync<ArgumentException>(() => service.OpenUrlAsync("file:///etc/passwd", cancellation));
            checks.Add("external URLs are limited to HTTP and HTTPS");

            string pe = Path.Combine(fixtures, "Windows.exe");
            File.WriteAllBytes(pe, new byte[] { 0x4D, 0x5A, 0, 0 });
            var windowsEntry = await service.AddGameAsync(pe, cancellation).ConfigureAwait(false);
            await MustThrowAsync<InvalidOperationException>(() => service.LaunchAsync(windowsEntry, cancellation));
            Check(fakePlatform.OpenedApps.Count == 1, "Windows EXE is not executed as a Mac app without conversion");

            string traversal = Path.Combine(fixtures, "Traversal.zip");
            WriteZip(traversal, ("Tiny.app/Contents/Info.plist", [1], 0x81A4), ("Tiny.app/Contents/MacOS/Tiny", [1], 0x81ED), ("../escape", [1], 0x81A4));
            await MustThrowAsync<InvalidDataException>(() => service.AddGameAsync(traversal, cancellation));
            Check(!File.Exists(Path.Combine(profileDirectory, "escape")), "archive traversal is rejected before extraction");
            string linkEscape = Path.Combine(fixtures, "LinkEscape.zip");
            WriteZip(linkEscape, ("Tiny.app/Contents/Info.plist", [1], 0x81A4), ("Tiny.app/Contents/MacOS/Tiny", [1], 0x81ED),
                ("Tiny.app/Contents/Resources/outside", Encoding.UTF8.GetBytes("../../../../escape"), 0xA1FF));
            await MustThrowAsync<InvalidDataException>(() => service.AddGameAsync(linkEscape, cancellation));
            checks.Add("escaping symbolic links are rejected before extraction");
            string collision = Path.Combine(fixtures, "CaseCollision.zip");
            WriteZip(collision, ("Tiny.app/Contents/Info.plist", [1], 0x81A4), ("Tiny.app/Contents/MacOS/Tiny", [1], 0x81ED),
                ("Tiny.app/Contents/Resources/A", [1], 0x81A4), ("Tiny.app/Contents/Resources/a", [2], 0x81A4));
            await MustThrowAsync<InvalidDataException>(() => service.AddGameAsync(collision, cancellation));
            checks.Add("case-insensitive archive collisions are rejected");

            await service.RemoveGameAsync(imported.Id, cancellation).ConfigureAwait(false);
            Check(File.Exists(appZip) && Directory.Exists(imported.PreparedMacAppPath), "removing a shortcut never deletes game files");
            await MustThrowAsync<KeyNotFoundException>(() => service.LaunchAsync(imported, cancellation));
            Check(fakePlatform.OpenedApps.Count == 1, "removed shortcuts cannot launch a stale game path");
            await service.RemoveGameAsync(windowsEntry.Id, cancellation).ConfigureAwait(false);
            Check(!(await new LauncherServices(profileDirectory).LoadLibraryAsync(cancellation).ConfigureAwait(false)).Any(e => e.Id == windowsEntry.Id || e.Id == imported.Id), "removals survive a profile reload");

            string corruptProfile = Path.Combine(fixtures, "corrupt-profile");
            Directory.CreateDirectory(corruptProfile);
            string corruptFile = Path.Combine(corruptProfile, "library.json");
            File.WriteAllText(corruptFile, "{ malformed library");
            string corruptHash = HashSource(corruptFile);
            await MustThrowAsync<InvalidDataException>(() => new LauncherServices(corruptProfile).AddGameAsync(portable, cancellation));
            Check(HashSource(corruptFile) == corruptHash, "a corrupt library is preserved rather than overwritten");
            sourcePreserved = HashSource(selected) == before;
            Check(sourcePreserved, "original game input remains byte-for-byte unchanged");
            Check(fakePlatform.OpenedApps.Count == 1, "smoke checks never execute a game; launch requests use the recording adapter");
            return new LauncherSmokeReport(true, checks, profileDirectory, inputPath, sourcePreserved, PreparedMacAppPath: prepared);

            void Check(bool condition, string description)
            {
                if (!condition) throw new InvalidOperationException("Smoke check failed: " + description);
                checks.Add(description);
            }
        }
        catch (Exception ex)
        {
            return new LauncherSmokeReport(false, checks, profileDirectory, inputPath, sourcePreserved, ex.GetType().Name + ": " + ex.Message, prepared);
        }
    }

    private static async Task MustThrowAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }

    private static void WriteZip(string path, params (string Name, byte[] Bytes, int Mode)[] entries)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Name);
            entry.ExternalAttributes = item.Mode << 16;
            using var output = entry.Open();
            output.Write(item.Bytes);
        }
    }

    private static string HashSource(string path)
    {
        if (File.Exists(path)) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        HashDirectory(path, "");
        return Convert.ToHexString(hash.GetHashAndReset());

        void HashDirectory(string directory, string relative)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                string name = relative + Path.GetFileName(entry);
                hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    hash.AppendData(Encoding.UTF8.GetBytes(new FileInfo(entry).LinkTarget ?? ""));
                else if ((attributes & FileAttributes.Directory) != 0) HashDirectory(entry, name + "/");
                else { using var input = File.OpenRead(entry); hash.AppendData(SHA256.HashData(input)); }
            }
        }
    }

    private sealed class RecordingPlatform : IPlatformLauncher
    {
        public bool IsMacOS => true;
        public List<string> OpenedApps { get; } = [];
        public List<string> RevealedPaths { get; } = [];
        public List<string> OpenedUrls { get; } = [];
        public Task OpenAppAsync(string path, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); OpenedApps.Add(path); return Task.CompletedTask; }
        public Task RevealAsync(string path, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); RevealedPaths.Add(path); return Task.CompletedTask; }
        public Task OpenUrlAsync(string url, CancellationToken cancellation = default) { cancellation.ThrowIfCancellationRequested(); OpenedUrls.Add(url); return Task.CompletedTask; }
    }
}
