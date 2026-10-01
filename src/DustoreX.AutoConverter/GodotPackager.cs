using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DustoreX.AutoConverter;

public sealed record GodotInspection(string EngineVersion, uint PackFormat, string SourceKind, IReadOnlyList<string> Features, string PayloadSha256, IReadOnlyList<string> Warnings)
{
    public string RuntimeVersion => EngineVersion;
}

/// <summary>Repackages inspected Godot 4 GDScript resource packs with explicitly supplied matching desktop runtimes.</summary>
public static class GodotPackager
{
    public static GodotInspection Inspect(string path)
    {
        var pack = GodotPck.ReadInput(Path.GetFullPath(path));
        return new GodotInspection(pack.EngineVersion, pack.Format, pack.SourceKind, pack.Features, SafeData.Hash(pack.Bytes), pack.Warnings);
    }

    public static PackageResult Package(PackageRequest request)
    {
        string input = Path.GetFullPath(request.InputPath), runtimePath = Path.GetFullPath(request.RuntimePath), output = Path.GetFullPath(request.OutputPath);
        SafeData.ValidateGameName(request.GameName);
        if (!Enum.IsDefined(request.Target)) throw new ArgumentException("Invalid Godot target.");
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Godot output must be a new ZIP.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists; overwriting is forbidden.");
        SafeData.RequireSeparateOutput(output, input); SafeData.RequireSeparateOutput(output, runtimePath); SafeData.RejectReparseAncestors(output);
        if (string.IsNullOrWhiteSpace(request.RuntimeVersion)) throw new InvalidDataException("Godot requires an explicit matching --runtime-version, such as 4.5.0. No runtime is downloaded or executed to guess its version.");
        var pack = GodotPck.ReadInput(input);
        if (NormalizeVersion(request.RuntimeVersion) != pack.EngineVersion)
            throw new InvalidDataException("Godot pack engine " + pack.EngineVersion + " does not match runtime-version assumption " + request.RuntimeVersion + ".");
        using var runtime = GodotRuntime.Open(runtimePath, request.Target);
        if (runtime.Version is not null && NormalizeVersion(runtime.Version) != pack.EngineVersion)
            throw new InvalidDataException("Templates version.txt does not match the Godot pack engine.");
        var warnings = new List<string>
        {
            "Packaged only: no runtime or game was executed, and the package has not been tested on the target OS.",
            "Godot GDScript/PCK resources may still contain platform-specific behavior, rendering settings or imported textures. A target-device launch is required.",
            "Only the inspected main PCK is transferred; existing saves and external sidecar data are not migrated.",
            "The caller explicitly supplied the runtime. SHA-256 pins its content; runtime authenticity and exact compiled features are not independently certified."
        };
        warnings.AddRange(pack.Warnings);
        if (request.Target == TargetPlatform.MacOS)
        {
            warnings.Add("The modified macOS bundle has no valid original bundle signature. Final signing/notarization and launch need macOS validation; this tool does not bypass Gatekeeper.");
            warnings.Add("Template privacy placeholders are removed without inventing tracking/data-collection declarations. Review privacy metadata and permissions before publishing the macOS app.");
        }
        string sourceHash = SafeData.HashPath(input), payloadHash = SafeData.Hash(pack.Bytes), runtimeHash = runtime.Hash;
        var manifest = new
        {
            schemaVersion = 1, converter = "DustoreX.AutoConverter Godot backend", status = "Packaged", testedOnTarget = false,
            engine = "Godot", engineVersion = pack.EngineVersion, packFormat = pack.Format, target = request.Target.ToString(), gameName = request.GameName,
            sourceSha256 = sourceHash, payloadSha256 = payloadHash, runtimeSha256 = runtimeHash,
            runtimeHashMethod = runtime.HashMethod, runtimeVersionAssumption = request.RuntimeVersion, templatesDeclaredVersion = runtime.Version,
            projectFeatures = pack.Features, sourceKind = pack.SourceKind, createdUtc = DateTimeOffset.UtcNow, warnings
        };
        string parent = Path.GetDirectoryName(output)!; Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".dustorex-godot-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                if (request.Target == TargetPlatform.Windows) WriteWindows(zip, runtime, pack.Bytes, request.GameName);
                else WriteMac(zip, runtime, pack.Bytes, request.GameName);
                foreach (var notice in runtime.ExtraFiles) Copy(zip, "runtime-notices/" + Path.GetFileName(notice.Name), notice);
                Write(zip, "package-manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), 0);
                Write(zip, "godot-license.txt", Encoding.UTF8.GetBytes(GodotLicense), 0);
            }
            if (request.Target == TargetPlatform.MacOS) MacZipMetadata.MarkUnixCreator(staging);
            File.Move(staging, output, overwrite: false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new PackageResult(output, "Packaged", sourceHash, payloadHash, runtimeHash, warnings);
    }

    private static string NormalizeVersion(string version)
    {
        var match = Regex.Match(version.Trim(), @"^(?<major>[0-9]+)\.(?<minor>[0-9]+)(?:\.(?<patch>[0-9]+))?(?:[.-](?:stable|official)(?:[.a-zA-Z0-9-]*)?)?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new InvalidDataException("Expected an explicit stable Godot runtime version, e.g. 4.5.0.");
        return int.Parse(match.Groups["major"].Value) + "." + int.Parse(match.Groups["minor"].Value) + "." + (match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0);
    }

    private static void WriteWindows(ZipArchive zip, GodotRuntime runtime, byte[] pack, string name)
    {
        var binary = runtime.Executable ?? throw new InvalidDataException("Missing Windows Godot runtime.");
        byte[] bytes = Read(binary, SafetyLimits.MaxPayloadBytes);
        using (var stream = new MemoryStream(bytes, false)) if (!BinaryKind.Read(stream).StartsWith("Windows PE", StringComparison.Ordinal)) throw new InvalidDataException("Windows Godot runtime is not PE.");
        if (GodotPck.Locate(bytes) is not null) throw new InvalidDataException("Runtime already contains an embedded PCK; provide a pristine export template.");
        Write(zip, name + "/" + name + ".exe", bytes, binary.ExternalAttributes);
        Write(zip, name + "/" + name + ".pck", pack, 0);
        foreach (var item in runtime.ExtraFiles)
        {
            string leaf = Path.GetFileName(item.Name);
            if (leaf.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase) || leaf.Equals(name + ".pck", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime sidecar collides with generated Godot output.");
            Copy(zip, name + "/" + leaf, item);
        }
    }

    private static void WriteMac(ZipArchive zip, GodotRuntime runtime, byte[] pack, string name)
    {
        var data = runtime.MacData ?? throw new InvalidDataException("macOS Godot runtime requires an original app/template ZIP.");
        var roots = data.Items.Select(i => AppRoot(i.Name)).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("macOS runtime ZIP must contain exactly one .app root.");
        string root = roots[0]!, newRoot = name + ".app/";
        if (data.Items.Any(i => i.Name.StartsWith(root, StringComparison.Ordinal) && i.Name.TrimEnd('/').EndsWith(".pck", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("macOS runtime already contains a PCK; supply a pristine template.");
        var plist = data.Items.SingleOrDefault(i => i.Name == root + "Contents/Info.plist") ?? throw new InvalidDataException("Missing macOS runtime Info.plist.");
        string xml = Encoding.UTF8.GetString(Read(plist, SafetyLimits.MaxScriptBytes));
        bool template = xml.Contains("$binary", StringComparison.Ordinal);
        DataItem? selected;
        if (template)
        {
            var release = data.Items.Where(i => i.Name.StartsWith(root + "Contents/MacOS/godot_macos_release.", StringComparison.Ordinal) && !i.IsDirectory).ToArray();
            selected = release.SingleOrDefault(i => i.Name.EndsWith(".universal", StringComparison.Ordinal));
            if (selected is null && release.Length == 1) selected = release[0];
            if (selected is null) throw new InvalidDataException("Need one universal Godot macOS release template; ambiguous architectures are refused.");
            string xmlName = System.Security.SecurityElement.Escape(name)!;
            xml = xml.Replace("$binary", xmlName, StringComparison.Ordinal).Replace("$name", xmlName, StringComparison.Ordinal)
                .Replace("$bundle_identifier", "org.dustorex.game." + SafeData.Hash(Encoding.UTF8.GetBytes(name))[..16], StringComparison.Ordinal)
                .Replace("$short_version", "1.0", StringComparison.Ordinal).Replace("$version", "1.0", StringComparison.Ordinal)
                .Replace("$signature", "????", StringComparison.Ordinal).Replace("$min_version_arm64", "11.0", StringComparison.Ordinal)
                .Replace("$min_version_x86_64", "10.13", StringComparison.Ordinal).Replace("$highres", "<true/>", StringComparison.Ordinal)
                .Replace("$usage_descriptions", "", StringComparison.Ordinal).Replace("$additional_plist_content", "", StringComparison.Ordinal)
                .Replace("$liquid_glass_icon", "", StringComparison.Ordinal)
                .Replace("$copyright", "", StringComparison.Ordinal).Replace("$app_category", "games", StringComparison.Ordinal);
            // Xcode metadata placeholders are informational; remove their unresolved dictionary pairs below.
        }
        else selected = null;
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = SafetyLimits.MaxScriptBytes });
        var document = XDocument.Load(reader);
        var dict = document.Root?.Element("dict") ?? throw new InvalidDataException("Info.plist lacks a dictionary.");
        string executable = Value(dict, "CFBundleExecutable") ?? throw new InvalidDataException("Missing CFBundleExecutable.");
        if (executable.Contains('/') || executable.Contains('\\') || executable is "." or "..") throw new InvalidDataException("Unsafe macOS executable name.");
        selected ??= data.Items.SingleOrDefault(i => i.Name == root + "Contents/MacOS/" + executable);
        if (selected is null || selected.IsSymlink || ((selected.ExternalAttributes >> 16) & 0x49) == 0) throw new InvalidDataException("macOS executable is missing, symlinked or lacks Unix executable mode.");
        var binaryBytes = Read(selected, SafetyLimits.MaxPayloadBytes);
        using (var stream = new MemoryStream(binaryBytes, false)) if (!BinaryKind.Read(stream).StartsWith("macOS Mach-O", StringComparison.Ordinal)) throw new InvalidDataException("macOS Godot runtime is not Mach-O.");
        if (GodotPck.Locate(binaryBytes) is not null) throw new InvalidDataException("macOS runtime contains an embedded game PCK.");
        foreach (var node in dict.Elements("key").ToArray())
            if (node.ElementsAfterSelf().FirstOrDefault() is XElement value && value.Value.Contains('$')) { value.Remove(); node.Remove(); }
        ValidatePlistDictionary(dict);
        Set(dict, "CFBundleExecutable", name); Set(dict, "CFBundleName", name); Set(dict, "CFBundleDisplayName", name);
        Set(dict, "CFBundleIdentifier", "org.dustorex.game." + SafeData.Hash(Encoding.UTF8.GetBytes(name))[..16]);
        Remove(dict, "CFBundleDocumentTypes"); Remove(dict, "UTExportedTypeDeclarations");
        foreach (var item in data.Items.Where(i => i.Name.StartsWith(root, StringComparison.Ordinal)))
        {
            string relative = item.Name[root.Length..];
            if (relative.StartsWith("Contents/_CodeSignature/", StringComparison.Ordinal) || relative == "Contents/CodeResources") continue;
            if (relative.StartsWith("Contents/MacOS/", StringComparison.Ordinal) && !item.IsDirectory)
            {
                if (ReferenceEquals(item, selected)) Write(zip, newRoot + "Contents/MacOS/" + name, binaryBytes, item.ExternalAttributes);
                continue; // Debug/other architecture binaries from pristine templates are not bundled.
            }
            if (item.IsSymlink)
            {
                SafeData.ValidateSymlink(item, root);
                SafeData.ValidateSymlink(new DataItem(newRoot + relative, item.Length, item.ExternalAttributes, false, item.Open), newRoot);
            }
            if (ReferenceEquals(item, plist))
            {
                using var buffer = new MemoryStream();
                using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) document.Save(writer);
                Write(zip, newRoot + relative, buffer.ToArray(), item.ExternalAttributes);
            }
            else if (relative == "Contents/Resources/PrivacyInfo.xcprivacy")
            {
                string privacy = Encoding.UTF8.GetString(Read(item, SafetyLimits.MaxScriptBytes));
                if (template) privacy = privacy.Replace("$priv_tracking", "", StringComparison.Ordinal).Replace("$priv_collection", "", StringComparison.Ordinal);
                using var privacyReader = XmlReader.Create(new StringReader(privacy), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = SafetyLimits.MaxScriptBytes });
                var privacyDocument = XDocument.Load(privacyReader);
                var privacyDict = privacyDocument.Root?.Element("dict") ?? throw new InvalidDataException("Invalid Godot privacy metadata.");
                ValidatePlistDictionary(privacyDict);
                Write(zip, newRoot + relative, Encoding.UTF8.GetBytes(privacy), item.ExternalAttributes);
            }
            else Copy(zip, newRoot + relative, item);
        }
        Write(zip, newRoot + "Contents/Resources/" + name + ".pck", pack, unchecked((int)(0x81A4u << 16)));
    }

    private static string? AppRoot(string name) { var pieces = name.Split('/'); for (int i = 0; i < pieces.Length; i++) if (pieces[i].EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return string.Join('/', pieces[..(i + 1)]) + "/"; return null; }
    private static string? Value(XElement dict, string key) => dict.Elements("key").FirstOrDefault(e => e.Value == key)?.ElementsAfterSelf().FirstOrDefault()?.Value;
    private static void Remove(XElement dict, string key) { foreach (var node in dict.Elements("key").Where(e => e.Value == key).ToArray()) { node.ElementsAfterSelf().FirstOrDefault()?.Remove(); node.Remove(); } }
    private static void Set(XElement dict, string key, string value) { Remove(dict, key); dict.Add(new XElement("key", key), new XElement("string", value)); }
    private static void ValidatePlistDictionary(XElement dict)
    {
        foreach (var nested in dict.DescendantsAndSelf("dict"))
        {
            if (nested.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value))) throw new InvalidDataException("Unresolved template text in plist dictionary.");
            var elements = nested.Elements().ToArray();
            if (elements.Length % 2 != 0) throw new InvalidDataException("Malformed plist dictionary pairs.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < elements.Length; i += 2)
                if (elements[i].Name != "key" || !seen.Add(elements[i].Value) || elements[i + 1].Name == "key") throw new InvalidDataException("Invalid/duplicate plist dictionary key.");
        }
    }
    internal static byte[] Read(DataItem item, long limit) { if (item.Length > limit) throw new InvalidDataException("Godot entry exceeds budget: " + item.Name); using var source = item.Open(); using var buffer = new MemoryStream(); SafeData.CopyBounded(source, buffer, limit); return buffer.ToArray(); }
    private static void Copy(ZipArchive zip, string name, DataItem item) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.ExternalAttributes = item.ExternalAttributes; if (item.IsDirectory) return; using var source = item.Open(); using var target = entry.Open(); SafeData.CopyBounded(source, target, SafetyLimits.MaxUncompressedBytes); }
    private static void Write(ZipArchive zip, string name, byte[] bytes, int attributes) { var entry = zip.CreateEntry(name, CompressionLevel.Optimal); entry.ExternalAttributes = attributes; using var target = entry.Open(); target.Write(bytes); }
    private const string GodotLicense = "Godot Engine — https://godotengine.org/license/\nCopyright (c) 2014-present Godot Engine contributors.\nCopyright (c) 2007-2014 Juan Linietsky, Ariel Manzur.\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\nThe above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.\n\nTHE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.\n\nGodot incorporates third-party components. Include the supplied COPYRIGHT.txt and relevant license notices when distributing a game.\n";
}

internal sealed class GodotRuntime : IDisposable
{
    private Stream? _stream;
    private ZipArchive? _outer;
    public DataItem? Executable { get; private set; }
    public SafeData? MacData { get; private set; }
    public List<DataItem> ExtraFiles { get; } = [];
    public string Hash { get; private set; } = "";
    public string HashMethod { get; private set; } = "file-sha256";
    public string? Version { get; private set; }

    public static GodotRuntime Open(string path, TargetPlatform target)
    {
        SafeData.RejectReparseAncestors(path);
        var runtime = new GodotRuntime();
        try
        {
            var items = new List<DataItem>();
            if (Directory.Exists(path))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Godot runtime directories cannot contain reparse points.");
                    if ((attributes & FileAttributes.Directory) != 0) continue;
                    items.Add(new DataItem(Path.GetRelativePath(path, entry).Replace('\\', '/'), new FileInfo(entry).Length, 0, false, () => File.OpenRead(entry)));
                    if (items.Count > SafetyLimits.MaxEntries) throw new InvalidDataException("Too many Godot runtime files.");
                }
                runtime.HashMethod = "selected-runtime-and-notices-sha256";
            }
            else if (SafeData.IsZip(path))
            {
                if (new FileInfo(path).Length > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("Godot template archive exceeds 2 GiB download budget.");
                runtime._stream = File.OpenRead(path); runtime._outer = new ZipArchive(runtime._stream, ZipArchiveMode.Read, true);
                if (runtime._outer.Entries.Count > SafetyLimits.MaxEntries) throw new InvalidDataException("Too many Godot template entries.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in runtime._outer.Entries)
                {
                    SafeData.ValidateName(entry.FullName);
                    if (!seen.Add(entry.FullName.TrimEnd('/').Normalize(NormalizationForm.FormC))) throw new InvalidDataException("Godot template archive has duplicate paths.");
                    items.Add(new DataItem(entry.FullName, entry.Length, entry.ExternalAttributes, entry.FullName.EndsWith('/'), entry.Open));
                }
                runtime.Hash = SafeData.HashPath(path);
            }
            else
            {
                if (!File.Exists(path)) throw new FileNotFoundException("Godot runtime not found.", path);
                items.Add(new DataItem(Path.GetFileName(path), new FileInfo(path).Length, 0, false, () => File.OpenRead(path)));
                runtime.Hash = SafeData.HashPath(path);
            }
            var versionFile = items.SingleOrDefault(i => Path.GetFileName(i.Name) == "version.txt");
            if (versionFile is not null)
            {
                runtime.Version = Encoding.UTF8.GetString(GodotPackager.Read(versionFile, 4096)).Trim();
                if (runtime.Version.Contains("mono", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Mono/.NET export templates need their own target build.");
            }
            runtime.ExtraFiles.AddRange(items.Where(i => !i.IsDirectory && IsNotice(i.Name)));
            if (File.Exists(path))
            {
                string parent = Path.GetDirectoryName(path)!;
                string versionPrefix = Regex.Match(runtime.Version ?? Path.GetFileName(path), @"[0-9]+\.[0-9]+(?:\.[0-9]+)?", RegexOptions.CultureInvariant).Value;
                foreach (string suffix in new[] { "LICENSE.txt", "COPYRIGHT.txt" })
                {
                    string[] candidates = [Path.GetFileNameWithoutExtension(path) + "." + suffix, "godot-" + versionPrefix + "-" + suffix, "godot-" + suffix];
                    string? leaf = candidates.FirstOrDefault(candidate => File.Exists(Path.Combine(parent, candidate)));
                    if (leaf is null) continue;
                    string noticePath = Path.Combine(parent, leaf);
                    SafeData.RejectReparseAncestors(noticePath);
                    runtime.ExtraFiles.Add(new DataItem(leaf, new FileInfo(noticePath).Length, 0, false, () => File.OpenRead(noticePath)));
                }
            }
            var noticeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var notice in runtime.ExtraFiles)
                if (!noticeNames.Add(Path.GetFileName(notice.Name).Normalize(NormalizationForm.FormC))) throw new InvalidDataException("Ambiguous duplicate runtime license notices.");
            if (target == TargetPlatform.Windows)
            {
                var named = items.Where(i => Path.GetFileName(i.Name).Equals("windows_release_x86_64.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (named.Length == 1) runtime.Executable = named[0];
                else
                {
                    var executables = items.Where(i => !i.IsDirectory && i.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !i.Name.EndsWith(".console.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (executables.Length == 1) runtime.Executable = executables[0];
                }
                if (runtime.Executable is null || runtime.Executable.IsSymlink) throw new InvalidDataException("Need Windows release x86_64 export template or one explicitly supplied standalone runtime executable.");
            }
            else
            {
                var macZip = items.Where(i => !i.IsDirectory && Path.GetFileName(i.Name).Equals("macos.zip", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (macZip.Length == 1) runtime.MacData = SafeData.FromBytes(GodotPackager.Read(macZip[0], SafetyLimits.MaxPayloadBytes), true);
                else if (File.Exists(path) && SafeData.IsZip(path)) runtime.MacData = SafeData.Open(path, true);
                else throw new InvalidDataException("macOS runtime requires original macos.zip or a templates ZIP/directory containing it.");
            }
            if (runtime.Hash.Length == 0)
            {
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                if (runtime.Executable is not null) { using var source = runtime.Executable.Open(); digest.AppendData(SHA256.HashData(source)); }
                else if (runtime.MacData is not null) digest.AppendData(Encoding.ASCII.GetBytes(runtime.MacData.Hash()));
                foreach (var item in runtime.ExtraFiles.OrderBy(i => i.Name, StringComparer.Ordinal)) { digest.AppendData(Encoding.UTF8.GetBytes(item.Name + "\0")); using var source = item.Open(); digest.AppendData(SHA256.HashData(source)); }
                runtime.Hash = Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            }
            return runtime;
        }
        catch { runtime.Dispose(); throw; }
    }
    private static bool IsNotice(string name) => Path.GetFileName(name).EndsWith("COPYRIGHT.txt", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(name).EndsWith("LICENSE.txt", StringComparison.OrdinalIgnoreCase);
    public void Dispose() { MacData?.Dispose(); _outer?.Dispose(); _stream?.Dispose(); }
}

internal sealed record GodotPckFile(string Name, int Offset, int Length);

internal sealed class GodotPck
{
    private const uint Magic = 0x43504447;
    public byte[] Bytes { get; private init; } = [];
    public uint Format { get; private init; }
    public string EngineVersion { get; private init; } = "";
    public string SourceKind { get; private set; } = "external-pck";
    public List<string> Features { get; private set; } = [];
    public List<string> Warnings { get; } = [];

    public static GodotPck ReadInput(string path)
    {
        SafeData.RejectReparseAncestors(path);
        if (File.Exists(path) && !SafeData.IsZip(path))
        {
            var bytes = SafeData.ReadFile(path, SafetyLimits.MaxArchiveBytes);
            var location = Locate(bytes) ?? throw new InvalidDataException("No validated external or embedded Godot PCK was found.");
            return Parse(bytes, location.Start, location.Length, location.Kind);
        }
        using var input = SafeData.Open(path, true);
        var candidates = input.Items.Where(i => !i.IsDirectory && i.Name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length > 1) throw new InvalidDataException("Multiple PCKs require an explicit main pack; patch ordering is not guessed.");
        if (candidates.Length == 1)
        {
            if (candidates[0].IsSymlink) throw new InvalidDataException("PCK symlink refused.");
            foreach (var item in input.Items.Where(i => !i.IsDirectory && !ReferenceEquals(i, candidates[0]) && !i.Name.Contains("Contents/MacOS/", StringComparison.Ordinal)))
                if (IsNativeName(item.Name) && !IsStandardMacLibrary(item.Name)) throw new InvalidDataException("Native sidecar needs a target-specific port: " + item.Name);
            var bytes = GodotPackager.Read(candidates[0], SafetyLimits.MaxPayloadBytes);
            return Parse(bytes, 0, bytes.Length, "external-pck-in-container");
        }
        var embedded = new List<GodotPck>();
        foreach (var item in input.Items.Where(i => !i.IsDirectory && (i.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || i.Name.Contains("Contents/MacOS/", StringComparison.Ordinal))))
        {
            if (item.IsSymlink) continue;
            var bytes = GodotPackager.Read(item, SafetyLimits.MaxArchiveBytes);
            var location = Locate(bytes);
            if (location.HasValue) embedded.Add(Parse(bytes, location.Value.Start, location.Value.Length, location.Value.Kind));
        }
        if (embedded.Count != 1) throw new InvalidDataException("Need exactly one external/embedded main PCK.");
        return embedded[0];
    }

    public static (int Start, int Length, string Kind)? Locate(byte[] bytes)
    {
        if (bytes.Length >= 4 && U32(bytes, 0) == Magic) return (0, bytes.Length, "external-pck");
        using var binary = new MemoryStream(bytes, false);
        string type = BinaryKind.Read(binary);
        if (!type.StartsWith("Windows PE", StringComparison.Ordinal) && !type.StartsWith("macOS Mach-O", StringComparison.Ordinal)) return null;
        if (bytes.Length >= 12 && U32(bytes, bytes.Length - 4) == Magic)
        {
            ulong length = U64(bytes, bytes.Length - 12);
            if (length <= (ulong)(bytes.Length - 12))
            {
                int start = bytes.Length - 12 - (int)length;
                if (start >= 64 && U32(bytes, start) == Magic) return (start, (int)length, "embedded-pck-footer");
            }
            throw new InvalidDataException("Invalid embedded PCK footer bounds.");
        }
        if (type.StartsWith("Windows PE", StringComparison.Ordinal))
        {
            int pe = checked((int)U32(bytes, 60));
            Need(bytes, pe, 24);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 6));
            int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pe + 20));
            if (count > 256) throw new InvalidDataException("Excessive PE section count.");
            int sections = checked(pe + 24 + optionalSize); Need(bytes, sections, count * 40);
            (int Start, int Length, string Kind)? result = null;
            for (int i = 0; i < count; i++)
            {
                int section = sections + i * 40;
                string name = Encoding.ASCII.GetString(bytes, section, 8).TrimEnd('\0');
                if (name != "pck") continue;
                uint offset = U32(bytes, section + 20), size = U32(bytes, section + 16);
                if (offset == 0 && size == 0) continue; // Pristine template placeholder.
                if (offset > int.MaxValue || size > int.MaxValue) throw new InvalidDataException("PCK PE section is too large.");
                Need(bytes, (int)offset, (int)size);
                for (int alignment = 0; alignment < 8 && alignment + 4 <= size; alignment++)
                {
                    int start = (int)offset + alignment;
                    if (U32(bytes, start) != Magic) continue;
                    if (result.HasValue) throw new InvalidDataException("Ambiguous PE PCK sections.");
                    int length = (int)size - alignment;
                    if (length >= 12 && U32(bytes, start + length - 4) == Magic)
                    {
                        ulong footerSize = U64(bytes, start + length - 12);
                        if (footerSize == (ulong)(length - 12)) length -= 12;
                    }
                    result = (start, length, "embedded-pck-pe-section");
                }
            }
            return result;
        }
        return null;
    }

    private static GodotPck Parse(byte[] container, int start, int length, string kind)
    {
        if (length > SafetyLimits.MaxPayloadBytes) throw new InvalidDataException("Godot PCK exceeds prototype payload budget.");
        Need(container, start, length);
        var bytes = container.AsSpan(start, length).ToArray();
        Need(bytes, 0, 96);
        if (U32(bytes, 0) != Magic) throw new InvalidDataException("Missing Godot PCK magic.");
        uint format = U32(bytes, 4), major = U32(bytes, 8), minor = U32(bytes, 12), patch = U32(bytes, 16), flags = U32(bytes, 20);
        if (format is not 2 and not 3 and not 4 || major != 4 || minor > 100 || patch > 1000) throw new InvalidDataException("Only bounded Godot 4 PCK V2/V3/V4 packs are supported; engine upgrades are not performed.");
        if ((flags & 1) != 0) throw new InvalidDataException("Encrypted PCK directory requires its original custom runtime/key; automatic porting refused.");
        if ((flags & ~2u) != 0) throw new InvalidDataException("Sparse/encrypted/unknown PCK flags require a dedicated adapter.");
        ulong fileBase = U64(bytes, 24);
        if (format == 2 && (flags & 2) == 0 && start > 0)
        {
            if (fileBase < (ulong)start) throw new InvalidDataException("Embedded PCK absolute file base is invalid.");
            fileBase -= (ulong)start; BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), fileBase);
        }
        ulong directory = format >= 3 ? U64(bytes, 32) : 96;
        if (directory > int.MaxValue || fileBase > int.MaxValue) throw new InvalidDataException("PCK offset exceeds bounded parser.");
        int cursor = (int)directory; Need(bytes, cursor, 4);
        uint count = U32(bytes, cursor); cursor += 4;
        if (count == 0 || count > SafetyLimits.MaxEntries) throw new InvalidDataException("PCK entry count outside budget.");
        var files = new List<GodotPckFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        for (int i = 0; i < count; i++)
        {
            Need(bytes, cursor, 4); uint pathLength = U32(bytes, cursor); cursor += 4;
            if (pathLength == 0 || pathLength > 4096) throw new InvalidDataException("Invalid PCK resource path length.");
            Need(bytes, cursor, checked((int)pathLength + 36));
            string name = new UTF8Encoding(false, true).GetString(bytes, cursor, (int)pathLength).TrimEnd('\0'); cursor += (int)pathLength;
            if (name.StartsWith("res://", StringComparison.Ordinal)) name = name[6..];
            SafeData.ValidateName(name);
            if (!names.Add(name.Normalize(NormalizationForm.FormC))) throw new InvalidDataException("Duplicate/case-colliding PCK resource: " + name);
            ulong offset = U64(bytes, cursor), size = U64(bytes, cursor + 8); var md5 = bytes.AsSpan(cursor + 16, 16); uint fileFlags = U32(bytes, cursor + 32); cursor += 36;
            if (fileFlags != 0) throw new InvalidDataException("Encrypted/removal/unknown PCK resource flags require a dedicated adapter: " + name);
            if (offset > int.MaxValue || size > int.MaxValue || fileBase > (ulong)int.MaxValue - offset) throw new InvalidDataException("PCK resource bounds overflow.");
            int absolute = (int)(fileBase + offset); Need(bytes, absolute, (int)size);
            if ((long)size > SafetyLimits.MaxUncompressedBytes - total) throw new InvalidDataException("PCK resource budget exceeded."); total += (long)size;
            if (!MD5.HashData(bytes.AsSpan(absolute, (int)size)).AsSpan().SequenceEqual(md5)) throw new InvalidDataException("PCK resource checksum mismatch: " + name);
            if (IsNativeName(name) || name.Contains("/.godot/mono/", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".godot/mono/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Godot native/.NET dependency needs a dedicated target build: " + name);
            if (name.EndsWith("extension_list.cfg", StringComparison.OrdinalIgnoreCase) && Encoding.UTF8.GetString(bytes, absolute, (int)size).Trim().Length > 0)
                throw new InvalidDataException("GDExtension list is nonempty; native dependencies require a dedicated port.");
            files.Add(new GodotPckFile(name, absolute, (int)size));
        }
        int headerEnd = format >= 3 ? 104 : 96;
        if ((int)directory < headerEnd) throw new InvalidDataException("PCK directory overlaps header.");
        foreach (var file in files)
            if (file.Length > 0 && (file.Offset < headerEnd || file.Offset < cursor && file.Offset + file.Length > (int)directory)) throw new InvalidDataException("PCK resource overlaps header/directory.");
        var ranges = files.Where(f => f.Length > 0).OrderBy(f => f.Offset).ToArray();
        for (int i = 1; i < ranges.Length; i++) if ((long)ranges[i - 1].Offset + ranges[i - 1].Length > ranges[i].Offset) throw new InvalidDataException("PCK resource ranges overlap.");
        var project = files.SingleOrDefault(f => f.Name == "project.binary") ?? throw new InvalidDataException("PCK is not a complete exported project: missing root project.binary.");
        var features = ProjectFeatures(bytes.AsSpan(project.Offset, project.Length));
        if (features.Count == 0) throw new InvalidDataException("Cannot establish required project features from project.binary.");
        if (features.Any(f => f.Equals("C#", StringComparison.OrdinalIgnoreCase) || f.Equals("Double Precision", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Godot C# / double-precision projects require their matching custom runtimes and are refused.");
        bool hasCompatibleVersionFeature = false;
        foreach (string feature in features)
        {
            var versionFeature = Regex.Match(feature, @"^(?<major>[0-9]+)\.(?<minor>[0-9]+)(?:\.(?<patch>[0-9]+))?$", RegexOptions.CultureInvariant);
            if (versionFeature.Success)
            {
                if (uint.Parse(versionFeature.Groups["major"].Value) != major || uint.Parse(versionFeature.Groups["minor"].Value) > minor)
                    throw new InvalidDataException("Project feature requires a newer/different Godot engine: " + feature);
                hasCompatibleVersionFeature = true;
            }
            else if (feature is not "Forward Plus" and not "Mobile" and not "GL Compatibility")
                throw new InvalidDataException("Unsupported/custom required engine feature: " + feature);
        }
        if (!hasCompatibleVersionFeature) throw new InvalidDataException("Project lacks an inspectable compatible Godot version feature.");
        var pack = new GodotPck { Bytes = bytes, Format = format, EngineVersion = major + "." + minor + "." + patch, SourceKind = kind, Features = features };
        if (format == 2 && (flags & 2) == 0 && start > 0) pack.Warnings.Add("An absolute embedded V2 file-base offset was rebased for a standalone PCK; resource bytes and checksums were preserved.");
        return pack;
    }

    private static List<string> ProjectFeatures(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SafetyLimits.MaxScriptBytes || bytes.Length < 8 || Encoding.ASCII.GetString(bytes[..4]) != "ECFG") throw new InvalidDataException("Invalid/budget-exceeding binary project settings.");
        int cursor = 8; uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (count > 100000) throw new InvalidDataException("Excessive project settings count.");
        List<string>? features = null; var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string key = ReadString(bytes, ref cursor, padded: false, max: 4096);
            if (!keys.Add(key)) throw new InvalidDataException("Duplicate project setting key.");
            Need(bytes, cursor, 4); uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]); cursor += 4;
            if (length > SafetyLimits.MaxScriptBytes) throw new InvalidDataException("Project setting exceeds budget."); Need(bytes, cursor, (int)length);
            var value = bytes.Slice(cursor, (int)length); cursor += (int)length;
            if (key.StartsWith("dotnet/", StringComparison.OrdinalIgnoreCase) || key.StartsWith("mono/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed project settings require a .NET target build.");
            if (key == "application/config/features")
            {
                Need(value, 0, 8); uint type = BinaryPrimitives.ReadUInt32LittleEndian(value) & 0xffffu;
                if (type != 34) throw new InvalidDataException("Unsupported project-features Variant format.");
                uint featureCount = BinaryPrimitives.ReadUInt32LittleEndian(value[4..]); if (featureCount > 1000) throw new InvalidDataException("Too many project features.");
                int featureCursor = 8; features = [];
                for (int j = 0; j < featureCount; j++) features.Add(ReadString(value, ref featureCursor, true, 4096));
                if (featureCursor != value.Length) throw new InvalidDataException("Unexpected feature Variant trailing data.");
            }
        }
        if (cursor != bytes.Length) throw new InvalidDataException("Unexpected project.binary trailing data.");
        return features ?? [];
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int cursor, bool padded, int max)
    {
        Need(bytes, cursor, 4); uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[cursor..]); cursor += 4;
        if (size > max) throw new InvalidDataException("Godot string exceeds parser budget."); Need(bytes, cursor, (int)size);
        string result = new UTF8Encoding(false, true).GetString(bytes.Slice(cursor, (int)size)).TrimEnd('\0'); cursor += (int)size;
        if (result.Contains('\0')) throw new InvalidDataException("Interior NUL in a Godot string is ambiguous.");
        if (padded) { int padding = (4 - (int)size % 4) % 4; Need(bytes, cursor, padding); cursor += padding; }
        return result;
    }
    private static bool IsNativeName(string name) { string ext = Path.GetExtension(name).ToLowerInvariant(); return ext is ".dll" or ".dylib" or ".so" or ".exe" or ".node" or ".gdextension" or ".gdnlib" or ".gdns" or ".cs" or ".csproj" || Regex.IsMatch(name, @"\.so(?:\.[0-9]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
    private static bool IsStandardMacLibrary(string name) => name.EndsWith("Contents/Frameworks/libEGL.dylib", StringComparison.Ordinal) || name.EndsWith("Contents/Frameworks/libGLESv2.dylib", StringComparison.Ordinal);
    private static uint U32(byte[] bytes, int offset) { Need(bytes, offset, 4); return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)); }
    private static ulong U64(byte[] bytes, int offset) { Need(bytes, offset, 8); return BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset)); }
    private static void Need(ReadOnlySpan<byte> bytes, int offset, int count) { if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new InvalidDataException("Godot binary is truncated or contains out-of-bounds offsets."); }
}
