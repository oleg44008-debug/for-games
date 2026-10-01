using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DustoreX.AutoConverter;

/// <summary>Transfers game data into a corresponding official Ren'Py SDK runtime without evaluating scripts.</summary>
public static class RenPyPackager
{
    public static EnginePayloadInspection Inspect(string path)
    {
        using var source = SafeData.Open(path, true);
        string root = FindGameRoot(source);
        string? version = FindVersion(source);
        var game = GameItems(source, root).ToArray();
        RuntimePackageTools.CheckPortableFiles(game, "Ren'Py", inspectPython: true);
        foreach (var archive in game.Where(i => i.Name.EndsWith(".rpa", StringComparison.OrdinalIgnoreCase))) CheckRpaIndex(archive);
        return new EnginePayloadInspection(version, root, [version is null
            ? "Source Ren'Py version could not be established. An explicit runtime version remains an unverified compatibility assumption."
            : "Ren'Py engine/script-version metadata declares " + version + "; target runtime must match.",
            "RPYC scripts and RPA archives are opaque to static source-code inspection. Their behavior still needs target validation."]);
    }

    public static PackageResult Package(PackageRequest request)
    {
        RuntimePackageTools.ValidateRequest(request);
        using var source = SafeData.Open(request.InputPath, true);
        string gameRoot = FindGameRoot(source);
        var game = GameItems(source, gameRoot).ToArray();
        RuntimePackageTools.CheckPortableFiles(game, "Ren'Py", inspectPython: true);
        foreach (var archive in game.Where(i => i.Name.EndsWith(".rpa", StringComparison.OrdinalIgnoreCase))) CheckRpaIndex(archive);
        byte[] payload = RuntimePackageTools.CreatePayload(game);
        string? requiredVersion = FindVersion(source);
        using var runtime = SafeData.Open(request.RuntimePath, request.Target == TargetPlatform.MacOS);
        var runner = RuntimePackageTools.ExactlyOne(runtime.Items.Where(i => !i.IsDirectory && (i.Name == "renpy.py" || i.Name.EndsWith("/renpy.py", StringComparison.Ordinal))), "Supply a full official Ren'Py SDK containing exactly one renpy.py.");
        string sdk = SafeData.ParentName(runner.Name);
        string? sdkVersion = FindVersion(runtime);
        if (sdkVersion is null) throw new InvalidDataException("Ren'Py SDK must contain readable renpy/vc_version.py version metadata.");
        RuntimePackageTools.CheckVersion(requiredVersion, sdkVersion, "Ren'Py");
        RuntimePackageTools.CheckVersion(sdkVersion, request.RuntimeVersion, "Ren'Py SDK");
        var warnings = RuntimePackageTools.Warnings("Ren'Py", sdkVersion);
        if (requiredVersion is null) warnings.Add("Source engine version was unknown; the supplied SDK is a compatibility trial, not a proved matching rebuild.");
        warnings.Add("RPA/RPYC contents remain unchanged. Hidden platform-specific code, DRM, custom engine modifications and platform Python modules cannot be proved portable by this transfer.");
        warnings.Add("Existing game saves and cache files are omitted; source-runtime files outside game/ are replaced by the supplied SDK.");
        return RuntimePackageTools.WritePackage(request, runtime, payload, "Ren'Py", warnings, output =>
        {
            if (request.Target == TargetPlatform.Windows) Windows(runtime, sdk, runner, game, request.GameName, output);
            else Mac(runtime, sdk, runner, game, request.GameName, output, warnings);
        });
    }

    private static string FindGameRoot(SafeData source)
    {
        static bool Script(string name) => name.EndsWith(".rpy", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rpyc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rpa", StringComparison.OrdinalIgnoreCase);
        if (source.Items.Any(i => !i.IsDirectory && !i.Name.Contains('/') && Script(i.Name))) return "";
        var roots = source.Items.Where(i => !i.IsDirectory && Script(i.Name)).Select(i =>
        {
            int index = i.Name.IndexOf("/game/", StringComparison.Ordinal);
            return i.Name.StartsWith("game/", StringComparison.Ordinal) ? "game/" : index < 0 ? null : i.Name[..(index + 6)];
        }).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToArray();
        return roots.Length == 1 ? roots[0]! : throw new InvalidDataException("Select exactly one Ren'Py game/ directory containing RPY, RPYC or RPA files.");
    }

    private static IEnumerable<DataItem> GameItems(SafeData data, string root) => data.Items
        .Where(i => i.Name.StartsWith(root, StringComparison.Ordinal))
        .Select(i => RuntimePackageTools.Rename(i, i.Name[root.Length..]))
        .Where(i => i.Name.Length != 0 && !i.Name.StartsWith("saves/", StringComparison.OrdinalIgnoreCase)
            && !i.Name.StartsWith("cache/", StringComparison.OrdinalIgnoreCase) && i.Name is not "saves/" and not "cache/");

    private static string? FindVersion(SafeData source)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source.Items.Where(i => !i.IsDirectory && (i.Name == "renpy/vc_version.py" || i.Name.EndsWith("/renpy/vc_version.py", StringComparison.Ordinal))))
        {
            var match = Regex.Match(RuntimePackageTools.ReadText(item), @"(?m)^\s*version\s*=\s*['" + "\"" + @"](?<version>[0-9]+\.[0-9]+\.[0-9]+)(?:\.[0-9]+)?['" + "\"" + @"]", RegexOptions.CultureInvariant);
            if (match.Success) versions.Add(match.Groups["version"].Value);
        }
        if (versions.Count == 0)
            foreach (var item in source.Items.Where(i => !i.IsDirectory && (i.Name == "script_version.txt" || i.Name.EndsWith("/game/script_version.txt", StringComparison.Ordinal))))
            {
                var match = Regex.Match(RuntimePackageTools.ReadText(item), @"^\s*\(\s*(?<a>[0-9]+)\s*,\s*(?<b>[0-9]+)\s*,\s*(?<c>[0-9]+)(?:\s*,[^)]*)?\)\s*$", RegexOptions.CultureInvariant);
                if (match.Success) versions.Add(match.Groups["a"].Value + "." + match.Groups["b"].Value + "." + match.Groups["c"].Value);
            }
        return versions.Count switch { 0 => null, 1 => versions.Single(), _ => throw new InvalidDataException("Conflicting Ren'Py engine version metadata; automatic runtime selection is unsafe.") };
    }

    private static void Windows(SafeData runtime, string sdk, DataItem runner, DataItem[] game, string name, ZipArchive output)
    {
        string native = sdk + "lib/py3-windows-x86_64/";
        var executable = RuntimePackageTools.ExactlyOne(runtime.Items.Where(i => i.Name == native + "renpy.exe"), "SDK has no Windows x64 Ren'Py runtime.");
        RuntimePackageTools.RequireBinary(executable, "Windows PE");
        if (!runtime.Items.Any(i => i.Name == native + "librenpython.dll") || !runtime.Items.Any(i => i.Name.StartsWith(sdk + "lib/python3.", StringComparison.Ordinal)))
            throw new InvalidDataException("Ren'Py SDK is missing its native Windows runtime or Python standard library.");
        string root = name + "/";
        RuntimePackageTools.Copy(output, root + name + ".exe", executable);
        RuntimePackageTools.Copy(output, root + name + ".py", runner);
        foreach (var item in runtime.Items.Where(i => i.Name.StartsWith(sdk + "renpy/", StringComparison.Ordinal)
            || i.Name.StartsWith(native, StringComparison.Ordinal) || i.Name.StartsWith(sdk + "lib/python3.", StringComparison.Ordinal)
            || i.Name == sdk + "LICENSE.txt" || i.Name.StartsWith(sdk + "licenses/", StringComparison.Ordinal)))
        {
            if (item.Name == native + "renpy.exe") continue; // The native launcher is renamed consistently below.
            string relative = item.Name[sdk.Length..];
            RuntimePackageTools.Copy(output, root + relative, item);
        }
        RuntimePackageTools.Copy(output, root + "lib/py3-windows-x86_64/" + name + ".exe", executable);
        foreach (var item in game) RuntimePackageTools.Copy(output, root + "game/" + item.Name, item);
    }

    private static void Mac(SafeData runtime, string sdk, DataItem runner, DataItem[] game, string name, ZipArchive output, List<string> warnings)
    {
        if (!runtime.IsArchive) throw new InvalidDataException("macOS Ren'Py runtime must be its original SDK ZIP preserving Unix modes and symlinks.");
        string native = sdk + "lib/py3-mac-universal/";
        var executable = RuntimePackageTools.ExactlyOne(runtime.Items.Where(i => i.Name == native + "renpy"), "SDK has no macOS universal Ren'Py runtime.");
        RuntimePackageTools.RequireBinary(executable, "macOS Mach-O", true);
        if (!runtime.Items.Any(i => i.Name == native + "librenpython.dylib")) throw new InvalidDataException("SDK is missing librenpython.dylib.");
        string root = name + ".app/", autorun = root + "Contents/Resources/autorun/";
        RuntimePackageTools.Copy(output, root + "Contents/MacOS/" + name, executable);
        RuntimePackageTools.Copy(output, autorun + name + ".py", runner);
        foreach (var item in runtime.Items.Where(i => i.Name.StartsWith(sdk + "renpy/", StringComparison.Ordinal)
            || i.Name.StartsWith(native, StringComparison.Ordinal) || i.Name.StartsWith(sdk + "lib/python3.", StringComparison.Ordinal)
            || i.Name == sdk + "LICENSE.txt" || i.Name.StartsWith(sdk + "licenses/", StringComparison.Ordinal)))
        {
            if (item.Name == native + "renpy") continue; // Already emitted as CFBundleExecutable.
            string relative = item.Name[sdk.Length..];
            string destination = relative.StartsWith("lib/py3-mac-universal/", StringComparison.Ordinal) ? root + "Contents/MacOS/" + item.Name[native.Length..]
                : relative.StartsWith("lib/python3.", StringComparison.Ordinal) ? root + "Contents/Resources/" + relative : autorun + relative;
            if (item.IsSymlink) { SafeData.ValidateSymlink(item, sdk); SafeData.ValidateSymlink(RuntimePackageTools.Rename(item, destination), root); }
            RuntimePackageTools.Copy(output, destination, item);
        }
        foreach (var item in game) RuntimePackageTools.Copy(output, autorun + "game/" + item.Name, item);
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict")));
        RuntimePackageTools.SetPlist(document, "CFBundleExecutable", name);
        RuntimePackageTools.SetPlist(document, "CFBundlePackageType", "APPL");
        RuntimePackageTools.SetPlist(document, "CFBundleInfoDictionaryVersion", "6.0");
        RuntimePackageTools.SetPlist(document, "CFBundleShortVersionString", "1.0");
        RuntimePackageTools.SetPlist(document, "CFBundleVersion", "1");
        RuntimePackageTools.BrandPlist(document, name, "renpy");
        document.Root!.Element("dict")!.Add(new XElement("key", "NSHighResolutionCapable"), new XElement("true"));
        var icon = runtime.Items.FirstOrDefault(i => i.Name == sdk + "launcher/icon.icns" || i.Name == sdk + "renpy.app/Contents/Resources/icon.icns");
        if (icon is not null) { RuntimePackageTools.Copy(output, root + "Contents/Resources/icon.icns", icon); RuntimePackageTools.SetPlist(document, "CFBundleIconFile", "icon"); }
        RuntimePackageTools.Write(output, root + "Contents/Info.plist", RuntimePackageTools.PlistBytes(document));
        // Native bootstrap discovers its platform and Python library through these directories.
        foreach (string directory in new[] { root + "Contents/MacOS/lib/", root + "Contents/MacOS/lib/py3-mac-universal/" })
            RuntimePackageTools.CreateDirectoryEntry(output, directory);
        warnings.Add("A new unsigned macOS application bundle was constructed using the official Ren'Py distribution layout. Sign/notarize it and test it on macOS before calling it a verified release.");
    }

    private static void CheckRpaIndex(DataItem archive)
    {
        // RPA indexes are pickle data. Never deserialize/evaluate Python pickle.
        // Decompress only a bounded index and conservatively reject visible native file names.
        byte[] bytes = RuntimePackageTools.ReadBytes(archive, SafetyLimits.MaxPayloadBytes);
        int newline = Array.IndexOf(bytes, (byte)'\n', 0, Math.Min(bytes.Length, 256));
        if (newline < 0) throw new InvalidDataException("Unsupported Ren'Py archive header: " + archive.Name);
        string header = Encoding.ASCII.GetString(bytes, 0, newline);
        var match = Regex.Match(header, @"^RPA-(?:2|3)\.0 (?<offset>[0-9a-fA-F]{16})(?: [0-9a-fA-F]{8})?$", RegexOptions.CultureInvariant);
        if (!match.Success || !long.TryParse(match.Groups["offset"].Value, System.Globalization.NumberStyles.HexNumber, null, out long offset) || offset <= newline || offset >= bytes.LongLength)
            throw new InvalidDataException("Custom/encrypted Ren'Py archives need a dedicated adapter: " + archive.Name);
        using var source = new MemoryStream(bytes, (int)offset, bytes.Length - (int)offset, false);
        using var decompressor = new ZLibStream(source, CompressionMode.Decompress);
        using var index = new MemoryStream(); SafeData.CopyBounded(decompressor, index, 16L * 1024 * 1024);
        byte[] indexBytes = index.ToArray();
        for (int position = 0; position < indexBytes.Length; position++)
        {
            int start, length;
            if ((indexBytes[position] is (byte)'X' or (byte)'T' or (byte)'B') && position + 5 <= indexBytes.Length)
            { length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(indexBytes.AsSpan(position + 1)); start = position + 5; }
            else if ((indexBytes[position] is (byte)'U' or (byte)'C' or 0x8c) && position + 2 <= indexBytes.Length)
            { length = indexBytes[position + 1]; start = position + 2; }
            else if ((indexBytes[position] is 0x8d or 0x8e or 0x96) && position + 9 <= indexBytes.Length)
            {
                ulong wideLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(indexBytes.AsSpan(position + 1));
                if (wideLength > 16384) continue;
                length = (int)wideLength; start = position + 9;
            }
            else if (indexBytes[position] is (byte)'S' or (byte)'V')
            {
                start = position + 1; int end = Array.IndexOf(indexBytes, (byte)'\n', start);
                if (end < 0) continue; length = end - start;
            }
            else continue;
            if (length < 0 || length > 16384 || length > indexBytes.Length - start) continue;
            string literal = Encoding.UTF8.GetString(indexBytes, start, length).Trim('"', '\'');
            if (RuntimePackageTools.NativeName(literal)) throw new InvalidDataException("RPA index contains a native dependency requiring a dedicated port: " + archive.Name);
            position = start + length - 1;
        }
    }
}
