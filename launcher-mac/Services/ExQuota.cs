using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Time the user cannot wind: the Date header of an HTTPS answer from dustore.ru (or
/// google.com), then this process's monotonic stopwatch. The Mac clock is never trusted.
/// Offline, time stands still at the last trusted moment — a new quota day only arrives with
/// the internet, and moving the clock forward or back changes nothing.
/// </summary>
public static class TrustedClock
{
    private static readonly string[] Sources = { "https://dustore.ru/", "https://www.google.com/generate_204" };
    private static DateTimeOffset? _server;
    private static readonly Stopwatch Since = new();
    private static readonly object Gate = new();

    /// <summary>Tests only: replaces the network answer.</summary>
    public static Func<DateTimeOffset?>? ServerOverride { get; set; }

    public static async Task SyncAsync(string dataDirectory)
    {
        DateTimeOffset? answer = ServerOverride?.Invoke();
        if (ServerOverride is null)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            foreach (string source in Sources)
            {
                try
                {
                    using var response = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, source)).ConfigureAwait(false);
                    answer = response.Headers.Date;
                    if (answer.HasValue) break;
                }
                catch (Exception) { }
            }
        }
        if (answer is not { } time) return;
        lock (Gate) { _server = time; Since.Restart(); }
        ExDailyQuota.NoteTrustedTime(dataDirectory, time);
    }

    public static DateTimeOffset? Now { get { lock (Gate) return _server is { } s ? s + Since.Elapsed : null; } }

    /// <summary>The quota day: Moscow date (UTC+3). Time zones cannot move it either.</summary>
    public static string DayOf(DateTimeOffset time) => time.ToOffset(TimeSpan.FromHours(3)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Free: at most three eX conversions a day by server time; Prime has no quota. The counter is
/// signed (HMAC) and kept twice — in the profile and in ~/Library/Preferences — so editing it
/// reads as «used up» and deleting one copy is not enough. Only a finished package counts.
/// </summary>
public static class ExDailyQuota
{
    public const int FreePerDay = 3;
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("dustore-ex-quota/v1/" + Environment.MachineName + "/" + Environment.UserName);

    private sealed record Record(string Day, int Used, long LastTrusted, bool Tampered = false);

    private static string FileIn(string dataDirectory) => Path.Combine(dataDirectory, "ex-quota.dat");
    private static string SecondCopy(string dataDirectory)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory))), 0, 6);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string folder = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Preferences") : Path.Combine(dataDirectory, "..");
        return Path.Combine(folder, ".local.dustorex.q" + hash);
    }

    private static string Sign(string body) => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(body)));

    private static Record? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Trim().Split('|');
        if (parts.Length != 4 || Sign(parts[0] + "|" + parts[1] + "|" + parts[2]) != parts[3]
            || !int.TryParse(parts[1], out int used) || !long.TryParse(parts[2], out long last))
            return new Record("", 0, 0, Tampered: true);
        return new Record(parts[0], used, last);
    }

    private static bool Later(string a, string b)
    {
        bool aDate = a.Length == 10 && char.IsDigit(a[0]), bDate = b.Length == 10 && char.IsDigit(b[0]);
        return aDate && (!bDate || string.CompareOrdinal(a, b) > 0);
    }

    private static Record Read(string dataDirectory)
    {
        var merged = new Record("", 0, 0);
        foreach (string path in new[] { FileIn(dataDirectory), SecondCopy(dataDirectory) })
        {
            Record? r = null;
            try { if (File.Exists(path)) r = Parse(File.ReadAllText(path)); } catch (Exception) { }
            if (r is null) continue;
            int used = r.Day == merged.Day ? Math.Max(r.Used, merged.Used) : Later(r.Day, merged.Day) || merged.Day.Length == 0 ? r.Used : merged.Used;
            string day = Later(r.Day, merged.Day) || merged.Day.Length == 0 ? r.Day : merged.Day;
            merged = new Record(day, used, Math.Max(r.LastTrusted, merged.LastTrusted), merged.Tampered || r.Tampered);
        }
        return merged;
    }

    private static void Write(string dataDirectory, Record record)
    {
        string body = $"{record.Day}|{record.Used}|{record.LastTrusted}";
        string text = body + "|" + Sign(body);
        foreach (string path in new[] { FileIn(dataDirectory), SecondCopy(dataDirectory) })
            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); } catch (Exception) { }
    }

    private static string CurrentDay(Record record)
    {
        string day = TrustedClock.Now is { } now ? TrustedClock.DayOf(now)
            : record.LastTrusted > 0 ? TrustedClock.DayOf(DateTimeOffset.FromUnixTimeSeconds(record.LastTrusted))
            : record.Day.Length > 0 ? record.Day : "first-run";
        return Later(record.Day, day) ? record.Day : day;
    }

    public static int UsedToday(string dataDirectory)
    {
        var record = Read(dataDirectory);
        if (record.Tampered) return FreePerDay;
        return record.Day == CurrentDay(record) ? record.Used : 0;
    }

    public static string? Refusal(string dataDirectory) =>
        Edition.IsPrime || UsedToday(dataDirectory) < FreePerDay ? null
            : $"Сегодня использованы все {FreePerDay} переноса eX версии Free. Новые появятся после полуночи по Москве (время берётся с сервера, нужен интернет). В Prime переносов без лимита.";

    public static string Remaining(string dataDirectory) =>
        Edition.IsPrime ? "" : $" Free: сегодня осталось переносов eX — {Math.Max(0, FreePerDay - UsedToday(dataDirectory))} из {FreePerDay}.";

    public static void RecordSuccess(string dataDirectory)
    {
        if (Edition.IsPrime) return;
        var record = Read(dataDirectory);
        string day = CurrentDay(record);
        int used = record.Tampered ? FreePerDay : record.Day == day ? record.Used : 0;
        Write(dataDirectory, new Record(day, used + 1, record.LastTrusted));
    }

    public static void NoteTrustedTime(string dataDirectory, DateTimeOffset time)
    {
        var record = Read(dataDirectory);
        long seconds = time.ToUnixTimeSeconds();
        if (record.Tampered || seconds <= record.LastTrusted) return;
        string day = TrustedClock.DayOf(time);
        bool keep = record.Day == day || Later(record.Day, day);
        Write(dataDirectory, new Record(keep ? record.Day : day, keep ? record.Used : 0, seconds));
    }

    /// <summary>Tests only.</summary>
    public static void Clear(string dataDirectory)
    {
        foreach (string path in new[] { FileIn(dataDirectory), SecondCopy(dataDirectory) }) try { File.Delete(path); } catch (Exception) { }
    }
    public static void DeleteFirstCopy(string dataDirectory) { try { File.Delete(FileIn(dataDirectory)); } catch (Exception) { } }
    public static void CorruptFirstCopy(string dataDirectory)
    {
        try { File.WriteAllText(FileIn(dataDirectory), File.ReadAllText(FileIn(dataDirectory)).Replace("|3|", "|0|")); } catch (Exception) { }
    }
}
