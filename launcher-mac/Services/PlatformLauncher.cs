using System.Diagnostics;

namespace DustoreLauncherV.Mac.Services;

public interface IPlatformLauncher
{
    bool IsMacOS { get; }
    Task OpenAppAsync(string appPath, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default);
    Task RevealAsync(string path, CancellationToken cancellation = default);
    Task OpenUrlAsync(string url, CancellationToken cancellation = default);
}

public sealed class PlatformLauncher : IPlatformLauncher
{
    /// <summary>Environment entry that is not exported: it names the file for the game's stdout and stderr.</summary>
    public const string GameLogKey = "__DUSTORE_GAME_LOG";

    public bool IsMacOS => OperatingSystem.IsMacOS();

    public Task OpenAppAsync(string appPath, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellation = default)
    {
        if (!IsMacOS) throw new PlatformNotSupportedException("Запуск .app доступен на macOS.");
        var open = new List<string> { "-a", appPath };
        foreach (var (key, value) in environment)
        {
            // The game's own output goes to a log the launcher (and support) can read.
            if (key == GameLogKey) { open.Add("--stdout"); open.Add(value); open.Add("--stderr"); open.Add(value); continue; }
            open.Add("--env"); open.Add(key + "=" + value);
        }
        if (arguments.Count > 0) { open.Add("--args"); open.AddRange(arguments); }
        return RunOpenAsync(open, cancellation);
    }

    public Task RevealAsync(string path, CancellationToken cancellation = default)
    {
        if (!IsMacOS) throw new PlatformNotSupportedException("Показ в Finder доступен на macOS.");
        return RunOpenAsync(["-R", path], cancellation);
    }

    public Task OpenUrlAsync(string url, CancellationToken cancellation = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
            throw new ArgumentException("Можно открыть только адрес HTTP или HTTPS.", nameof(url));
        if (!IsMacOS) throw new PlatformNotSupportedException("Этот лаунчер предназначен для macOS.");
        return RunOpenAsync([uri.AbsoluteUri], cancellation);
    }

    private static async Task RunOpenAsync(IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo("/usr/bin/open")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить macOS open.");
        Task<string> error = process.StandardError.ReadToEndAsync(cancellation);
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellation);
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            string errorText = await error.ConfigureAwait(false);
            await output.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException(string.IsNullOrWhiteSpace(errorText)
                ? "macOS не смогла открыть приложение. Код: " + process.ExitCode
                : errorText.Trim());
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
            throw;
        }
    }
}
