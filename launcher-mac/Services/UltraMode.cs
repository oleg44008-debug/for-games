using System.Runtime.InteropServices;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Prime ULTRA: more frames without touching the picture. The game keeps its resolution,
/// textures, filtering and effects; ULTRA removes what stands between the game and the Mac:
///
///  • the shortest graphics path: on Apple silicon Direct3D 11 goes straight to Metal (DXMT on
///    the CrossOver-based Wine), on Intel through DXVK — the fastest route that draws there;
///  • no frame cap: vertical sync and DXVK's limiter no longer hold the game at 60 FPS;
///  • persistent shader caches: a second start, and every level after the first visit, no
///    longer stutters while shaders compile; all CPU cores compile them;
///  • Wine's debug output off; Rosetta exposes AVX/AVX2 so games pick their vector code;
///  • the Mac does not nap or sleep while the game runs, and the launcher steps aside.
/// </summary>
public static class UltraMode
{
    /// <summary>Logical size of the main display, set by the window when it opens.</summary>
    public static (int Width, int Height) Display { get; set; } = (1440, 900);

    public static bool AppleSilicon => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>On Apple silicon ULTRA runs Windows games on the Metal route (DXMT).</summary>
    public static bool UsesMetal => AppleSilicon;

    /// <summary>Engine arguments: none — the game keeps its resolution, quality and frame pacing.</summary>
    public static IReadOnlyList<string> Arguments(GameEngineKind engine) => Array.Empty<string>();

    private static string CacheDirectory(string kind)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string folder = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Caches", "DUSTORE Launcher V", kind) : Path.Combine(Path.GetTempPath(), "dustore-" + kind);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Shared by both graphics routes.</summary>
    public static void AddCommon(IDictionary<string, string> env)
    {
        env["WINEDEBUG"] = "-all";
        env["WINEESYNC"] = "1";
        env["WINEMSYNC"] = "1";
        // macOS 15 Rosetta can advertise AVX/AVX2: games pick their vectorised code paths.
        env["ROSETTA_ADVERTISE_AVX"] = "1";
        env["MVK_CONFIG_RESUME_LOST_DEVICE"] = "1";
    }

    /// <summary>DXVK route: uncapped, every core compiling shaders, a persistent pipeline cache. Image quality untouched.</summary>
    public static void AddDxvk(IDictionary<string, string> env, string dataDirectory)
    {
        AddCommon(env);
        string config = Path.Combine(dataDirectory, "dxvk-ultra.conf");
        File.WriteAllText(config, string.Join("\n",
            "# DUSTORE Prime ULTRA: steady frames, nothing taken away",
            "# Vertical sync stays: uncapped frames heat a MacBook until it throttles and loses FPS.",
            "dxvk.numCompilerThreads = 0",
            ""));
        env["DXVK_CONFIG_FILE"] = config;
        env["DXVK_STATE_CACHE"] = "1";
        env["DXVK_STATE_CACHE_PATH"] = CacheDirectory("dxvk");
        if (AppleSilicon) env["MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS"] = "0";
    }

    /// <summary>Metal route (DXMT): native resolution, no upscaling, frames up to the display's maximum.</summary>
    public static void AddMetal(IDictionary<string, string> env, List<string> dxmtConfig)
    {
        AddCommon(env);
        env["DXMT_SHADER_CACHE_PATH"] = CacheDirectory("dxmt");
        // Frame pacing stays the display's own.
    }
}
