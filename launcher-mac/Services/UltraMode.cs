using System.Runtime.InteropServices;

namespace DustoreLauncherV.Mac.Services;

/// <summary>
/// Prime ULTRA: everything that buys frames, switched on together for one game.
///
/// The big levers are the number of pixels and the frame cap. The game renders fewer pixels
/// (MetalFX upscales them back on Apple silicon; on Intel the game simply runs at ¾ of the
/// screen), vertical sync stops capping it at the display's 60 Hz, and the translation layers
/// drop the safety work that costs frames. The smaller levers: Rosetta exposes AVX to the game,
/// Wine stops logging, shaders compile in the background, the launcher yields the CPU and GPU.
/// </summary>
public static class UltraMode
{
    /// <summary>Logical size of the main display, set by the window when it opens.</summary>
    public static (int Width, int Height) Display { get; set; } = (1440, 900);

    public static bool AppleSilicon => RuntimeInformation.OSArchitecture == Architecture.Arm64;

    /// <summary>Render size for engines that take it on the command line: ¾ of the screen, even numbers.</summary>
    public static (int Width, int Height) RenderSize()
    {
        double scale = AppleSilicon ? 0.85 : 0.75;
        int w = (int)(Display.Width * scale) / 2 * 2, h = (int)(Display.Height * scale) / 2 * 2;
        return (Math.Max(640, w), Math.Max(360, h));
    }

    public static IReadOnlyList<string> Arguments(GameEngineKind engine)
    {
        var (w, h) = RenderSize();
        return engine switch
        {
            // Fullscreen at a lower resolution: macOS scales the picture up for free.
            GameEngineKind.Unity => new[] { "-screen-fullscreen", "1", "-window-mode", "borderless", "-screen-width", w.ToString(), "-screen-height", h.ToString(), "-nolog" },
            GameEngineKind.Godot => new[] { "--fullscreen", "--resolution", $"{w}x{h}", "--disable-vsync" },
            _ => Array.Empty<string>()
        };
    }

    /// <summary>Shared by both graphics routes.</summary>
    public static void AddCommon(IDictionary<string, string> env)
    {
        env["WINEDEBUG"] = "-all";
        env["WINEESYNC"] = "1";
        env["WINEMSYNC"] = "1";
        // macOS 15 Rosetta can advertise AVX/AVX2: games pick their vectorised code paths.
        env["ROSETTA_ADVERTISE_AVX"] = "1";
        env["DXVK_ASYNC"] = "1";
    }

    /// <summary>Standard route: DXVK over MoltenVK, no vsync, no anisotropic filtering, relaxed barriers.</summary>
    public static void AddDxvk(IDictionary<string, string> env, string dataDirectory)
    {
        AddCommon(env);
        string config = Path.Combine(dataDirectory, "dxvk-ultra.conf");
        File.WriteAllText(config, string.Join("\n",
            "# DUSTORE Prime ULTRA",
            "dxgi.syncInterval = 0",
            "dxgi.maxFrameRate = 0",
            "d3d9.presentInterval = 0",
            "d3d11.samplerAnisotropy = 0",
            "d3d9.samplerAnisotropy = 0",
            "d3d11.relaxedBarriers = True",
            "dxvk.numCompilerThreads = 0",
            "") );
        env["DXVK_CONFIG_FILE"] = config;
        env["DXVK_FRAME_RATE"] = "0";
        // MoltenVK: fast math in the generated Metal shaders, no waiting on each submit.
        env["MVK_CONFIG_FAST_MATH_ENABLED"] = "1";
        env["MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS"] = "0";
    }

    /// <summary>Metal route (DXMT): MetalFX renders at half size and upscales; frames up to 120.</summary>
    public static void AddMetal(IDictionary<string, string> env, List<string> dxmtConfig)
    {
        AddCommon(env);
        if (AppleSilicon)
        {
            env["DXMT_METALFX_SPATIAL_SWAPCHAIN"] = "1";
            dxmtConfig.Add("d3d11.metalSpatialUpscaleFactor=2.0");
        }
        dxmtConfig.Add("d3d11.preferredMaxFrameRate=120");
    }
}
