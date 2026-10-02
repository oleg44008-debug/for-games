using System.Diagnostics;
using DustoreX.AutoConverter;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Mac CI check of the whole Wine path: the launcher installs Wine, eX picks the game EXE out of a
/// Unity-style folder and packages it, and the package's own launch script runs it through that Wine.
/// The "game" is Wine's own cmd.exe (a real Windows PE) copied beside a decoy crash handler.
/// </summary>
internal static class WineSmoke
{
    public const string Token = "DUSTORE-WINE-OK";

    public static async Task<object> RunAsync(string workDirectory, CancellationToken cancellation)
    {
        var total = Stopwatch.StartNew();
        var install = Stopwatch.StartNew();
        bool wasInstalled = WineRuntime.IsInstalled;
        string wine = await WineRuntime.InstallAsync(new Progress<WineProgress>(p => Console.WriteLine(p.Stage + " " + (p.Fraction * 100).ToString("0"))), cancellation);
        install.Stop();
        var (versionCode, versionText) = await WineRuntime.RunAsync(wine, new[] { "--version" }, null, TimeSpan.FromMinutes(2), cancellation);
        if (versionCode != 0) throw new InvalidOperationException("wine --version failed: " + versionText);

        string wineHome = Path.GetDirectoryName(Path.GetDirectoryName(wine)!)!;
        string cmd = Directory.EnumerateFiles(wineHome, "cmd.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("x86_64-windows", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Wine has no x86_64-windows/cmd.exe to use as a test program.");

        string game = Path.Combine(workDirectory, "wine-smoke-game", "WineSmoke");
        if (Directory.Exists(game)) Directory.Delete(game, true);
        Directory.CreateDirectory(Path.Combine(game, "WineSmoke_Data"));
        File.Copy(cmd, Path.Combine(game, "WineSmoke.exe"));
        File.Copy(cmd, Path.Combine(game, "UnityCrashHandler64.exe")); // decoy the finder must skip
        File.WriteAllText(Path.Combine(game, "WineSmoke_Data", "globalgamemanagers"), "\0\0\0\02022.3.10f1\0synthetic");

        var plan = ConversionEngine.Inspect(game, TargetPlatform.MacOS);
        if (plan.Method != "wine" || !plan.CanConvert || !plan.Detail.Contains("WineSmoke.exe", StringComparison.Ordinal))
            throw new InvalidOperationException("eX did not plan the Unity-style folder for Wine with WineSmoke.exe: " + plan.Detail);
        string package = Path.Combine(workDirectory, "wine-smoke-" + Guid.NewGuid().ToString("N")[..8] + ".zip");
        await ConversionEngine.ConvertAsync(new ConversionRequest(game, TargetPlatform.MacOS, "WineSmoke", package), null, cancellation);
        string apps = Path.Combine(workDirectory, "wine-smoke-apps", Guid.NewGuid().ToString("N"));
        string app = MacPackageImporter.ImportIfMacApp(package, apps, cancellation)
            ?? throw new InvalidOperationException("The Wine package has no .app.");
        if (!WineRuntime.IsWineWrapper(app)) throw new InvalidOperationException("The package is not recognised as a Wine wrapper.");

        // The package's own script must find the launcher-installed Wine without any hint.
        var run = Stopwatch.StartNew();
        var environment = new Dictionary<string, string> { ["DUSTOREX_WINE"] = "", ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" };
        var (code, output) = await WineRuntime.RunAsync(Path.Combine(app, "Contents", "MacOS", "launch"),
            new[] { "/c", "echo", Token }, environment, TimeSpan.FromMinutes(10), cancellation);
        run.Stop();
        bool token = output.Contains(Token, StringComparison.Ordinal);
        if (!token) throw new InvalidOperationException($"The packaged game did not run under Wine (exit {code}): " + Tail(output));
        return new
        {
            status = "Pass", wineInstalledByLauncher = !wasInstalled, wineVersion = versionText.Trim(), wineArchive = WineRuntime.ArchiveName,
            wineSha256 = WineRuntime.Sha256, installSeconds = Math.Round(install.Elapsed.TotalSeconds, 1), chosenExecutable = "WineSmoke.exe",
            decoySkipped = true, packagedRunExitCode = code, tokenSeen = token, runSeconds = Math.Round(run.Elapsed.TotalSeconds, 1),
            totalSeconds = Math.Round(total.Elapsed.TotalSeconds, 1), appleSilicon = WineRuntime.IsAppleSilicon
        };
    }

    private static string Tail(string text) => text.Length > 1500 ? text[^1500..] : text;
}
