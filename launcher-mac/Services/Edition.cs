namespace DustoreLauncherV.Mac.Services;

/// <summary>Free or Prime, fixed at build time (-p:DustoreEdition=Prime).</summary>
public static class Edition
{
#if PRIME
    public static readonly bool IsPrime = true;
#else
    public static readonly bool IsPrime = false;
#endif
    public static string Name => IsPrime ? "Prime" : "Free";

    /// <summary>Free converts at a capped pace; Prime has no limit.</summary>
    public const long FreeExBytesPerSecond = 4L * 1024 * 1024;
}

/// <summary>
/// Free: at most three eX conversions a day (local date); Prime has no quota.
/// Only a finished package counts: a failure or a cancel does not spend the quota.
/// </summary>
public static class ExDailyQuota
{
    public const int FreePerDay = 3;

    private static string FileIn(string dataDirectory) => Path.Combine(dataDirectory, "ex-daily.txt");

    public static int UsedToday(string dataDirectory)
    {
        try
        {
            string path = FileIn(dataDirectory);
            string[] parts = File.Exists(path) ? File.ReadAllText(path).Trim().Split(' ') : Array.Empty<string>();
            return parts.Length == 2 && parts[0] == DateTime.Now.ToString("yyyy-MM-dd") && int.TryParse(parts[1], out int used) ? used : 0;
        }
        catch (Exception) { return 0; }
    }

    public static string? Refusal(string dataDirectory) =>
        Edition.IsPrime || UsedToday(dataDirectory) < FreePerDay ? null
            : $"Сегодня использованы все {FreePerDay} переноса eX версии Free. Завтра они снова доступны, а в Prime переносов без лимита.";

    public static string Remaining(string dataDirectory) =>
        Edition.IsPrime ? "" : $" Free: сегодня осталось переносов eX — {Math.Max(0, FreePerDay - UsedToday(dataDirectory))} из {FreePerDay}.";

    public static void RecordSuccess(string dataDirectory)
    {
        if (Edition.IsPrime) return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(FileIn(dataDirectory), DateTime.Now.ToString("yyyy-MM-dd") + " " + (UsedToday(dataDirectory) + 1));
        }
        catch (Exception) { }
    }
}
