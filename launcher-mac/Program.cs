using Avalonia;
using DustoreLauncherV.Mac.Services;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace DustoreLauncherV.Mac;

internal static class Program
{
    internal static bool UiSmoke { get; private set; }
    internal static string? SmokeInputPath { get; private set; }
    /// <summary>--screen=ex|settings|store|home|jams|assets opens a section directly (for checks).</summary>
    internal static string? StartSection { get; private set; }
    internal static string SmokeReportPath { get; private set; } = "";
    internal static string SmokeScreenshotPath { get; private set; } = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--wine-smoke"))
        {
            // Headless Wine check for Mac CI: install, package with eX, run through the package script.
            SmokeReportPath = Path.GetFullPath(Argument(args, "--smoke-report") ?? Path.Combine(Path.GetTempPath(), "wine-smoke.json"));
            Directory.CreateDirectory(Path.GetDirectoryName(SmokeReportPath)!);
            try
            {
                WriteReport(WineSmoke.RunAsync(Path.GetDirectoryName(SmokeReportPath)!, CancellationToken.None).GetAwaiter().GetResult());
                return 0;
            }
            catch (Exception error)
            {
                WriteReport(new { status = "Fail", mode = "wine", error = error.ToString() });
                return 1;
            }
        }
        bool serviceSmoke = args.Contains("--smoke-test");
        UiSmoke = args.Contains("--ui-smoke");
        StartupDiagnostics.Begin(serviceSmoke ? "services-smoke" : UiSmoke ? "ui-smoke" : "normal");
        SmokeInputPath = Argument(args, "--smoke-input");
        StartSection = args.FirstOrDefault(a => a.StartsWith("--screen=", StringComparison.Ordinal))?["--screen=".Length..];
        if (serviceSmoke || UiSmoke)
        {
            SmokeReportPath = Path.GetFullPath(Argument(args, "--smoke-report")
                ?? Path.Combine(Path.GetTempPath(), "dustore-launcher-mac-" + Guid.NewGuid().ToString("N"), "smoke.json"));
            // Darwin's standard temporary directory can use the /var alias. Keep the
            // owned test profile on its physical path without relaxing Core's policy.
            if (OperatingSystem.IsMacOS() && SmokeReportPath.StartsWith("/var/", StringComparison.Ordinal))
                SmokeReportPath = "/private" + SmokeReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(SmokeReportPath)!);
            SmokeScreenshotPath = Path.GetFullPath(Argument(args, "--smoke-screenshot") ?? Path.ChangeExtension(SmokeReportPath, ".png"));
            Directory.CreateDirectory(Path.GetDirectoryName(SmokeScreenshotPath)!);
            string profile = Path.Combine(Path.GetDirectoryName(SmokeReportPath)!, "profile-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("DUSTOREV_PROFILE_DIRECTORY", profile);
            try
            {
                var report = LauncherSmokeChecks.RunAsync(SmokeInputPath, profile, CancellationToken.None).GetAwaiter().GetResult();
                if (!report.Success || !OriginalLogoUnchanged())
                {
                    WriteReport(new { status = "Fail", mode = "launcher-services", report, originalLogoUnchanged = OriginalLogoUnchanged() });
                    return 1;
                }
                if (serviceSmoke)
                {
                    WriteReport(new { status = "Pass", mode = "launcher-services", report,
                        operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        originalLogoUnchanged = true, verifiedAtUtc = DateTimeOffset.UtcNow });
                    return 0;
                }
            }
            catch (Exception error)
            {
                WriteReport(new { status = "Fail", mode = "launcher-services", error = error.ToString() });
                return 1;
            }
        }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error)
        {
            StartupDiagnostics.RecordFailure(error);
            if (UiSmoke)
                WriteReport(new { status = "Fail", mode = "native-desktop-ui-startup", error = error.ToString() });
            else
                ShowNativeFailureAlert(error);
            return 1;
        }
    }

    // When the interface itself cannot start there is no window to report into;
    // a system alert keeps the failure from looking like a silent instant close.
    private static void ShowNativeFailureAlert(Exception error)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            string message = ("DUSTORE LAUNCHER V не смог открыть окно.\n\n" + error.Message + "\n\nЖурнал: " + StartupDiagnostics.LogPath)
                .Replace("\\", "\\\\").Replace("\"", "\\\"");
            var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            start.ArgumentList.Add("-e");
            start.ArgumentList.Add("display alert \"Ошибка запуска\" message \"" + message + "\" as critical");
            using var alert = System.Diagnostics.Process.Start(start);
            alert?.WaitForExit(120_000);
        }
        catch (Exception) { /* The log already holds the failure. */ }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
        // Drawn by the GPU through Metal. Software rendering redrew the whole Retina window on the
        // CPU — blurred shadows and the translucent sidebar included — and the library lagged on
        // Intel Macs. OpenGL and then software remain as fallbacks (a VM without a GPU, for one).
        if (OperatingSystem.IsMacOS())
            builder.With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = new[] { AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software }
            });
        return builder;
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static void WriteReport(object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SmokeReportPath)!);
        File.WriteAllText(SmokeReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static bool OriginalLogoUnchanged()
    {
        using Stream logo = Assembly.GetExecutingAssembly().GetManifestResourceStream("Dustore.original-logo.png")
            ?? throw new InvalidOperationException("The original launcher logo is missing.");
        return Convert.ToHexString(SHA256.HashData(logo)).Equals("69cdb26a75f82302b8f476788a705bbdd6c1ed8d74934e3f05cb4df6a41468d4", StringComparison.OrdinalIgnoreCase);
    }
}
