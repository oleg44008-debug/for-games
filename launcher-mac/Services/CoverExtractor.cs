using System.Diagnostics;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Turns the icon of a Mac application into a cached PNG for the library shelf.
/// Uses the system tools plutil and sips; games without an .app keep the monogram tile.
/// </summary>
internal static class CoverExtractor
{
    public static async Task<string?> GetCoverAsync(GameEntry entry, string dataDirectory, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(dataDirectory)) return null;
        string? app = entry.PreparedMacAppPath is { } prepared && Directory.Exists(prepared) ? prepared
            : entry.SourcePath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(entry.SourcePath) ? entry.SourcePath
            : null;
        if (app is null) return null;
        string covers = Path.Combine(dataDirectory, "Covers");
        string cover = Path.Combine(covers, entry.Id.ToString("N") + ".png");
        if (File.Exists(cover) && new FileInfo(cover).Length > 0) return cover;
        string? icon = await FindIconAsync(app, cancellation);
        if (icon is null) return null;
        Directory.CreateDirectory(covers);
        string temporary = cover + "." + Guid.NewGuid().ToString("N") + ".png";
        try
        {
            if (await RunAsync("/usr/bin/sips", new[] { "-s", "format", "png", "-Z", "512", icon, "--out", temporary }, cancellation) is null
                || !File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                return null;
            File.Move(temporary, cover, overwrite: true);
            return cover;
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static async Task<string?> FindIconAsync(string app, CancellationToken cancellation)
    {
        string resources = Path.Combine(app, "Contents", "Resources");
        if (!Directory.Exists(resources)) return null;
        string? named = await RunAsync("/usr/bin/plutil", new[] { "-extract", "CFBundleIconFile", "raw", "-o", "-", Path.Combine(app, "Contents", "Info.plist") }, cancellation);
        if (!string.IsNullOrWhiteSpace(named))
        {
            string file = named.Trim();
            if (!file.EndsWith(".icns", StringComparison.OrdinalIgnoreCase)) file += ".icns";
            string candidate = Path.Combine(resources, Path.GetFileName(file));
            if (File.Exists(candidate)) return candidate;
        }
        return Directory.EnumerateFiles(resources, "*.icns").OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
    }

    private static async Task<string?> RunAsync(string tool, string[] arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
            await errors;
            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException) { return null; }
    }
}
