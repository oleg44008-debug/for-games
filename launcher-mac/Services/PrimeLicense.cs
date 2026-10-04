using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Prime is unlocked by a purchase in the Dustore store, not by the file.
///
/// The launcher's own store view opens the Prime product's download link as the signed-in buyer:
/// the store answers with the file (bought — the download is cancelled at once) or with its
/// payment or sign-in page (not bought). Nothing leaves this Mac but that request to dustore.ru.
/// A confirmed purchase is bound to this Mac's hardware, re-checked monthly and kept offline.
/// </summary>
public static class PrimeLicense
{
    /// <summary>The Prime product in the Dustore store (game_id). 0 until the product is published.</summary>
    public const int ProductId = 0;
    public static string DownloadUrl => "https://dustore.ru/swad/controllers/download_game.php?game_id=" + ProductId;
    private static readonly TimeSpan Recheck = TimeSpan.FromDays(30);
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("dustore-prime/mac/v1");
    private static bool? _active;

    public enum Ownership { Owned, NotOwned, NeedsLogin, NotPublished, Offline }

    private static string FilePath
    {
        get
        {
            string profile = Environment.GetEnvironmentVariable("DUSTOREV_PROFILE_DIRECTORY")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DUSTORE Launcher V");
            return Path.Combine(profile, "prime-license.dat");
        }
    }

    /// <summary>This Mac's hardware UUID, hashed: the record is worthless on any other Mac.</summary>
    private static string DeviceId()
    {
        string uuid = "";
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var start = new ProcessStartInfo("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(start)!;
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                var m = System.Text.RegularExpressions.Regex.Match(text, "\"IOPlatformUUID\" = \"([^\"]+)\"");
                if (m.Success) uuid = m.Groups[1].Value;
            }
            catch (Exception) { }
        }
        else uuid = Environment.MachineName;
        return Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes("device/" + uuid + "/" + Environment.UserName)));
    }

    private static string Seal(string body) => Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(body)));

    private static (string Device, long Checked)? Read()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string[] parts = File.ReadAllText(FilePath).Trim().Split('|');
            if (parts.Length != 3 || Seal(parts[0] + "|" + parts[1]) != parts[2] || !long.TryParse(parts[1], out long when)) return null;
            return (parts[0], when);
        }
        catch (Exception) { return null; }
    }

    private static void Write(string device, long checkedAt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $"{device}|{checkedAt}|{Seal(device + "|" + checkedAt)}");
    }

    public static bool IsActive
    {
        get
        {
            if (_active.HasValue) return _active.Value;
            _active = Read() is { } r && r.Device == DeviceId();
            return _active.Value;
        }
    }

    public static bool NeedsRecheck => Read() is { } r && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(r.Checked) > Recheck;

    /// <summary>Records what the store said; the probe itself runs in the store view (see MainViewModel).</summary>
    public static void Accept(Ownership answer)
    {
        if (answer == Ownership.Owned) { Write(DeviceId(), DateTimeOffset.UtcNow.ToUnixTimeSeconds()); _active = true; }
        else if (answer == Ownership.NotOwned && Read() is not null && NeedsRecheck) { try { File.Delete(FilePath); } catch (Exception) { } _active = false; }
    }

    public static string Explain(Ownership answer) => answer switch
    {
        Ownership.Owned => "Prime активирован на этом Mac. Перезапустите лаунчер, чтобы включились все возможности.",
        Ownership.NeedsLogin => "Войдите в свой аккаунт Dustore во вкладке «Магазин» и нажмите «Активировать Prime» снова.",
        Ownership.NotOwned => "В этом аккаунте Dustore Prime не куплен. Купите Prime в магазине Dustore и повторите.",
        Ownership.NotPublished => "Prime ещё не выставлен в магазине Dustore.",
        _ => "Нет связи с магазином Dustore. Проверьте интернет и повторите."
    };

    /// <summary>A store response is the Prime file itself — the buyer owns it.</summary>
    public static bool IsPrimeFile(string url) =>
        ProductId > 0 && url.Contains("s3.regru.cloud", StringComparison.OrdinalIgnoreCase) && url.Contains("/game-" + ProductId + "/", StringComparison.Ordinal);
}
