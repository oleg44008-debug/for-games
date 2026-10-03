using System.Text.RegularExpressions;

namespace DustoreLauncherV.Mac.Services;

public enum GameEngineKind { Other, Unity, Godot }

/// <summary>
/// Per-game window settings passed on the command line. Unity and Godot accept a window mode and
/// size there, which also overrides what a game saved on its first, too-large fullscreen start.
/// </summary>
public static class GameLaunchOptions
{
    public const string Windowed = "windowed", Fullscreen = "fullscreen", GameDefault = "game";
    public const int DefaultWidth = 1280, DefaultHeight = 720;

    public static GameEngineKind DetectEngine(string? app)
    {
        if (app is null || !Directory.Exists(app)) return GameEngineKind.Other;
        try
        {
            // An eX Wine package keeps the Windows build under Contents/Resources/game.
            string wineGame = Path.Combine(app, "Contents", "Resources", "game");
            string root = Directory.Exists(wineGame) ? wineGame : Path.Combine(app, "Contents");
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            foreach (string path in Directory.EnumerateFileSystemEntries(root, "*", options))
            {
                string name = Path.GetFileName(path);
                if (name.Equals("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) || name.Equals("UnityPlayer.dylib", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("globalgamemanagers", StringComparison.OrdinalIgnoreCase) || name.Equals("data.unity3d", StringComparison.OrdinalIgnoreCase))
                    return GameEngineKind.Unity;
                if (name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) return GameEngineKind.Godot;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return GameEngineKind.Other;
    }

    public static IReadOnlyList<string> Arguments(GameEngineKind engine, string? mode, int? width, int? height)
    {
        mode ??= Windowed;
        if (mode == GameDefault || engine == GameEngineKind.Other) return Array.Empty<string>();
        int w = width ?? DefaultWidth, h = height ?? DefaultHeight;
        bool full = mode == Fullscreen;
        return engine switch
        {
            GameEngineKind.Unity => full
                ? new[] { "-screen-fullscreen", "1", "-window-mode", "borderless", "-screen-width", w.ToString(), "-screen-height", h.ToString() }
                : new[] { "-screen-fullscreen", "0", "-window-mode", "windowed", "-screen-width", w.ToString(), "-screen-height", h.ToString() },
            GameEngineKind.Godot => new[] { full ? "--fullscreen" : "--windowed", "--resolution", $"{w}x{h}" },
            _ => Array.Empty<string>()
        };
    }

    /// <summary>DXVK compiles shaders in the background instead of stalling the first frames.</summary>
    public static IReadOnlyDictionary<string, string> Environment(bool wine) => wine
        ? new Dictionary<string, string> { ["DXVK_ASYNC"] = "1" }
        : new Dictionary<string, string>();

    /// <summary>Stops a game even when it covers the screen and ignores Esc.</summary>
    public static async Task StopAsync(string app, CancellationToken cancellation)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (WineRuntime.IsWineWrapper(app) && WinePrefixOf(app) is { } prefix)
        {
            string wineserver = Path.Combine(WineRuntime.CurrentLink, "bin", "wineserver");
            if (File.Exists(wineserver))
                await WineRuntime.RunAsync(wineserver, new[] { "-k" }, new Dictionary<string, string> { ["WINEPREFIX"] = prefix }, TimeSpan.FromSeconds(20), cancellation);
            return;
        }
        string pattern = Regex.Replace(Path.Combine(app.TrimEnd('/'), "Contents", "MacOS") + "/", @"[.\[\]()*+?{}|^$\\]", m => "\\" + m.Value);
        await WineRuntime.RunAsync("/usr/bin/pkill", new[] { "-f", "--", pattern }, null, TimeSpan.FromSeconds(10), cancellation);
    }

    // The eX wrapper script names its prefix: export WINEPREFIX="$HOME/Library/Application Support/DustoreX/Wine/<id>"
    internal static string? WinePrefixOf(string app)
    {
        string script = Path.Combine(app, "Contents", "MacOS", "launch");
        if (!File.Exists(script)) return null;
        var match = Regex.Match(File.ReadAllText(script), "export WINEPREFIX=\"\\$HOME/([^\"]+)\"");
        return match.Success ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), match.Groups[1].Value) : null;
    }
}
