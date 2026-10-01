using System.Runtime.InteropServices;
using System.Text.Json;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

public sealed class LauncherServices
{
    private const int MaximumLibraryEntries = 10000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly SemaphoreSlim _libraryGate = new(1, 1);
    private readonly IPlatformLauncher _platform;
    private readonly Exception? _profileResolutionError;
    private List<GameEntry>? _entries;

    public LauncherServices(string? profileDirectory = null, IPlatformLauncher? platform = null)
    {
        _platform = platform ?? new PlatformLauncher();
        try
        {
            string? selectedProfile = profileDirectory ?? Environment.GetEnvironmentVariable("DUSTOREV_PROFILE_DIRECTORY");
            if (selectedProfile is null)
            {
                string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrWhiteSpace(applicationData)) throw new ArgumentException("Система не предоставила папку Application Support для профиля.");
                selectedProfile = Path.Combine(applicationData, "DUSTORE Launcher V");
            }
            if (string.IsNullOrWhiteSpace(selectedProfile)) throw new ArgumentException("Путь профиля не задан.");
            DataDirectory = Path.GetFullPath(selectedProfile);
            OutputDirectory = Path.Combine(DataDirectory, "Converted");
            ManagedGamesDirectory = Path.Combine(DataDirectory, "Managed Games");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // The window can still open and display the profile error during async initialization.
            // A malformed override never falls back to another profile or writes into the working directory.
            _profileResolutionError = error;
            DataDirectory = OutputDirectory = ManagedGamesDirectory = "";
        }
    }

    public string DataDirectory { get; }
    public string OutputDirectory { get; }
    public string ManagedGamesDirectory { get; }
    public string LibraryPath => Path.Combine(DataDirectory, "library.json");
    public string HostArchitecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public async Task<IReadOnlyList<GameEntry>> LoadLibraryAsync(CancellationToken cancellation = default)
    {
        await _libraryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try { await EnsureLoadedAsync(cancellation).ConfigureAwait(false); return _entries!.ToArray(); }
        finally { _libraryGate.Release(); }
    }

    public async Task<GameEntry> AddGameAsync(string sourcePath, CancellationToken cancellation = default)
    {
        sourcePath = ExistingPath(sourcePath);
        string name = Path.GetFileNameWithoutExtension(sourcePath.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name)) name = "Игра";
        await _libraryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellation).ConfigureAwait(false);
            var duplicate = _entries!.FirstOrDefault(e => PathEquals(e.SourcePath, sourcePath));
            if (duplicate is not null) return duplicate;
            if (_entries!.Count >= MaximumLibraryEntries) throw new InvalidDataException("В библиотеке слишком много записей.");
            var entry = new GameEntry(Guid.NewGuid(), name, sourcePath, DateTimeOffset.UtcNow);
            if (File.Exists(sourcePath) && Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string? app = await Task.Run(() => MacPackageImporter.ImportIfMacApp(sourcePath,
                    Path.Combine(ManagedGamesDirectory, entry.Id.ToString("N")), cancellation), cancellation).ConfigureAwait(false);
                if (app is not null)
                {
                    MacQuarantine.Preserve(sourcePath, app);
                    await MacLocalSigner.SignOwnedGodotIfNeededAsync(app, ManagedGamesDirectory, cancellation).ConfigureAwait(false);
                    entry = entry with { PreparedMacAppPath = app };
                }
            }
            else if (Directory.Exists(sourcePath) && !sourcePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                string? app = FindSingleApp(sourcePath);
                if (app is not null) entry = entry with { PreparedMacAppPath = app };
            }
            _entries.Add(entry);
            try { await SaveAsync(cancellation).ConfigureAwait(false); }
            catch { _entries.Remove(entry); throw; }
            return entry;
        }
        finally { _libraryGate.Release(); }
    }

    public async Task RemoveGameAsync(Guid id, CancellationToken cancellation = default)
    {
        await _libraryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellation).ConfigureAwait(false);
            var previous = _entries!.ToList();
            _entries!.RemoveAll(e => e.Id == id);
            // Removing a library shortcut never deletes the user's game or a prepared package.
            try { await SaveAsync(cancellation).ConfigureAwait(false); }
            catch { _entries = previous; throw; }
        }
        finally { _libraryGate.Release(); }
    }

    public Task<ConversionPlan> InspectAsync(string inputPath, TargetPlatform target, string architecture = "arm64", CancellationToken cancellation = default)
        => Task.Run(() => { cancellation.ThrowIfCancellationRequested(); return ConversionEngine.Inspect(inputPath, target, architecture); }, cancellation);

    public async Task<PackageResult> ConvertAsync(ConversionRequest request, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        EnsureProfileDirectory();
        // The core enforces source preservation, runtime verification, archive limits and separate outputs.
        // Its packaging stage is atomic but not interruptible after writing starts: retain a completed result.
        request = request with { OutputPath = request.OutputPath ?? NewOutputPath(request.Name, request.Target) };
        return await Task.Run(() => ConversionEngine.ConvertAsync(request, progress, cancellation), cancellation).ConfigureAwait(false);
    }

    public string NewOutputPath(string gameName, TargetPlatform target)
    {
        ThrowProfileResolutionError();
        string name = string.IsNullOrWhiteSpace(gameName) ? "Game" : gameName.Trim();
        foreach (char value in Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).Distinct()) name = name.Replace(value, '_');
        name = name.Trim('.', ' ');
        if (name.Length == 0) name = "Game";
        if (name.Length > 80) name = name[..80];
        string platform = target == TargetPlatform.Windows ? "windows" : "macos";
        return Path.Combine(OutputDirectory, name + "-" + platform + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
    }

    public async Task<GameEntry> PrepareConvertedMacAsync(Guid id, PackageResult result, CancellationToken cancellation = default)
    {
        EnsureProfileDirectory();
        string output = ExistingPath(result.OutputPath);
        // Validate every archive path and symlink before extracting, including manually selected ZIPs.
        string? app = await Task.Run(() => MacPackageImporter.ImportIfMacApp(output,
            Path.Combine(ManagedGamesDirectory, id.ToString("N")), cancellation), cancellation).ConfigureAwait(false);
        if (app is null) throw new InvalidDataException("В пакете нет единственного приложения .app для macOS.");
        MacQuarantine.Preserve(output, app);
        await MacLocalSigner.SignOwnedGodotIfNeededAsync(app, ManagedGamesDirectory, cancellation).ConfigureAwait(false);
        return await UpdateEntryAsync(id, e => e with { PreparedMacAppPath = app, LastOutputPath = output }, cancellation).ConfigureAwait(false);
    }

    public Task<GameEntry> RegisterPreparedMacAppAsync(Guid id, string appPath, CancellationToken cancellation = default)
    {
        appPath = ExistingPath(appPath);
        if (!Directory.Exists(appPath) || !appPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(appPath, "Contents", "Info.plist")))
            throw new ArgumentException("Выберите папку приложения .app с Contents/Info.plist.", nameof(appPath));
        return UpdateEntryAsync(id, e => e with { PreparedMacAppPath = appPath }, cancellation);
    }

    public async Task LaunchAsync(GameEntry entry, CancellationToken cancellation = default)
    {
        // A stale/removed shortcut must not start a game and then fail while updating its history.
        entry = (await LoadLibraryAsync(cancellation).ConfigureAwait(false)).FirstOrDefault(e => e.Id == entry.Id)
            ?? throw new KeyNotFoundException("Игра больше не находится в библиотеке.");
        string? app = entry.PreparedMacAppPath;
        if (app is null && Directory.Exists(entry.SourcePath) && entry.SourcePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) app = entry.SourcePath;
        if (app is null || !Directory.Exists(app)) throw new InvalidOperationException("Сначала создайте macOS-версию в eX или добавьте готовое приложение .app.");
        await _platform.OpenAppAsync(app, cancellation).ConfigureAwait(false);
        await UpdateEntryAsync(entry.Id, e => e with { LastPlayedUtc = DateTimeOffset.UtcNow }, cancellation).ConfigureAwait(false);
    }

    public Task RevealAsync(string path, CancellationToken cancellation = default)
        => _platform.RevealAsync(ExistingPath(path), cancellation);

    public Task OpenUrlAsync(string url, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
            throw new ArgumentException("Можно открыть только адрес HTTP или HTTPS.", nameof(url));
        return _platform.OpenUrlAsync(uri.AbsoluteUri, cancellation);
    }

    private async Task<GameEntry> UpdateEntryAsync(Guid id, Func<GameEntry, GameEntry> update, CancellationToken cancellation)
    {
        await _libraryGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellation).ConfigureAwait(false);
            int index = _entries!.FindIndex(e => e.Id == id);
            if (index < 0) throw new KeyNotFoundException("Игра больше не находится в библиотеке.");
            var previous = _entries[index];
            var updated = update(previous);
            _entries[index] = updated;
            try { await SaveAsync(cancellation).ConfigureAwait(false); }
            catch { _entries[index] = previous; throw; }
            return updated;
        }
        finally { _libraryGate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellation)
    {
        EnsureProfileDirectory();
        if (_entries is not null) return;
        if (!File.Exists(LibraryPath)) { _entries = []; return; }
        if (new FileInfo(LibraryPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("Файл библиотеки слишком велик.");
        try
        {
            var entries = JsonSerializer.Deserialize<List<GameEntry>>(await File.ReadAllTextAsync(LibraryPath, cancellation).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Пустой файл библиотеки.");
            if (entries.Count > MaximumLibraryEntries || entries.Any(e => e is null || e.Id == Guid.Empty || string.IsNullOrWhiteSpace(e.Name)
                || string.IsNullOrWhiteSpace(e.SourcePath)) || entries.Select(e => e.Id).Distinct().Count() != entries.Count)
                throw new InvalidDataException("Некорректные записи библиотеки.");
            _entries = entries;
        }
        catch (JsonException ex) { throw new InvalidDataException("Файл библиотеки повреждён. Он сохранён для восстановления: " + LibraryPath, ex); }
    }

    private async Task SaveAsync(CancellationToken cancellation)
    {
        EnsureProfileDirectory();
        string temporary = Path.Combine(DataDirectory, "library-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_entries, JsonOptions), cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, LibraryPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string ExistingPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Выберите файл или папку игры.", nameof(path));
        path = Path.GetFullPath(path);
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Файл или папка игры не найдены.", path);
        return path;
    }

    private void EnsureProfileDirectory()
    {
        ThrowProfileResolutionError();
        try { Directory.CreateDirectory(DataDirectory); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException("Не удалось открыть папку профиля лаунчера: " + DataDirectory + ". " + error.Message, error);
        }
    }

    private void ThrowProfileResolutionError()
    {
        if (_profileResolutionError is not null)
            throw new InvalidDataException("Не удалось определить папку профиля лаунчера. Проверьте DUSTOREV_PROFILE_DIRECTORY: " + _profileResolutionError.Message,
                _profileResolutionError);
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string? FindSingleApp(string directory)
    {
        var apps = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(p, "Contents", "Info.plist")))
            .Take(2).ToArray();
        return apps.Length == 1 ? apps[0] : null;
    }
}
