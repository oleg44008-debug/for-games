using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DustoreX.AutoConverter;

public sealed record EnginePayloadInspection(string? RequiredVersion, string PayloadRoot, IReadOnlyList<string> Warnings);

/// <summary>Transfers a JavaScript application into a caller-supplied target NW.js runtime.</summary>
public static class NwPackager
{
    public static EnginePayloadInspection Inspect(string path)
    {
        byte[] payload = ReadPayload(path);
        using var data = SafeData.FromBytes(payload, false);
        var manifest = RuntimePackageTools.ExactlyOne(data.Items.Where(i => i.Name == "package.json" && !i.IsDirectory), "NW.js payload needs root package.json.");
        using var json = JsonDocument.Parse(RuntimePackageTools.ReadBytes(manifest, SafetyLimits.MaxScriptBytes));
        ValidateManifest(data, json.RootElement);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        foreach (string key in new[] { "nwjsVersion", "nwjs_version", "nwjs-version", "nwVersion" })
            if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                if (RuntimePackageTools.ExactVersion(value.GetString()) is { } declared) versions.Add(declared);
        if (json.RootElement.TryGetProperty("engines", out var engines) && engines.ValueKind == JsonValueKind.Object)
            foreach (string key in new[] { "nw", "nwjs" })
                if (engines.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    if (RuntimePackageTools.ExactVersion(value.GetString()) is { } declared) versions.Add(declared);
        if (versions.Count > 1) throw new InvalidDataException("Conflicting exact NW.js runtime-version metadata; automatic selection is unsafe.");
        string? version = versions.SingleOrDefault();
        RuntimePackageTools.CheckPortableFiles(data.Items, "NW.js", inspectJavaScript: true);
        return new EnginePayloadInspection(version, "application payload with root package.json", [version is null
            ? "No exact source NW.js runtime version was established; app package.json version is an application version and is not used as runtime version."
            : "Required NW.js version was declared by source metadata; authenticity and behavioral compatibility remain unverified."]);
    }

    public static PackageResult Package(PackageRequest request)
    {
        RuntimePackageTools.ValidateRequest(request);
        byte[] payload = ReadPayload(request.InputPath);
        using var data = SafeData.FromBytes(payload, false);
        var manifest = data.Items.SingleOrDefault(i => i.Name == "package.json" && !i.IsDirectory)
            ?? throw new InvalidDataException("NW.js payload needs package.json at its root.");
        using var config = JsonDocument.Parse(RuntimePackageTools.ReadBytes(manifest, SafetyLimits.MaxScriptBytes));
        ValidateManifest(data, config.RootElement);
        RuntimePackageTools.CheckPortableFiles(data.Items, "NW.js", inspectJavaScript: true);
        string? requiredVersion = Inspect(request.InputPath).RequiredVersion;
        RuntimePackageTools.CheckVersion(requiredVersion, request.RuntimeVersion, "NW.js");
        using var runtime = SafeData.Open(request.RuntimePath, request.Target == TargetPlatform.MacOS);
        var warnings = RuntimePackageTools.Warnings("NW.js", request.RuntimeVersion);
        warnings.Add("JavaScript and assets were transferred; existing saves and source-runtime files outside the selected application payload were not migrated.");
        warnings.Add("Dependencies must already be present. The adapter does not execute npm install, rebuild native addons, or evaluate JavaScript.");
        return RuntimePackageTools.WritePackage(request, runtime, payload, "NW.js", warnings, zip =>
        {
            if (request.Target == TargetPlatform.Windows) Windows(runtime, payload, request.GameName, zip);
            else Mac(runtime, payload, request.GameName, zip, warnings);
        });
    }

    private static void ValidateManifest(SafeData data, JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("main", out var main)
            || main.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(main.GetString()))
            throw new InvalidDataException("NW.js package.json must declare a main entry point.");
        string entryPoint = main.GetString()!;
        if (Uri.TryCreate(entryPoint, UriKind.Absolute, out _)) throw new InvalidDataException("Remote NW.js entry points are not packaged by this adapter.");
        SafeData.ValidateName(entryPoint);
        if (!data.Items.Any(i => !i.IsDirectory && i.Name == entryPoint)) throw new InvalidDataException("NW.js main entry point is absent or has different letter case: " + entryPoint);
    }

    private static byte[] ReadPayload(string path)
    {
        if (File.Exists(path) && !SafeData.IsZip(path)) return RuntimePackageTools.ReadAppendedZip(path);
        using var input = SafeData.Open(path, true);
        var packed = input.Items.Where(i => !i.IsDirectory && (i.Name.EndsWith("/app.nw", StringComparison.OrdinalIgnoreCase)
            || i.Name.EndsWith("/package.nw", StringComparison.OrdinalIgnoreCase) || i.Name is "app.nw" or "package.nw")).ToArray();
        if (packed.Length > 1) throw new InvalidDataException("Multiple NW.js application archives are ambiguous.");
        if (packed.Length == 1)
        {
            if (packed[0].IsSymlink) throw new InvalidDataException("An NW.js payload cannot be a symlink.");
            return RuntimePackageTools.ReadBytes(packed[0], SafetyLimits.MaxPayloadBytes);
        }
        var roots = input.Items.Where(i => !i.IsDirectory && (i.Name == "package.json"
            || i.Name.EndsWith("/www/package.json", StringComparison.OrdinalIgnoreCase)
            || i.Name == "www/package.json"
            || i.Name.EndsWith("/app.nw/package.json", StringComparison.OrdinalIgnoreCase)
            || i.Name.EndsWith("/package.nw/package.json", StringComparison.OrdinalIgnoreCase)))
            .Select(i => SafeData.ParentName(i.Name)).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("Select exactly one NW.js www/app.nw/package.nw directory or application ZIP with root package.json.");
        string root = roots[0];
        if (input.IsArchive && root.Length == 0) return SafeData.ReadFile(path, SafetyLimits.MaxPayloadBytes);
        return RuntimePackageTools.CreatePayload(input.Items.Where(i => i.Name.StartsWith(root, StringComparison.Ordinal))
            .Select(i => RuntimePackageTools.Rename(i, i.Name[root.Length..])).Where(i => i.Name.Length != 0));
    }

    private static void Windows(SafeData runtime, byte[] payload, string name, ZipArchive output)
    {
        var exe = RuntimePackageTools.ExactlyOne(runtime.Items.Where(i => !i.IsDirectory && Path.GetFileName(i.Name).Equals("nw.exe", StringComparison.OrdinalIgnoreCase)), "NW.js runtime needs exactly one nw.exe.");
        RuntimePackageTools.RequireBinary(exe, "Windows PE");
        string root = SafeData.ParentName(exe.Name);
        bool Has(string file) => runtime.Items.Any(i => i.Name.Equals(root + file, StringComparison.OrdinalIgnoreCase) && !i.IsDirectory);
        if (!Has("nw.dll") || !Has("icudtl.dat")) throw new InvalidDataException("Supply the complete official Windows NW.js runtime, including nw.dll and icudtl.dat.");
        if (runtime.Items.Any(i => i.IsSymlink || i.Name.StartsWith(root + "package.nw", StringComparison.OrdinalIgnoreCase) || i.Name == root + "package.json"))
            throw new InvalidDataException("NW.js runtime must be pristine and contain no application payload or symlinks.");
        foreach (var item in runtime.Items.Where(i => i.Name.StartsWith(root, StringComparison.Ordinal)))
        {
            string relative = item.Name[root.Length..];
            if (relative.Length == 0) continue;
            string destination = ReferenceEquals(item, exe) ? name + "/" + name + ".exe" : name + "/" + relative;
            if (!ReferenceEquals(item, exe) && (relative.Equals(name + ".exe", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(name + ".exe/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Generated NW.js executable collides with a runtime path.");
            RuntimePackageTools.Copy(output, destination, item);
        }
        RuntimePackageTools.Write(output, name + "/package.nw", payload);
    }

    private static void Mac(SafeData runtime, byte[] payload, string name, ZipArchive output, List<string> warnings)
    {
        if (!runtime.IsArchive) throw new InvalidDataException("macOS NW.js runtime must be the original ZIP preserving permissions and symlinks.");
        string root = RuntimePackageTools.FindMacRoot(runtime);
        var plist = RuntimePackageTools.ExactlyOne(runtime.Items.Where(i => i.Name == root + "Contents/Info.plist"), "Missing main NW.js Info.plist.");
        var document = RuntimePackageTools.ReadPlist(plist);
        string executable = RuntimePackageTools.GetPlist(document, "CFBundleExecutable") ?? throw new InvalidDataException("Missing CFBundleExecutable.");
        SafeData.ValidateGameName(executable);
        var main = RuntimePackageTools.ExactlyOne(runtime.Items.Where(i => i.Name == root + "Contents/MacOS/" + executable), "Missing main NW.js runtime executable.");
        RuntimePackageTools.RequireBinary(main, "macOS Mach-O", requireExecuteMode: true);
        if (runtime.Items.Any(i => i.Name.StartsWith(root + "Contents/Resources/app.nw", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("NW.js runtime already contains app.nw; provide a pristine runtime.");
        string outputRoot = name + ".app/";
        foreach (var item in runtime.Items.Where(i => i.Name.StartsWith(root, StringComparison.Ordinal)))
        {
            string relative = item.Name[root.Length..];
            if (relative.Length == 0 || RuntimePackageTools.IsSignature(relative)) continue;
            var renamed = RuntimePackageTools.Rename(item, outputRoot + relative);
            if (item.IsSymlink) { SafeData.ValidateSymlink(item, root); SafeData.ValidateSymlink(renamed, outputRoot); }
            if (ReferenceEquals(item, plist))
            {
                RuntimePackageTools.BrandPlist(document, name, "nwjs");
                RuntimePackageTools.Write(output, renamed.Name, RuntimePackageTools.PlistBytes(document), item.ExternalAttributes);
            }
            else RuntimePackageTools.Copy(output, renamed.Name, item);
        }
        RuntimePackageTools.Write(output, outputRoot + "Contents/Resources/app.nw", payload);
        warnings.Add("macOS bundle metadata was modified and stale signature resources removed. Final signing/notarization and execution must be validated on macOS; helper-app names and runtime icons remain those of NW.js.");
    }
}

internal static class RuntimePackageTools
{
    internal const int RegularMode = unchecked((int)0x81A40000);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ZipArchive, OutputPaths> Paths = new();

    internal static void ValidateRequest(PackageRequest request)
    {
        SafeData.ValidateGameName(request.GameName);
        if (!Enum.IsDefined(request.Target)) throw new ArgumentException("Invalid target platform.");
        string output = Path.GetFullPath(request.OutputPath);
        if (!output.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be a new ZIP file.");
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Output already exists; it will not be overwritten.");
        SafeData.RequireSeparateOutput(output, Path.GetFullPath(request.InputPath));
        SafeData.RequireSeparateOutput(output, Path.GetFullPath(request.RuntimePath));
        SafeData.RejectReparseAncestors(output);
    }

    internal static List<string> Warnings(string engine, string? version) =>
    [
        "Packaged only: no supplied game/runtime was executed and target-OS compatibility is unverified.",
        "Runtime SHA-256 identifies caller-supplied bytes; authenticity and completeness are not proved by binary headers.",
        version is null ? "Runtime version is unknown; compatibility is not assumed." : "Runtime version is the caller's assertion: " + version + ".",
        "Static inspection cannot prove absence of dynamically constructed platform-specific calls in " + engine + " code."
    ];
    internal static string? ExactVersion(string? value) => value is not null && Regex.IsMatch(value, @"^v?[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant)
        ? value.TrimStart('v') : null;
    internal static void CheckVersion(string? required, string? supplied, string engine)
    {
        if (required is not null && supplied is not null && required != supplied.TrimStart('v'))
            throw new InvalidDataException(engine + " source requires " + required + ", supplied runtime-version assertion is " + supplied + ". Automatic upgrading is not supported.");
    }

    internal static PackageResult WritePackage(PackageRequest request, SafeData runtime, byte[] payload, string engine, List<string> warnings, Action<ZipArchive> build)
    {
        string output = Path.GetFullPath(request.OutputPath);
        string sourceHash = SafeData.HashPath(request.InputPath), runtimeHash = runtime.Hash(), payloadHash = SafeData.Hash(payload);
        string parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".dustorex-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                build(zip);
                Write(zip, "package-manifest.json", JsonSerializer.SerializeToUtf8Bytes(new
                {
                    schemaVersion = 1, engine, status = "Packaged", testedOnTarget = false,
                    target = request.Target.ToString(), gameName = request.GameName, runtimeVersionAssumption = request.RuntimeVersion,
                    sourceSha256 = sourceHash, runtimeSha256 = runtimeHash, payloadSha256 = payloadHash,
                    payloadHashMethod = "portable-payload-zip-sha256", createdUtc = DateTimeOffset.UtcNow, warnings
                }, Json));
            }
            if (request.Target == TargetPlatform.MacOS) MacZipMetadata.MarkUnixCreator(staging);
            File.Move(staging, output, false);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
        return new PackageResult(output, "Packaged", sourceHash, payloadHash, runtimeHash, warnings);
    }

    internal static DataItem Rename(DataItem item, string name) => new(name, item.Length, item.ExternalAttributes, item.IsDirectory, item.Open);
    internal static T ExactlyOne<T>(IEnumerable<T> items, string error)
    {
        T[] values = items.Take(2).ToArray();
        return values.Length == 1 ? values[0] : throw new InvalidDataException(error);
    }
    internal static byte[] ReadBytes(DataItem item, long limit)
    {
        if (item.IsDirectory || item.Length > limit) throw new InvalidDataException("Entry exceeds budget or is a directory: " + item.Name);
        using var input = item.Open(); using var output = new MemoryStream();
        SafeData.CopyBounded(input, output, limit);
        return output.ToArray();
    }
    internal static string ReadText(DataItem item) => new UTF8Encoding(false, true).GetString(ReadBytes(item, SafetyLimits.MaxScriptBytes));
    internal static void Copy(ZipArchive output, string name, DataItem item)
    {
        ValidateOutputPath(output, name, item.IsDirectory);
        var entry = output.CreateEntry(name, CompressionLevel.Optimal); entry.ExternalAttributes = item.ExternalAttributes;
        if (item.IsDirectory) return;
        using var input = item.Open(); using var target = entry.Open(); SafeData.CopyBounded(input, target, SafetyLimits.MaxUncompressedBytes);
    }
    internal static void Write(ZipArchive output, string name, byte[] bytes, int attributes = RegularMode)
    {
        ValidateOutputPath(output, name, false);
        var entry = output.CreateEntry(name, CompressionLevel.Optimal); entry.ExternalAttributes = attributes;
        using var target = entry.Open(); target.Write(bytes);
    }
    internal static void CreateDirectoryEntry(ZipArchive output, string name)
    {
        ValidateOutputPath(output, name, true);
        var entry = output.CreateEntry(name); entry.ExternalAttributes = unchecked((int)0x41ED0000);
    }
    private static void ValidateOutputPath(ZipArchive output, string name, bool directory)
    {
        SafeData.ValidateName(name);
        string normalized = name.TrimEnd('/').Normalize(NormalizationForm.FormC);
        var paths = Paths.GetOrCreateValue(output);
        if (!paths.Explicit.Add(normalized) || paths.Files.Contains(normalized) || (!directory && paths.Directories.Contains(normalized)))
            throw new InvalidDataException("Generated package paths collide: " + name);
        int slash = normalized.IndexOf('/');
        while (slash >= 0)
        {
            string parent = normalized[..slash];
            if (paths.Files.Contains(parent)) throw new InvalidDataException("Generated file/directory paths collide: " + name);
            paths.Directories.Add(parent); slash = normalized.IndexOf('/', slash + 1);
        }
        if (directory) paths.Directories.Add(normalized); else paths.Files.Add(normalized);
    }
    private sealed class OutputPaths
    {
        internal HashSet<string> Explicit { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    internal static byte[] CreatePayload(IEnumerable<DataItem> source)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            foreach (var item in source.OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                if (item.IsSymlink) throw new InvalidDataException("Portable payload symlinks are unsupported: " + item.Name);
                Copy(zip, item.Name, item);
            }
        if (buffer.Length > SafetyLimits.MaxPayloadBytes) throw new InvalidDataException("Portable payload exceeds ZIP byte budget.");
        return buffer.ToArray();
    }
    internal static bool NativeName(string name) => Regex.IsMatch(name, @"\.(?:dll|dylib|so(?:\.[0-9]+)*|exe|node|nexe|pexe|jsc|bundle|a|lib)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    internal static void CheckPortableFiles(IEnumerable<DataItem> files, string engine, bool inspectJavaScript = false, bool inspectPython = false)
    {
        foreach (var item in files.Where(i => !i.IsDirectory))
        {
            if (item.IsSymlink || NativeName(item.Name)) throw new InvalidDataException(engine + " native dependency needs a dedicated port: " + item.Name);
            using (var input = item.Open()) if (BinaryKind.Read(input) != "Unknown binary") throw new InvalidDataException("Native binary in portable payload: " + item.Name);
            string extension = Path.GetExtension(item.Name).ToLowerInvariant();
            if ((inspectJavaScript && extension is ".js" or ".mjs" or ".cjs" or ".html") || (inspectPython && extension is ".py" or ".rpy"))
            {
                string text = ReadText(item);
                string pattern = inspectJavaScript ? @"\bevalNWBin(?:Module)?\b|\bprocess\s*\.\s*dlopen\b|\bffi[-_]napi\b|\bnode[-_]ffi\b"
                    : @"\bctypes\b|\bcffi\b|\bsubprocess\b|\bos\s*\.\s*(?:system|popen|startfile)\b|\bwin32(?:api|com|gui|process)\b";
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new InvalidDataException(engine + " native/precompiled/external-process dependency requires manual porting: " + item.Name);
            }
        }
    }
    internal static void RequireBinary(DataItem item, string kind, bool requireExecuteMode = false)
    {
        if (item.IsDirectory || item.IsSymlink || (requireExecuteMode && ((item.ExternalAttributes >> 16) & 0x49) == 0))
            throw new InvalidDataException("Runtime executable must be a regular file with required execute metadata: " + item.Name);
        using var input = item.Open();
        if (!BinaryKind.Read(input).StartsWith(kind, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected runtime binary format: " + item.Name);
    }
    internal static string FindMacRoot(SafeData data)
    {
        var roots = data.Items.Select(i =>
        {
            var parts = i.Name.Split('/');
            int index = Array.FindIndex(parts, p => p.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
            return index < 0 ? null : string.Join('/', parts[..(index + 1)]) + "/";
        }).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToArray();
        return roots.Length == 1 ? roots[0]! : throw new InvalidDataException("Runtime ZIP must contain one top-level application bundle.");
    }
    internal static bool IsSignature(string relative) => relative.Contains("/_CodeSignature/", StringComparison.Ordinal) || relative.EndsWith("/CodeResources", StringComparison.Ordinal);
    internal static XDocument ReadPlist(DataItem item)
    {
        using var input = item.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = SafetyLimits.MaxScriptBytes });
        return XDocument.Load(reader);
    }
    internal static string? GetPlist(XDocument doc, string key) => doc.Root?.Element("dict")?.Elements("key").FirstOrDefault(e => e.Value == key)?.ElementsAfterSelf().FirstOrDefault()?.Value;
    internal static void SetPlist(XDocument doc, string key, string value)
    {
        var dict = doc.Root?.Element("dict") ?? throw new InvalidDataException("Invalid Info.plist dictionary.");
        foreach (var node in dict.Elements("key").Where(k => k.Value == key).ToArray()) { node.ElementsAfterSelf().FirstOrDefault()?.Remove(); node.Remove(); }
        dict.Add(new XElement("key", key), new XElement("string", value));
    }
    internal static void BrandPlist(XDocument document, string name, string engine)
    {
        SetPlist(document, "CFBundleName", name); SetPlist(document, "CFBundleDisplayName", name);
        SetPlist(document, "CFBundleIdentifier", "org.dustorex." + engine + "." + SafeData.Hash(Encoding.UTF8.GetBytes(name))[..16]);
        var dict = document.Root!.Element("dict")!;
        foreach (var node in dict.Elements("key").Where(k => k.Value is "CFBundleDocumentTypes" or "UTExportedTypeDeclarations").ToArray()) { node.ElementsAfterSelf().FirstOrDefault()?.Remove(); node.Remove(); }
    }
    internal static byte[] PlistBytes(XDocument document)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false })) document.Save(writer);
        return buffer.ToArray();
    }
    internal static byte[] ReadAppendedZip(string path)
    {
        byte[] bytes = SafeData.ReadFile(path, SafetyLimits.MaxArchiveBytes);
        using (var binary = new MemoryStream(bytes, false)) if (!BinaryKind.Read(binary).StartsWith("Windows PE", StringComparison.Ordinal)) throw new InvalidDataException("NW.js executable input must be a PE with an appended application ZIP.");
        for (int offset = bytes.Length - 22; offset >= Math.Max(0, bytes.Length - 65557); offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) != 0x06054b50) continue;
            var end = bytes.AsSpan(offset);
            if (offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(end[20..]) != bytes.Length) continue;
            ushort entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]), location = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            if (entries == 0 || entries == ushort.MaxValue || size == uint.MaxValue || location == uint.MaxValue || BinaryPrimitives.ReadUInt16LittleEndian(end[4..]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(end[6..]) != 0)
                throw new InvalidDataException("Split/ZIP64 NW.js executables need a dedicated adapter.");
            long start = offset - (long)size - location;
            if (start < 64 || start > bytes.Length || bytes.Length - start > SafetyLimits.MaxPayloadBytes) throw new InvalidDataException("Invalid appended NW.js ZIP bounds.");
            byte[] result = bytes.AsSpan((int)start).ToArray();
            using var check = SafeData.FromBytes(result, false);
            if (check.Items.Count != entries || !check.Items.Any(i => i.Name == "package.json")) throw new InvalidDataException("Appended archive is not an NW.js application.");
            return result;
        }
        throw new InvalidDataException("Executable has no validated appended NW.js payload; select its www/package.nw folder instead.");
    }
}
