using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DustoreX.AutoConverter;

public enum TargetPlatform { Windows, MacOS }
public sealed record PackageRequest(string InputPath, string RuntimePath, string OutputPath, TargetPlatform Target, string GameName, string? RuntimeVersion = null);
public sealed record PackageResult(string OutputPath, string Status, string SourceSha256, string PayloadSha256, string RuntimeSha256, IReadOnlyList<string> Warnings);

public static class SafetyLimits
{
    public const int MaxEntries = 10000;
    public const long MaxUncompressedBytes = 1024L * 1024 * 1024;
    public const long MaxArchiveBytes = 1024L * 1024 * 1024;
    public const long MaxPayloadBytes = 512L * 1024 * 1024;
    public const long MaxScriptBytes = 4L * 1024 * 1024;
}

public sealed class AnalysisReport
{
    public string InputPath { get; init; } = "";
    public string Engine { get; init; } = "Unknown";
    public string DetectedPlatform { get; init; } = "Unknown";
    public string Route { get; init; } = "UnsupportedWithoutSource";
    public List<string> PayloadCandidates { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

public static class BuildAnalyzer
{
    public static AnalysisReport Analyze(string path, TargetPlatform? target = null)
    {
        path = Path.GetFullPath(path);
        var warnings = new List<string>();
        var candidates = new List<string>();
        string engine = "Unknown", platform = "Unknown", route = "UnsupportedWithoutSource";
        if (File.Exists(path) && !SafeData.IsZip(path))
        {
            using var input = File.OpenRead(path);
            platform = BinaryKind.Read(input);
            if (platform.StartsWith("Windows PE", StringComparison.Ordinal))
            {
                try
                {
                    using var payload = LovePayload.Open(path);
                    engine = "LÖVE"; candidates.Add("Embedded ZIP payload"); route = "RuntimeRepackCandidate";
                    warnings.AddRange(payload.Warnings);
                }
                catch (InvalidDataException ex) { warnings.Add("No validated portable LÖVE payload: " + ex.Message); }
            }
            if (engine == "Unknown") warnings.Add("A machine-code executable cannot be converted by changing its extension; obtain source or a build for the target platform.");
        }
        else
        {
            using var source = SafeData.Open(path, allowSymlinks: true);
            var names = source.Items.Select(e => e.Name).ToArray();
            bool mac = names.Any(n => n.Contains(".app/Contents/", StringComparison.OrdinalIgnoreCase)) || path.EndsWith(".app", StringComparison.OrdinalIgnoreCase);
            platform = mac ? "macOS app bundle" : names.Any(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ? "Windows" : "Portable or unknown";
            candidates.AddRange(names.Where(n => n.EndsWith(".love", StringComparison.OrdinalIgnoreCase)));
            if (candidates.Count > 0 || names.Contains("main.lua", StringComparer.Ordinal))
            {
                engine = "LÖVE"; route = "RuntimeRepackCandidate";
                try { using var payload = LovePayload.Open(path); warnings.AddRange(payload.Warnings); }
                catch (InvalidDataException ex) { route = "Blocked"; warnings.Add(ex.Message); }
            }
            else if (names.Any(n => n.EndsWith("project.godot", StringComparison.OrdinalIgnoreCase))) { engine = "Godot source"; route = "BuildFromSource"; }
            else if (names.Any(n => n.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))) { engine = "Godot"; route = "ConditionalRuntimeRepack"; warnings.Add("PCKs are exported for a chosen platform; runtime version, native extensions, encryption and platform resources require validation. This prototype does not repackage Godot."); }
            else if (names.Any(n => n.EndsWith("UnityPlayer.dll", StringComparison.OrdinalIgnoreCase) || n.Contains("_Data/", StringComparison.OrdinalIgnoreCase) || n.Contains("Contents/Resources/Data/", StringComparison.OrdinalIgnoreCase))) { engine = "Unity"; route = "BuildFromSource"; }
            else if (names.Any(n => n.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || n.Contains("Binaries/", StringComparison.OrdinalIgnoreCase))) { engine = "Unreal or native"; route = "BuildFromSource"; }
            else if (names.Any(n => n.Contains("renpy/", StringComparison.OrdinalIgnoreCase))) { engine = "Ren'Py"; route = "ConditionalRuntimeRepack"; warnings.Add("Ren'Py version and native dependencies need a dedicated adapter; this prototype only packages LÖVE."); }
            else if (names.Any(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))) { engine = "Web"; route = "PortableBrowserRuntime"; warnings.Add("Use a local HTTP server and compatible browser; native Electron/NW.js dependencies require inspection. No web wrapper is generated here."); }
        }
        warnings.Add("Detection uses file structure and headers and does not prove behavioral compatibility.");
        if (target.HasValue) warnings.Add("Requested target: " + target.Value + ". A target-device launch is a separate validation step.");
        return new AnalysisReport { InputPath = path, Engine = engine, DetectedPlatform = platform, Route = route, PayloadCandidates = candidates, Warnings = warnings };
    }
}

public static class LovePackager
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static PackageResult Package(PackageRequest request)
    {
        string input = Path.GetFullPath(request.InputPath), runtime = Path.GetFullPath(request.RuntimePath), output = Path.GetFullPath(request.OutputPath);
        SafeData.ValidateGameName(request.GameName);
        if (!Enum.IsDefined(request.Target)) throw new ArgumentException("Invalid target.");
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be a new .zip archive.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists; overwriting is forbidden.");
        SafeData.RequireSeparateOutput(output, input);
        SafeData.RequireSeparateOutput(output, runtime);
        SafeData.RejectReparseAncestors(output);
        using var payload = LovePayload.Open(input);
        using var runtimeData = SafeData.Open(runtime, allowSymlinks: request.Target == TargetPlatform.MacOS);
        var warnings = new List<string>(payload.Warnings)
        {
            "Packaged only: no game or runtime was executed, and the package has not been tested on the target OS.",
            "The runtime was explicitly supplied by the caller. Its SHA-256 pins the input; authenticity and runtime version are not independently verified.",
            "Static inspection cannot prove that Lua code has no platform-specific behavior or dynamically constructed native calls."
        };
        if (request.RuntimeVersion is null) warnings.Add("Runtime version is unknown; no newest-version compatibility assumption was made.");
        if (payload.DeclaredVersion is not null && request.RuntimeVersion is not null && payload.DeclaredVersion != request.RuntimeVersion)
            throw new InvalidDataException("Declared LÖVE version " + payload.DeclaredVersion + " does not match supplied runtime-version assumption " + request.RuntimeVersion + ".");
        string sourceHash = SafeData.HashPath(input), runtimeHash = runtimeData.Hash(), payloadHash = SafeData.Hash(payload.Bytes);
        var manifest = new
        {
            schemaVersion = 1, converter = "DustoreX.AutoConverter prototype", status = "Packaged", testedOnTarget = false,
            engine = "LÖVE", target = request.Target.ToString(), gameName = request.GameName,
            sourceSha256 = sourceHash, payloadSha256 = payloadHash, runtimeSha256 = runtimeHash,
            sourceHashMethod = Directory.Exists(input) ? "sorted-relative-path-and-content-sha256" : "file-sha256",
            runtimeHashMethod = Directory.Exists(runtime) ? "sorted-relative-path-and-content-sha256" : "file-sha256",
            declaredLoveVersion = payload.DeclaredVersion, runtimeVersionAssumption = request.RuntimeVersion,
            createdUtc = DateTimeOffset.UtcNow, warnings
        };
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".dustorex-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                if (request.Target == TargetPlatform.Windows) PackageWindows(runtimeData, payload.Bytes, request.GameName, zip);
                else PackageMac(runtimeData, payload.Bytes, request.GameName, zip, warnings);
                WriteEntry(zip, "package-manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json), 0);
            }
            if (request.Target == TargetPlatform.MacOS) MacZipMetadata.MarkUnixCreator(staging);
            File.Move(staging, output, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new PackageResult(output, "Packaged", sourceHash, payloadHash, runtimeHash, warnings);
    }

    private static void PackageWindows(SafeData runtime, byte[] payload, string name, ZipArchive output)
    {
        if (runtime.Items.Any(i => i.IsSymlink)) throw new InvalidDataException("Windows runtime contains symlinks.");
        var executables = runtime.Items.Where(i => !i.IsDirectory && Path.GetFileName(i.Name).Equals("love.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (executables.Length != 1) throw new InvalidDataException("Windows runtime must contain exactly one love.exe.");
        var executable = executables[0];
        using (var stream = executable.Open())
            if (!BinaryKind.Read(stream).StartsWith("Windows PE", StringComparison.Ordinal)) throw new InvalidDataException("love.exe is not a recognized PE executable.");
        string root = SafeData.ParentName(executable.Name);
        var files = runtime.Items.Where(i => !i.IsDirectory && SafeData.ParentName(i.Name).Equals(root, StringComparison.Ordinal)).ToArray();
        bool Has(string file) => files.Any(i => Path.GetFileName(i.Name).Equals(file, StringComparison.OrdinalIgnoreCase));
        if (!Has("love.dll") || !Has("lua51.dll") || !(Has("SDL2.dll") || Has("SDL3.dll")) || !Has("license.txt"))
            throw new InvalidDataException("Runtime must supply love.dll, lua51.dll, SDL2.dll or SDL3.dll, and license.txt beside love.exe; provide the complete official runtime.");
        if (runtime.Items.Any(i => i.Name.TrimEnd('/').EndsWith(".love", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Runtime already contains a game payload; provide a pristine runtime.");
        string gameEntry = name + "/" + name + ".exe";
        if (runtime.Items.Any(i => !ReferenceEquals(i, executable) && i.Name.StartsWith(root, StringComparison.Ordinal)
            && i.Name[root.Length..].TrimEnd('/').Split('/')[0].Normalize(NormalizationForm.FormC).Equals((name + ".exe").Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Generated executable collides with a runtime file.");
        var fused = output.CreateEntry(gameEntry, CompressionLevel.Optimal);
        using (var target = fused.Open())
        using (var source = executable.Open())
        {
            SafeData.CopyBounded(source, target, SafetyLimits.MaxUncompressedBytes);
            target.Write(payload);
        }
        foreach (var item in runtime.Items.Where(i => i.Name.StartsWith(root, StringComparison.Ordinal) && !ReferenceEquals(i, executable)))
        {
            string relative = item.Name[root.Length..];
            if (relative.Length == 0) continue;
            CopyEntry(output, name + "/" + relative, item);
        }
    }

    private static void PackageMac(SafeData runtime, byte[] payload, string name, ZipArchive output, List<string> warnings)
    {
        if (!runtime.IsArchive) throw new InvalidDataException("macOS runtime must be its original ZIP, preserving Unix modes and framework symlinks. A Windows-extracted directory is not accepted.");
        var roots = runtime.Items.Select(i => AppRoot(i.Name)).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("macOS runtime ZIP must contain exactly one .app bundle.");
        string root = roots[0]!;
        var plist = runtime.Items.SingleOrDefault(i => i.Name == root + "Contents/Info.plist") ?? throw new InvalidDataException("Missing Contents/Info.plist.");
        var document = ReadPlist(plist);
        var dict = document.Root?.Element("dict") ?? throw new InvalidDataException("Info.plist must contain a plist dictionary.");
        string exe = PlistValue(dict, "CFBundleExecutable") ?? throw new InvalidDataException("Missing CFBundleExecutable.");
        if (exe.Contains('/') || exe.Contains('\\') || exe is "." or "..") throw new InvalidDataException("Unsafe CFBundleExecutable.");
        var binary = runtime.Items.SingleOrDefault(i => i.Name == root + "Contents/MacOS/" + exe) ?? throw new InvalidDataException("CFBundleExecutable does not match a bundled executable.");
        if (binary.IsSymlink || ((binary.ExternalAttributes >> 16) & 0x49) == 0) throw new InvalidDataException("macOS executable must be a regular entry with a Unix executable bit.");
        using (var stream = binary.Open()) if (!BinaryKind.Read(stream).StartsWith("macOS Mach-O", StringComparison.Ordinal)) throw new InvalidDataException("macOS runtime executable is not Mach-O.");
        if (runtime.Items.Any(i => i.Name.StartsWith(root, StringComparison.Ordinal) && i.Name.TrimEnd('/').EndsWith(".love", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Runtime already contains a game payload; provide a pristine runtime.");
        string outputRoot = name + ".app/";
        foreach (var link in runtime.Items.Where(i => i.IsSymlink && i.Name.StartsWith(root, StringComparison.Ordinal)))
        {
            SafeData.ValidateSymlink(link, root);
            SafeData.ValidateSymlink(new DataItem(outputRoot + link.Name[root.Length..], link.Length, link.ExternalAttributes, false, link.Open), outputRoot);
        }
        SetPlistValue(dict, "CFBundleName", name);
        SetPlistValue(dict, "CFBundleDisplayName", name);
        SetPlistValue(dict, "CFBundleIdentifier", "org.dustorex.game." + SafeData.Hash(Encoding.UTF8.GetBytes(name))[..16].ToLowerInvariant());
        RemovePlistValue(dict, "CFBundleDocumentTypes");
        RemovePlistValue(dict, "UTExportedTypeDeclarations");
        foreach (var item in runtime.Items.Where(i => i.Name.StartsWith(root, StringComparison.Ordinal)))
        {
            string relative = item.Name[root.Length..];
            if (relative.StartsWith("Contents/_CodeSignature/", StringComparison.Ordinal) || relative == "Contents/CodeResources") continue;
            if (ReferenceEquals(item, plist))
            {
                using var bytes = new MemoryStream();
                using (var writer = XmlWriter.Create(bytes, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) document.Save(writer);
                WriteEntry(output, outputRoot + relative, bytes.ToArray(), item.ExternalAttributes);
            }
            else CopyEntry(output, outputRoot + relative, item);
        }
        WriteEntry(output, outputRoot + "Contents/Resources/game.love", payload, unchecked((int)(0x81A4u << 16)));
        warnings.Add("The macOS bundle was modified, so any original code signature is invalid. Signature resource entries were removed; final signing/notarization and launch must be validated on macOS. This tool does not bypass Gatekeeper.");
        warnings.Add("CFBundleExecutable and all runtime symlink bytes/Unix modes were preserved; this is an unsigned packaging artifact, not a verified macOS release.");
    }

    private static string? AppRoot(string name)
    {
        var parts = name.Split('/');
        for (int i = 0; i < parts.Length; i++) if (parts[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return string.Join('/', parts[..(i + 1)]) + "/";
        return null;
    }

    private static XDocument ReadPlist(DataItem item)
    {
        using var stream = item.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = SafetyLimits.MaxScriptBytes });
        return XDocument.Load(reader);
    }
    private static string? PlistValue(XElement dict, string key) => dict.Elements("key").FirstOrDefault(e => e.Value == key)?.ElementsAfterSelf().FirstOrDefault()?.Value;
    private static void RemovePlistValue(XElement dict, string key)
    {
        foreach (var node in dict.Elements("key").Where(e => e.Value == key).ToArray()) { node.ElementsAfterSelf().FirstOrDefault()?.Remove(); node.Remove(); }
    }
    private static void SetPlistValue(XElement dict, string key, string value) { RemovePlistValue(dict, key); dict.Add(new XElement("key", key), new XElement("string", value)); }
    private static void CopyEntry(ZipArchive zip, string name, DataItem item)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        entry.ExternalAttributes = item.ExternalAttributes;
        if (item.IsDirectory) return;
        using var source = item.Open();
        using var target = entry.Open();
        SafeData.CopyBounded(source, target, SafetyLimits.MaxUncompressedBytes);
    }
    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes, int attributes)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.ExternalAttributes = attributes;
        using var stream = entry.Open(); stream.Write(bytes);
    }
}

internal sealed class LovePayload : IDisposable
{
    public byte[] Bytes { get; private init; } = [];
    public string? DeclaredVersion { get; private set; }
    public List<string> Warnings { get; } = [];

    public static LovePayload Open(string input)
    {
        byte[] bytes;
        if (File.Exists(input) && !SafeData.IsZip(input)) bytes = EmbeddedZip.Read(input);
        else
        {
            using var data = SafeData.Open(input, allowSymlinks: true);
            if (data.Items.Any(i => i.Name == "main.lua"))
            {
                if (data.IsArchive) bytes = SafeData.ReadFile(input, SafetyLimits.MaxPayloadBytes);
                else
                {
                    if (data.Items.Any(i => i.IsSymlink)) throw new InvalidDataException("Game payload symlinks are not supported.");
                    using var buffer = new MemoryStream();
                    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
                        foreach (var item in data.Items)
                        {
                            var entry = zip.CreateEntry(item.Name, CompressionLevel.Optimal); entry.ExternalAttributes = item.ExternalAttributes;
                            if (item.IsDirectory) continue;
                            using var source = item.Open(); using var target = entry.Open(); SafeData.CopyBounded(source, target, SafetyLimits.MaxPayloadBytes);
                        }
                    bytes = buffer.ToArray();
                }
            }
            else
            {
                var candidates = data.Items.Where(i => !i.IsDirectory && i.Name.EndsWith(".love", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length != 1) throw new InvalidDataException("Need exactly one .love payload, or main.lua at the input root. Ambiguous payloads are not guessed.");
                if (candidates[0].IsSymlink) throw new InvalidDataException("A symlink is not an accepted game payload.");
                foreach (var sidecar in data.Items.Where(i => !i.IsDirectory && !ReferenceEquals(i, candidates[0]) && (i.Name.Contains("Contents/Resources/", StringComparison.Ordinal) || !i.Name.Contains("Contents/", StringComparison.Ordinal))))
                {
                    if (IsNativeName(sidecar.Name)) throw new InvalidDataException("Native sidecar outside .love needs a dedicated port: " + sidecar.Name);
                }
                using var stream = candidates[0].Open(); using var buffer = new MemoryStream(); SafeData.CopyBounded(stream, buffer, SafetyLimits.MaxPayloadBytes); bytes = buffer.ToArray();
            }
        }
        if (bytes.LongLength > SafetyLimits.MaxPayloadBytes) throw new InvalidDataException("Payload archive exceeds prototype budget.");
        var payload = new LovePayload { Bytes = bytes };
        using var dataPayload = SafeData.FromBytes(bytes, allowSymlinks: false);
        if (!dataPayload.Items.Any(i => i.Name == "main.lua" && !i.IsDirectory)) throw new InvalidDataException("LÖVE payload requires main.lua at archive root with exact case.");
        foreach (var item in dataPayload.Items.Where(i => !i.IsDirectory))
        {
            string ext = Path.GetExtension(item.Name).ToLowerInvariant();
            if (IsNativeName(item.Name))
                throw new InvalidDataException("Native game dependency requires a dedicated port: " + item.Name);
            using (var sniff = item.Open())
            {
                string binary = BinaryKind.Read(sniff);
                if (binary != "Unknown binary") throw new InvalidDataException("Native binary payload requires a dedicated port: " + item.Name);
            }
            if (ext is ".lua" or ".luac")
            {
                if (item.Length > SafetyLimits.MaxScriptBytes) throw new InvalidDataException("Script exceeds static-inspection budget: " + item.Name);
                using var stream = item.Open(); using var buffer = new MemoryStream(); SafeData.CopyBounded(stream, buffer, SafetyLimits.MaxScriptBytes); var scriptBytes = buffer.ToArray();
                if (scriptBytes.Length > 0 && scriptBytes[0] == 0x1b) throw new InvalidDataException("Compiled Lua bytecode cannot be safely inspected for portability: " + item.Name);
                string script = new UTF8Encoding(false, true).GetString(scriptBytes);
                if (Regex.IsMatch(script, @"\bffi\b|\bloadlib\b|\bos\s*\.\s*execute\b|\bio\s*\.\s*popen\b", RegexOptions.CultureInvariant))
                    throw new InvalidDataException("Potential native or external-process dependency needs manual porting: " + item.Name);
                if (item.Name == "conf.lua")
                {
                    payload.DeclaredVersion = LuaConfig.ReadSimpleDeclaredVersion(script);
                }
            }
            using var read = item.Open(); SafeData.CopyBounded(read, Stream.Null, SafetyLimits.MaxUncompressedBytes);
        }
        if (payload.DeclaredVersion is null) payload.Warnings.Add("No literal LÖVE t.version was found in conf.lua; required runtime version remains unknown.");
        payload.Warnings.Add("Only the selected .love payload is transferred; files outside it and existing saves are not migrated. A game that depends on sidecar data needs a dedicated adapter.");
        return payload;
    }
    private static bool IsNativeName(string name)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".dll" or ".dylib" or ".so" or ".exe" or ".node" or ".bundle" or ".a" or ".lib"
            || Regex.IsMatch(name, @"\.so(?:\.[0-9]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    public void Dispose() { }
}

// Best-effort inspection, never Lua evaluation. Only a simple love.conf body with
// an unambiguous literal assignment to its actual parameter establishes a version.
internal static class LuaConfig
{
    private readonly record struct Token(string Text, bool IsString = false);

    public static string? ReadSimpleDeclaredVersion(string source)
    {
        var tokens = Lex(source);
        int definitions = 0;
        for (int i = 0; i < tokens.Count; i++)
            if (Match(tokens, i, "function", "love", ".", "conf") || Match(tokens, i, "love", ".", "conf", "=")) definitions++;
        if (definitions != 1) return null;
        for (int i = 0; i + 7 < tokens.Count; i++)
        {
            int parameterAt;
            if (Match(tokens, i, "function", "love", ".", "conf", "(")) parameterAt = i + 5;
            else if (Match(tokens, i, "love", ".", "conf", "=", "function", "(")) parameterAt = i + 6;
            else continue;
            if (parameterAt + 1 >= tokens.Count || tokens[parameterAt].IsString || tokens[parameterAt + 1].Text != ")") return null;
            string parameter = tokens[parameterAt].Text;
            var versions = new HashSet<string>(StringComparer.Ordinal);
            for (int j = parameterAt + 2; j < tokens.Count; j++)
            {
                if (!tokens[j].IsString && tokens[j].Text == "end") return versions.Count == 1 ? versions.Single() : null;
                if (!tokens[j].IsString && tokens[j].Text is "if" or "for" or "while" or "repeat" or "do" or "function") return null;
                if (Match(tokens, j, parameter, ".", "version", "="))
                {
                    if (j + 4 >= tokens.Count || !tokens[j + 4].IsString || !Regex.IsMatch(tokens[j + 4].Text, @"^[0-9]+\.[0-9]+(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)) return null;
                    if (j + 5 < tokens.Count && !tokens[j + 5].IsString && tokens[j + 5].Text is "." or "+" or "-" or "*" or "/" or "^" or "%" or "[" or "(") return null;
                    versions.Add(tokens[j + 4].Text);
                }
            }
            return null;
        }
        return null;
    }

    private static bool Match(List<Token> tokens, int start, params string[] expected)
    {
        if (start + expected.Length > tokens.Count) return false;
        for (int i = 0; i < expected.Length; i++) if (tokens[start + i].IsString || tokens[start + i].Text != expected[i]) return false;
        return true;
    }

    private static List<Token> Lex(string source)
    {
        var tokens = new List<Token>();
        int position = 0;
        while (position < source.Length)
        {
            char current = source[position];
            if (char.IsWhiteSpace(current)) { position++; continue; }
            if (current == '-' && position + 1 < source.Length && source[position + 1] == '-')
            {
                position += 2;
                if (TryLongString(source, ref position, out _)) continue;
                while (position < source.Length && source[position] is not '\r' and not '\n') position++;
                continue;
            }
            if (current == '[' && TryLongString(source, ref position, out string? longString)) { tokens.Add(new Token(longString!, true)); continue; }
            if (current is '\'' or '"')
            {
                char quote = current; position++; var text = new StringBuilder();
                while (position < source.Length && source[position] != quote)
                {
                    char value = source[position++];
                    if (value == '\\' && position < source.Length) { text.Append('\\'); text.Append(source[position++]); }
                    else text.Append(value);
                }
                if (position < source.Length) position++;
                tokens.Add(new Token(text.ToString(), true));
                continue;
            }
            if (char.IsLetter(current) || current == '_')
            {
                int start = position++;
                while (position < source.Length && (char.IsLetterOrDigit(source[position]) || source[position] == '_')) position++;
                tokens.Add(new Token(source[start..position]));
            }
            else { tokens.Add(new Token(current.ToString())); position++; }
            if (tokens.Count > 500000) return [];
        }
        return tokens;
    }

    private static bool TryLongString(string source, ref int position, out string? text)
    {
        text = null;
        if (position >= source.Length || source[position] != '[') return false;
        int cursor = position + 1;
        while (cursor < source.Length && source[cursor] == '=') cursor++;
        if (cursor >= source.Length || source[cursor] != '[') return false;
        string closing = "]" + new string('=', cursor - position - 1) + "]";
        int start = cursor + 1, end = source.IndexOf(closing, start, StringComparison.Ordinal);
        if (end < 0) { position = source.Length; text = ""; return true; }
        text = source[start..end]; position = end + closing.Length; return true;
    }
}

internal sealed class DataItem(string name, long length, int externalAttributes, bool isDirectory, Func<Stream> open)
{
    public string Name { get; } = name;
    public long Length { get; } = length;
    public int ExternalAttributes { get; } = externalAttributes;
    public bool IsDirectory { get; } = isDirectory;
    public bool IsSymlink => ((ExternalAttributes >> 16) & 0xF000) == 0xA000;
    public Stream Open() => open();
}

internal sealed class SafeData : IDisposable
{
    private ZipArchive? _archive;
    private Stream? _stream;
    private string? _path;
    public bool IsArchive => _archive is not null;
    public List<DataItem> Items { get; } = [];

    public static SafeData Open(string path, bool allowSymlinks)
    {
        path = Path.GetFullPath(path);
        RejectReparseAncestors(path);
        var data = new SafeData { _path = path };
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Filesystem symlinks/reparse points are refused: " + file);
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    string name = Path.GetRelativePath(path, file).Replace('\\', '/') + (directory ? "/" : "");
                    data.Items.Add(new DataItem(name, directory ? 0 : new FileInfo(file).Length, 0, directory, () => File.OpenRead(file)));
                    if (data.Items.Count > SafetyLimits.MaxEntries) throw new InvalidDataException("Too many input entries.");
                }
            }
            else
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Input does not exist.", path);
                if (new FileInfo(path).Length > SafetyLimits.MaxArchiveBytes) throw new InvalidDataException("Archive exceeds prototype byte budget.");
                data._stream = File.OpenRead(path);
                data._archive = new ZipArchive(data._stream, ZipArchiveMode.Read, true);
                data.ReadEntries();
            }
            data.Validate(allowSymlinks);
            return data;
        }
        catch { data.Dispose(); throw; }
    }

    public static SafeData FromBytes(byte[] bytes, bool allowSymlinks)
    {
        var data = new SafeData { _stream = new MemoryStream(bytes, false) };
        try { data._archive = new ZipArchive(data._stream, ZipArchiveMode.Read, true); data.ReadEntries(); data.Validate(allowSymlinks); return data; }
        catch { data.Dispose(); throw; }
    }

    private void ReadEntries()
    {
        if (_archive!.Entries.Count > SafetyLimits.MaxEntries) throw new InvalidDataException("Too many archive entries.");
        foreach (var entry in _archive.Entries)
        {
            if (entry.FullName.Contains('\\')) throw new InvalidDataException("ZIP entries must use forward slashes: " + entry.FullName);
            string name = entry.FullName;
            Items.Add(new DataItem(name, entry.Length, entry.ExternalAttributes, name.EndsWith('/'), entry.Open));
        }
    }

    private void Validate(bool allowSymlinks)
    {
        var seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in Items)
        {
            ValidateName(item.Name);
            string key = item.Name.TrimEnd('/').Normalize(NormalizationForm.FormC);
            if (!seen.TryAdd(key, item.IsDirectory)) throw new InvalidDataException("Duplicate or case/Unicode-colliding path: " + item.Name);
            if (item.IsSymlink && !allowSymlinks) throw new InvalidDataException("Payload symlink is refused: " + item.Name);
            if (item.IsSymlink && item.IsDirectory) throw new InvalidDataException("Conflicting symlink/directory metadata: " + item.Name);
            if (item.Length < 0 || item.Length > SafetyLimits.MaxUncompressedBytes - total) throw new InvalidDataException("Uncompressed archive budget exceeded.");
            total += item.Length;
        }
        if (IsArchive)
        {
            long actualTotal = 0;
            foreach (var item in Items.Where(i => !i.IsDirectory))
            {
                using var stream = item.Open();
                long actual = CopyBounded(stream, Stream.Null, SafetyLimits.MaxUncompressedBytes - actualTotal);
                if (actual != item.Length) throw new InvalidDataException("Declared and actual ZIP length differ: " + item.Name);
                actualTotal += actual;
            }
        }
        foreach (var item in Items)
        {
            string parent = ParentName(item.Name.TrimEnd('/')).TrimEnd('/');
            while (parent.Length > 0)
            {
                if (seen.TryGetValue(parent.Normalize(NormalizationForm.FormC), out bool directory) && !directory)
                    throw new InvalidDataException("File/directory path collision: " + item.Name);
                parent = ParentName(parent).TrimEnd('/');
            }
        }
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains('\\') || name.Length > 1024 || name.Any(c => c < 32))
            throw new InvalidDataException("Unsafe archive path: " + name);
        foreach (string part in name.TrimEnd('/').Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0)
                throw new InvalidDataException("Unsafe or Windows-incompatible archive path: " + name);
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Reserved Windows path: " + name);
        }
    }
    public static void ValidateGameName(string name)
    {
        if (name.Length is < 1 or > 80 || name.Contains('/') || name.Contains('\\')) throw new ArgumentException("Game name must be one safe path component, 1–80 characters.");
        ValidateName(name);
    }
    public static string ParentName(string name) => name.LastIndexOf('/') is var index && index >= 0 ? name[..(index + 1)] : "";
    public static void ValidateSymlink(DataItem item, string root)
    {
        if (item.Length > 4096) throw new InvalidDataException("Oversized symlink target.");
        using var source = item.Open(); using var bytes = new MemoryStream(); CopyBounded(source, bytes, 4096);
        string target = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        if (target.Length == 0 || target.StartsWith('/') || target.Contains('\\') || target.Contains(':') || target.Any(c => c < 32)) throw new InvalidDataException("Unsafe runtime symlink: " + item.Name);
        var parts = ParentName(item.Name).TrimEnd('/').Split('/').ToList();
        foreach (var part in target.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException("Escaping runtime symlink."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        string resolved = string.Join('/', parts) + "/";
        if (!resolved.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Runtime symlink escapes its .app bundle: " + item.Name);
    }

    public string Hash() => _path is not null && File.Exists(_path) ? HashFile(_path) : HashItems(Items);
    public static string HashPath(string path)
    {
        if (File.Exists(path)) return HashFile(path);
        using var data = Open(path, false); return HashItems(data.Items);
    }
    private static string HashItems(IEnumerable<DataItem> items)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in items.OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            digest.AppendData(Encoding.UTF8.GetBytes(item.Name + "\0"));
            if (!item.IsDirectory) { using var stream = item.Open(); digest.AppendData(SHA256.HashData(stream)); }
        }
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    public static bool IsZip(string path)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path); return file.ReadByte() == 'P' && file.ReadByte() == 'K';
    }
    public static byte[] ReadFile(string path, long limit)
    {
        RejectReparseAncestors(path);
        using var file = File.OpenRead(path); if (file.Length > limit) throw new InvalidDataException("File exceeds prototype byte budget.");
        using var buffer = new MemoryStream(); CopyBounded(file, buffer, limit); return buffer.ToArray();
    }
    public static long CopyBounded(Stream input, Stream output, long limit)
    {
        byte[] buffer = new byte[65536]; long total = 0;
        while (true) { int count = input.Read(buffer); if (count == 0) return total; if (count > limit - total) throw new InvalidDataException("Actual stream exceeds byte budget."); output.Write(buffer, 0, count); total += count; }
    }
    public static void RequireSeparateOutput(string output, string source)
    {
        if (output.Equals(source, StringComparison.OrdinalIgnoreCase) || Directory.Exists(source) && output.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Output must be outside input/runtime and must not overwrite a source.");
    }
    public static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse-point paths are refused: " + current);
    }
    public void Dispose() { _archive?.Dispose(); _stream?.Dispose(); }
}

// .NET 8 writes VersionMadeBy's host platform as the current host (Windows here),
// even when Unix ExternalAttributes are supplied. Unix extractors need both.
internal static class MacZipMetadata
{
    public static void MarkUnixCreator(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int tailLength = (int)Math.Min(file.Length, 22L + ushort.MaxValue);
        byte[] tail = new byte[tailLength]; file.Position = file.Length - tailLength; file.ReadExactly(tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) == 0x06054b50 && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20, 2)) == tail.Length) { end = i; break; }
        if (end < 0) throw new InvalidDataException("Cannot finalize macOS ZIP: missing end record.");
        var eocd = tail.AsSpan(end);
        int entries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
        long centralSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        long centralStart = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        if (entries == ushort.MaxValue || centralSize == uint.MaxValue || centralStart == uint.MaxValue) throw new InvalidDataException("ZIP64 output is outside prototype limits.");
        long endPosition = file.Length - tailLength + end;
        if (centralStart + centralSize != endPosition) throw new InvalidDataException("Cannot finalize macOS ZIP: invalid directory bounds.");
        long cursor = centralStart;
        byte[] header = new byte[46];
        for (int i = 0; i < entries; i++)
        {
            if (cursor > endPosition - 46) throw new InvalidDataException("Truncated ZIP central directory.");
            file.Position = cursor; file.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50) throw new InvalidDataException("Invalid ZIP central entry.");
            file.Position = cursor + 5; file.WriteByte(3); // VersionMadeBy high byte: Unix.
            cursor += 46L + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
        }
        if (cursor != endPosition) throw new InvalidDataException("Unexpected macOS ZIP central directory data.");
    }
}

internal static class BinaryKind
{
    public static string Read(Stream input)
    {
        Span<byte> header = stackalloc byte[64]; int count = input.ReadAtLeast(header, 64, throwOnEndOfStream: false);
        if (count >= 64 && header[0] == 'M' && header[1] == 'Z')
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(header[60..]);
            if (offset >= 64 && offset <= 16 * 1024 * 1024 && (!input.CanSeek || offset <= input.Length - 24))
            {
                if (input.CanSeek) input.Position = offset;
                else
                {
                    byte[] discard = new byte[4096]; int remaining = offset - count;
                    while (remaining > 0) { int skipped = input.Read(discard, 0, Math.Min(discard.Length, remaining)); if (skipped == 0) return "Unknown binary"; remaining -= skipped; }
                }
                Span<byte> pe = stackalloc byte[24];
                int peCount = input.ReadAtLeast(pe, 24, throwOnEndOfStream: false);
                if (peCount == 24 && pe[0] == 'P' && pe[1] == 'E' && pe[2] == 0 && pe[3] == 0)
                {
                    string? architecture = BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) switch { 0x14c => "x86", 0x8664 => "x64", 0xaa64 => "arm64", _ => null };
                    if (architecture is not null) return "Windows PE " + architecture;
                }
            }
        }
        if (count >= 4)
        {
            uint magic = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA) return "macOS Mach-O";
            if (magic == 0x7f454c46) return "Linux ELF";
        }
        return "Unknown binary";
    }
}

internal static class EmbeddedZip
{
    public static byte[] Read(string path)
    {
        var bytes = SafeData.ReadFile(path, SafetyLimits.MaxArchiveBytes);
        using (var stream = new MemoryStream(bytes, false)) if (!BinaryKind.Read(stream).StartsWith("Windows PE", StringComparison.Ordinal)) throw new InvalidDataException("No standalone .love ZIP or validated fused Windows PE input.");
        int minimum = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        for (int offset = bytes.Length - 22; offset >= minimum; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) != 0x06054b50) continue;
            var eocd = bytes.AsSpan(offset);
            if (offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(eocd[20..]) != bytes.Length) continue;
            ushort disk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[4..]), centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd[6..]);
            ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(eocd[10..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]), location = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
            if (disk != 0 || centralDisk != 0 || entries == 0 || entries == ushort.MaxValue || size == uint.MaxValue || location == uint.MaxValue || entries != BinaryPrimitives.ReadUInt16LittleEndian(eocd[8..]))
                throw new InvalidDataException("Split/ZIP64 fused payloads are outside this prototype.");
            long start = offset - (long)size - location;
            long central = start + location;
            if (start < 64 || central < start || central + size != offset || central > bytes.Length - 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)central, 4)) != 0x02014b50)
                throw new InvalidDataException("Invalid fused ZIP directory offsets.");
            if (bytes.Length - start > SafetyLimits.MaxPayloadBytes) throw new InvalidDataException("Embedded payload exceeds prototype budget.");
            var payload = bytes.AsSpan((int)start).ToArray();
            using var valid = SafeData.FromBytes(payload, false);
            if (valid.Items.Count != entries || !valid.Items.Any(i => i.Name == "main.lua")) throw new InvalidDataException("Embedded ZIP is not an unambiguous root-main.lua LÖVE payload.");
            return payload;
        }
        throw new InvalidDataException("No validated appended ZIP payload; signed/ZIP64/custom fused executables need a dedicated adapter.");
    }
}
