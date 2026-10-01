using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DustoreX.AutoConverter;

public static class RuntimeCatalog
{
    private const long DownloadCeiling = 3L * 1024 * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromMinutes(40) };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    public static string CacheDirectory => Environment.GetEnvironmentVariable("DUSTOREX_RUNTIME_CACHE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DustoreX", "AutoConverter", "runtimes");
    public static string DefaultVersion(string method) => method switch { "love" => "11.5", "nw" => "0.117.0", "renpy" => "8.5.3", _ => throw new ArgumentException("Версия этого движка должна быть установлена по сборке.") };
    public static string RuntimeFileName(string method, string version, TargetPlatform target, string architecture)
    {
        ValidateVersion(version);
        if (!Enum.IsDefined(target)) throw new ArgumentException("Неизвестная целевая платформа.");
        if (architecture is not ("x64" or "arm64")) throw new ArgumentException("Архитектура должна быть x64 или arm64.");
        // Architecture selection concerns macOS; Windows packages always use x64.
        if (target == TargetPlatform.Windows) architecture = "x64";
        return method switch
        {
            "love" => $"love-{version}-{(target == TargetPlatform.Windows ? "win64" : "macos")}.zip",
            "nw" => $"nwjs-v{version}-{(target == TargetPlatform.Windows ? "win-x64" : "osx-" + architecture)}.zip",
            "renpy" => $"renpy-{version}-sdk.zip",
            "godot" => $"Godot_v{GodotRelease(version)}-stable_export_templates.tpz",
            _ => throw new ArgumentException("Неизвестный движок.")
        };
    }
    public static async Task<string> ResolveAsync(string method, string version, TargetPlatform target, string architecture, IProgress<string>? progress = null, CancellationToken cancellation = default, string? cacheDirectory = null)
    {
        string file = RuntimeFileName(method, version, target, architecture), url = RuntimeUrl(method, version, file);
        string cache = Path.GetFullPath(cacheDirectory ?? CacheDirectory);
        SafeData.RejectReparseAncestors(cache); Directory.CreateDirectory(cache); SafeData.RejectReparseAncestors(cache);
        string destination = Path.Combine(cache, file);
        var gate = Locks.GetOrAdd(destination, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(cancellation);
        try
        {
            SafeData.RejectReparseAncestors(destination); SafeData.RejectReparseAncestors(destination + ".lock.json");
            if (VerifyCached(destination, url, version))
            {
                await EnsureGodotNoticesAsync(method, version, cache, cancellation);
                progress?.Report("Использую движок " + version + " с проверенной контрольной суммой из кэша."); return destination;
            }
            string expected = await ExpectedHashAsync(method, version, file, cancellation);
            string? seed = FindSeed(file);
            string temp = destination + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                if (seed is not null)
                {
                    SafeData.RejectReparseAncestors(seed);
                    if (new FileInfo(seed).Length > DownloadCeiling) throw new InvalidDataException("Локальный runtime превышает допустимый размер.");
                    cancellation.ThrowIfCancellationRequested();
                    progress?.Report("Подготавливаю локальный пакет движка " + version + " и проверяю контрольную сумму…"); File.Copy(seed, temp, false);
                }
                else { progress?.Report("Скачиваю официальный движок " + version + "…"); await DownloadAsync(url, temp, progress, cancellation); }
                string hash = await HashAsync(temp, cancellation);
                string actual = expected.Length == 128 ? await HashAsync(temp, cancellation, sha512: true) : hash;
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма движка не совпала с официальной. Пакет не используется.");
                await EnsureGodotNoticesAsync(method, version, cache, cancellation);
                SafeData.RejectReparseAncestors(destination); SafeData.RejectReparseAncestors(destination + ".lock.json");
                File.Move(temp, destination, overwrite: true);
                await AtomicTextAsync(destination + ".lock.json", JsonSerializer.Serialize(new
                {
                    schemaVersion = 2, method, file, url, sha256 = hash, officialChecksum = expected.ToLowerInvariant(), officialChecksumVerified = true,
                    verificationKind = method == "love" ? "PinnedReleaseSha256" : "PublishedChecksumViaHttps",
                    version, verifiedUtc = DateTimeOffset.UtcNow
                }), cancellation);
                return destination;
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }
    public static bool VerifyCached(string path) => VerifyCached(path, null, null);
    private static bool VerifyCached(string path, string? expectedUrl, string? expectedVersion)
    {
        if (!File.Exists(path) || !File.Exists(path + ".lock.json")) return false;
        try
        {
            SafeData.RejectReparseAncestors(path); SafeData.RejectReparseAncestors(path + ".lock.json");
            if (new FileInfo(path).Length is <= 0 or > DownloadCeiling || new FileInfo(path + ".lock.json").Length > 16384) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path + ".lock.json")); var root = doc.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 2 || !root.GetProperty("officialChecksumVerified").GetBoolean()) return false;
            string method = root.GetProperty("method").GetString()!, version = root.GetProperty("version").GetString()!, file = root.GetProperty("file").GetString()!;
            string url = root.GetProperty("url").GetString()!, sha256 = root.GetProperty("sha256").GetString()!, official = root.GetProperty("officialChecksum").GetString()!;
            if (file != Path.GetFileName(path) || url != RuntimeUrl(method, version, file) || (expectedUrl is not null && url != expectedUrl)
                || (expectedVersion is not null && version != expectedVersion) || !Regex.IsMatch(sha256, @"^[a-fA-F0-9]{64}$")
                || !Regex.IsMatch(official, @"^(?:[a-fA-F0-9]{64}|[a-fA-F0-9]{128})$")) return false;
            using var stream = File.OpenRead(path); string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) return false;
            if (official.Length == 128) { stream.Position = 0; actual = Convert.ToHexString(SHA512.HashData(stream)); }
            return actual.Equals(official, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }
    public static string RuntimeUrl(string method, string version, string file)
    {
        ValidateVersion(version);
        bool known = new[] { TargetPlatform.Windows, TargetPlatform.MacOS }.SelectMany(target => new[] { "x64", "arm64" }.Select(arch => RuntimeFileName(method, version, target, arch))).Contains(file, StringComparer.Ordinal);
        if (!known) throw new ArgumentException("Имя файла не соответствует официальному пакету движка и версии.");
        return method switch { "love" => $"https://github.com/love2d/love/releases/download/{version}/{file}", "godot" => $"https://github.com/godotengine/godot-builds/releases/download/{GodotRelease(version)}-stable/{file}", "nw" => $"https://dl.nwjs.io/v{version}/{file}", "renpy" => $"https://www.renpy.org/dl/{version}/{file}", _ => throw new ArgumentException("Неизвестный движок.") };
    }
    public static void ValidateVersion(string version) { if (!Regex.IsMatch(version, @"^[0-9]{1,2}\.[0-9]{1,3}(?:\.[0-9]{1,4})?$", RegexOptions.CultureInvariant)) throw new ArgumentException("Версия должна иметь вид 4.7.1 или 11.5."); }
    private static string GodotRelease(string version) => version.EndsWith(".0", StringComparison.Ordinal) && version.Count(c => c == '.') == 2 ? version[..^2] : version;
    private static string? FindSeed(string file)
    {
        var roots = new List<string> { Path.Combine(AppContext.BaseDirectory, "runtimes") };
        if (Environment.GetEnvironmentVariable("DUSTOREX_RUNTIME_SEED_DIR") is { Length: > 0 } seedDirectory)
            roots.Add(Path.GetFullPath(seedDirectory, AppContext.BaseDirectory));
        if (Environment.GetEnvironmentVariable("DUSTOREX_RUNTIME_SEEDS") is { Length: > 0 } configured)
            roots.AddRange(configured.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.GetFullPath(p, AppContext.BaseDirectory)));
        // Development fixtures are resolved relative to the running build, never a developer's drive.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && directory is not null; i++, directory = directory.Parent) roots.Add(Path.Combine(directory.FullName, "test-assets", "runtimes"));
        return roots.Select(root => Path.GetFullPath(Path.Combine(root, file))).Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }
    private static async Task<string> ExpectedHashAsync(string method, string version, string file, CancellationToken cancellation)
    {
        // Pins identify the exact official 11.5 assets verified during integration.
        if (method == "love" && version == "11.5") return file.Contains("win64", StringComparison.Ordinal) ? "BA6E56BE2685E53C817749C4A5007F51137136FE5A3AB64920508BABC2E74369" : "6795BB3A1656AF6A2FDFE741E150787B481886D3A280327A261A3FDDED586913";
        string? url = method switch { "godot" => $"https://github.com/godotengine/godot-builds/releases/download/{GodotRelease(version)}-stable/SHA512-SUMS.txt", "renpy" => $"https://www.renpy.org/dl/{version}/checksums.txt", "nw" => $"https://dl.nwjs.io/v{version}/SHASUMS256.txt", _ => null };
        if (url is null) throw new InvalidDataException("Для этой версии нет настроенного доверенного SHA-256/SHA-512. Укажите официальный runtime вручную.");
        string text = await ReadOfficialTextAsync(url, 16 * 1024 * 1024, cancellation);
        var matches = Regex.Matches(text, @"(?m)^([a-fA-F0-9]{64}|[a-fA-F0-9]{128})\s+\*?" + Regex.Escape(file) + @"\s*$").Cast<Match>().ToArray();
        if (matches.Length == 0) throw new InvalidDataException("Официальный список не содержит SHA-256/SHA-512 этого runtime. Загрузка остановлена.");
        foreach (var algorithm in matches.GroupBy(m => m.Groups[1].Length))
            if (algorithm.Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().Count() != 1) throw new InvalidDataException("Официальный список содержит конфликтующие контрольные суммы.");
        return matches.OrderByDescending(m => m.Groups[1].Length).First().Groups[1].Value;
    }
    private static async Task EnsureGodotNoticesAsync(string method, string version, string cache, CancellationToken cancellation)
    {
        if (method != "godot") return;
        foreach (string license in new[] { "LICENSE.txt", "COPYRIGHT.txt" })
        {
            string destination = Path.Combine(cache, "godot-" + GodotRelease(version) + "-" + license);
            SafeData.RejectReparseAncestors(destination);
            if (File.Exists(destination) && new FileInfo(destination).Length is > 0 and <= 2 * 1024 * 1024) continue;
            string text = await ReadOfficialTextAsync($"https://raw.githubusercontent.com/godotengine/godot/{GodotRelease(version)}-stable/{license}", 2 * 1024 * 1024, cancellation);
            await AtomicTextAsync(destination, text, cancellation);
        }
    }
    private static async Task AtomicTextAsync(string destination, string text, CancellationToken cancellation)
    {
        SafeData.RejectReparseAncestors(destination);
        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { byte[] bytes = new UTF8Encoding(false).GetBytes(text); await file.WriteAsync(bytes, cancellation); }
            SafeData.RejectReparseAncestors(destination); File.Move(staging, destination, true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    private static async Task<string> ReadOfficialTextAsync(string url, int budget, CancellationToken cancellation)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > budget) throw new InvalidDataException("Недопустимый ответ сервера контрольных сумм.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation); using var buffer = new MemoryStream();
        byte[] chunk = new byte[65536];
        while (true) { int count = await input.ReadAsync(chunk, cancellation); if (count == 0) break; if (buffer.Length + count > budget) throw new InvalidDataException("Ответ сервера превышает размер бюджета."); buffer.Write(chunk, 0, count); }
        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
    }
    private static async Task DownloadAsync(string url, string output, IProgress<string>? progress, CancellationToken cancellation)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation); response.EnsureSuccessStatusCode();
        long? length = response.Content.Headers.ContentLength;
        if (response.RequestMessage?.RequestUri?.Scheme != "https" || length > DownloadCeiling) throw new InvalidDataException("Runtime превышает допустимый размер или получен по недопустимому протоколу.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation); await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true);
        byte[] buffer = new byte[131072]; long total = 0, last = 0;
        while (true) { int count = await input.ReadAsync(buffer, cancellation); if (count == 0) break; total += count; if (total > DownloadCeiling) throw new InvalidDataException("Runtime превышает допустимый размер."); await file.WriteAsync(buffer.AsMemory(0, count), cancellation); if (total - last > 16 * 1024 * 1024) { progress?.Report(length is > 0 ? "Загрузка движка: " + total * 100 / length.Value + "%" : "Загрузка движка: " + total / 1048576 + " МБ"); last = total; } }
        if (total == 0 || (length.HasValue && total != length.Value)) throw new InvalidDataException("Загрузка движка неполная.");
    }
    private static async Task<string> HashAsync(string path, CancellationToken cancellation, bool sha512 = false) { await using var file = File.OpenRead(path); byte[] hash = sha512 ? await SHA512.HashDataAsync(file, cancellation) : await SHA256.HashDataAsync(file, cancellation); return Convert.ToHexString(hash).ToLowerInvariant(); }
}
