using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DustoreLauncherV.Mac;

internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, object?> State = new();
    private static string _directory = "";

    internal static string ReportPath => Path.Combine(_directory, "last-startup.json");
    internal static string LogPath => Path.Combine(_directory, "startup.log");

    internal static void Begin(string mode)
    {
        string userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _directory = OperatingSystem.IsMacOS()
            ? Path.Combine(userDirectory, "Library", "Logs", "DUSTORE Launcher V")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DUSTORE Launcher V", "Logs");
        lock (Gate)
        {
            State.Clear();
            State["launchId"] = Guid.NewGuid().ToString("N");
            State["product"] = "DUSTORE LAUNCHER V";
            State["version"] = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            State["mode"] = mode;
            State["processId"] = Environment.ProcessId;
            State["architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
            State["operatingSystem"] = RuntimeInformation.OSDescription;
            State["renderingMode"] = OperatingSystem.IsMacOS() ? "software" : "platform-default";
            State["applicationBaseDirectory"] = AppContext.BaseDirectory;
            State["startedAtUtc"] = DateTimeOffset.UtcNow;
            Save("starting");
        }
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RecordFailure(args.ExceptionObject as Exception ?? new Exception("Unhandled runtime failure."));
        TaskScheduler.UnobservedTaskException += (_, args) => RecordFailure(args.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RecordExit();
    }

    internal static void RecordFrameworkInitialized()
    {
        lock (Gate) Save("framework-initialized");
    }

    internal static void RecordReady(MainWindow window)
    {
        lock (Gate)
        {
            State["profileDirectory"] = window.ViewModel.DataDirectory;
            State["windowVisible"] = window.IsVisible;
            State["clientWidth"] = window.ClientSize.Width;
            State["clientHeight"] = window.ClientSize.Height;
            State["libraryEntryCount"] = window.ViewModel.Games.Count;
            State["libraryInitializationSucceeded"] = !window.ViewModel.HasError && !window.ViewModel.IsBusy;
            if (window.ViewModel.HasError)
                State["error"] = window.ViewModel.Error;
            Save(window.ViewModel.HasError ? "failed" : "ready");
        }
    }

    internal static void RecordFailure(Exception error)
    {
        lock (Gate)
        {
            State["error"] = error.ToString();
            Save("failed");
        }
    }

    private static void RecordExit()
    {
        lock (Gate)
        {
            State["exitedAtUtc"] = DateTimeOffset.UtcNow;
            State["exitCode"] = Environment.ExitCode;
            // Preserve the last initialization state, including any failure.
            Save(State.GetValueOrDefault("status") as string ?? "exited");
        }
    }

    private static void Save(string status)
    {
        State["status"] = status;
        State["updatedAtUtc"] = DateTimeOffset.UtcNow;
        try
        {
            Directory.CreateDirectory(_directory);
            string report = JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true });
            string temporary = ReportPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, report);
            File.Move(temporary, ReportPath, overwrite: true);
            // Bound the app's own text log; diagnostics must never prevent startup.
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2 * 1024 * 1024)
                File.Move(LogPath, LogPath + ".previous", overwrite: true);
            File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} {status}" +
                (status == "failed" ? "\n" + State.GetValueOrDefault("error") : "") + "\n");
        }
        catch (Exception error)
        {
            Debug.WriteLine("Startup diagnostics unavailable: " + error.Message);
        }
    }
}
