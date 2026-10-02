using System.Text;
using System.Text.RegularExpressions;

namespace DustoreX.AutoConverter;

/// <summary>The game's own executable inside a Windows build, chosen without asking the user.</summary>
public sealed record GameExecutable(string Path, string Architecture, string Reason, IReadOnlyList<string> Skipped);

/// <summary>What a Windows Unity build reveals about itself without running it.</summary>
public sealed record UnityBuildInfo(string DataFolder, string? Version, string ScriptingBackend, string Architecture);

/// <summary>
/// Picks the main Windows executable of a game folder or ZIP. Builds usually carry helpers next to
/// the game (UnityCrashHandler64.exe, Godot's *.console.exe, Unreal's prerequisite installers,
/// redistributables, uninstallers); engine layout tells which file actually starts the game.
/// </summary>
public static class GameExecutableFinder
{
    // Helpers that ship beside games but never start them.
    private static readonly Regex HelperName = new(
        @"^(unins\d*|setup|install(er)?|.*redist.*|vc_?redist.*|vcredist.*|dxsetup|dxwebsetup|directx.*|dotnet.*|ndp\d.*|.*crash ?handler.*|crashpad_handler|crashreportclient|.*crashreporter.*|.*crash_?report.*|notification_helper|.*prereq.*|ue\d?prereqsetup.*|physx.*|oalinst|.*updater|7z.*|zsync.*|python[w]?|pythonw?\d.*|.*\.console)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HelperFolder = new(
        @"(^|/)(_?commonredist|redist|redistributables?|directx|vcredist|prerequisites|__installer|installers?|support|engine/extras|engine/binaries/thirdparty|monobleedingedge|lib/py[^/]*)/",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static GameExecutable? Find(string input, string? preferredName = null)
    {
        using var data = SafeData.Open(input, true);
        return Find(data.Items, preferredName ?? System.IO.Path.GetFileNameWithoutExtension(input.TrimEnd('/', '\\')));
    }

    internal static GameExecutable? Find(IReadOnlyList<DataItem> items, string? preferredName)
    {
        // ZIPs often list files only, so every parent folder is derived from the file paths.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            string name = item.Name.TrimEnd('/');
            names.Add(name);
            for (int slash = name.IndexOf('/'); slash > 0; slash = name.IndexOf('/', slash + 1)) names.Add(name[..slash]);
        }
        var skipped = new List<string>();
        var scored = new List<(DataItem Item, string Architecture, int Score, string Reason)>();
        foreach (var item in items.Where(i => !i.IsDirectory && !i.IsSymlink && i.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
        {
            string file = System.IO.Path.GetFileName(item.Name), stem = System.IO.Path.GetFileNameWithoutExtension(file);
            if (HelperName.IsMatch(stem) || HelperFolder.IsMatch(item.Name)) { skipped.Add(item.Name); continue; }
            string kind;
            using (var stream = item.Open()) kind = BinaryKind.Read(stream);
            if (!kind.StartsWith("Windows PE", StringComparison.Ordinal)) { skipped.Add(item.Name); continue; }
            string parent = SafeData.ParentName(item.Name);
            int depth = item.Name.Count(c => c == '/');
            int score = -10 * depth;
            string reason = "единственный подходящий EXE";
            if (names.Contains(parent + stem + "_Data")) { score += 100; reason = "рядом папка " + stem + "_Data (Unity)"; }
            else if (names.Contains(parent + stem + ".pck")) { score += 90; reason = "рядом " + stem + ".pck (Godot)"; }
            else if (names.Contains(parent + stem + "/Binaries/Win64") || names.Any(n => n.StartsWith(parent + stem + "/Binaries/Win64/", StringComparison.OrdinalIgnoreCase)))
            { score += 80; reason = "загрузчик Unreal Engine (" + stem + "/Binaries)"; }
            else if (item.Name.Contains("/Binaries/Win", StringComparison.OrdinalIgnoreCase) && names.Any(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.Count(c => c == '/') < depth))
            { score -= 40; } // Unreal shipping binary; the top-level bootstrap starts it with the right arguments.
            if (preferredName is { Length: > 0 } && Normalize(stem) == Normalize(preferredName)) { score += 30; if (reason.StartsWith("единственный", StringComparison.Ordinal)) reason = "имя совпадает с названием игры"; }
            scored.Add((item, kind["Windows PE ".Length..], score, reason));
        }
        if (scored.Count == 0) return null;
        // Equal scores: the larger file is the engine player rather than a small tool.
        var best = scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Item.Length).ThenBy(s => s.Item.Name, StringComparer.Ordinal).First();
        string why = scored.Count == 1 ? best.Reason
            : best.Reason.StartsWith("единственный", StringComparison.Ordinal) ? "самый крупный из " + scored.Count + " EXE на верхнем уровне" : best.Reason;
        skipped.AddRange(scored.Where(s => s.Item != best.Item).Select(s => s.Item.Name));
        return new GameExecutable(best.Item.Name, best.Architecture, why, skipped);
    }

    /// <summary>Unity facts read from the build's data folder; null when the folder is not a Unity build.</summary>
    public static UnityBuildInfo? InspectUnity(string input, GameExecutable executable)
    {
        using var data = SafeData.Open(input, true);
        return InspectUnity(data.Items, executable);
    }

    internal static UnityBuildInfo? InspectUnity(IReadOnlyList<DataItem> items, GameExecutable executable)
    {
        string parent = SafeData.ParentName(executable.Path);
        string dataFolder = parent + System.IO.Path.GetFileNameWithoutExtension(executable.Path) + "_Data/";
        if (!items.Any(i => i.Name.StartsWith(dataFolder, StringComparison.OrdinalIgnoreCase))) return null;
        string backend = items.Any(i => i.Name.Equals(parent + "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) ? "IL2CPP"
            : items.Any(i => i.Name.StartsWith(dataFolder + "Managed/", StringComparison.OrdinalIgnoreCase) && i.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ? "Mono"
            : "не определён";
        string? version = null;
        foreach (string candidate in new[] { "globalgamemanagers", "data.unity3d", "mainData", "level0" })
        {
            var file = items.FirstOrDefault(i => !i.IsDirectory && i.Name.Equals(dataFolder + candidate, StringComparison.OrdinalIgnoreCase));
            if (file is null) continue;
            version = ReadUnityVersion(file);
            if (version is not null) break;
        }
        return new UnityBuildInfo(dataFolder.TrimEnd('/'), version, backend, executable.Architecture);
    }

    private static readonly Regex UnityVersion = new(@"(?<![\d.])((?:20\d\d|6\d{3}|[3-5])\.\d{1,2}\.\d{1,3}[abfpx]\d{1,3})", RegexOptions.CultureInvariant);

    // Serialized files and UnityFS bundles carry the editor version as plain ASCII near the start.
    private static string? ReadUnityVersion(DataItem item)
    {
        byte[] head = new byte[512];
        int count;
        using (var stream = item.Open()) count = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        var match = UnityVersion.Match(Encoding.ASCII.GetString(head, 0, count));
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Normalize(string value) => new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
