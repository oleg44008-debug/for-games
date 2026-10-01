using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DustoreX.AutoConverter;

if (args.Length > 0 && args[0] == "--inspect")
{
    Console.WriteLine(JsonSerializer.Serialize(GodotPackager.Inspect(args[1]), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args.Length > 0 && args[0] == "--package")
{
    var request = new PackageRequest(args[1], args[2], args[3], args[4] == "macos" ? TargetPlatform.MacOS : TargetPlatform.Windows, args[5], args[6]);
    Console.WriteLine(JsonSerializer.Serialize(GodotPackager.Package(request), new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}

string root = Path.Combine(Path.GetTempPath(), "DustoreX-GodotChecks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
int passed = 0, failed = 0;
void Check(string name, Action body) { try { body(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); } }
void True(bool valid, string reason) { if (!valid) throw new Exception(reason); }
void Refuse(Action body) { try { body(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return; } throw new Exception("Expected explicit refusal."); }
string FileWith(string name, byte[] bytes) { string path = Path.Combine(root, name); File.WriteAllBytes(path, bytes); return path; }
string Out() => Path.Combine(root, "package-" + Guid.NewGuid().ToString("N") + ".zip");
var pe = FakePe(); string windows = FileWith("windows_release_x86_64.exe", pe); string mac = FileWith("macos.zip", MacRuntime());
PackageResult Package(string source, TargetPlatform target = TargetPlatform.Windows, string? runtime = null, string version = "4.7.1", string name = "Probe") => GodotPackager.Package(new(source, runtime ?? (target == TargetPlatform.Windows ? windows : mac), Out(), target, name, version));

foreach (int format in new[] { 2, 3, 4 })
{
    int localFormat = format;
    Check("inspect external PCK V" + format, () => { var inspection = GodotPackager.Inspect(FileWith("pack" + localFormat + ".pck", Pck(localFormat))); True(inspection.PackFormat == localFormat && inspection.EngineVersion == "4.7.1" && inspection.Features.Contains("4.7"), "Bad inspected requirements."); });
    Check("package Windows PCK V" + format, () => { byte[] bytes = Pck(localFormat); var result = Package(FileWith("win" + localFormat + ".pck", bytes)); using var zip = ZipFile.OpenRead(result.OutputPath); True(ReadEntry(zip, "Probe/Probe.pck").SequenceEqual(bytes), "Pack bytes changed."); True(ReadEntry(zip, "Probe/Probe.exe").SequenceEqual(pe), "Runtime bytes changed."); });
}
Check("Mac template executable and PCK alignment", () =>
{
    var result = Package(FileWith("mac-source.pck", Pck(4)), TargetPlatform.MacOS);
    using var zip = ZipFile.OpenRead(result.OutputPath);
    True(zip.GetEntry("Probe.app/Contents/MacOS/Probe") is not null && zip.GetEntry("Probe.app/Contents/Resources/Probe.pck") is not null, "Mac paths differ.");
    string plist = Encoding.UTF8.GetString(ReadEntry(zip, "Probe.app/Contents/Info.plist")); True(plist.Contains("<string>Probe</string>") && !plist.Contains('$'), "Unresolved template placeholders.");
    string privacy = Encoding.UTF8.GetString(ReadEntry(zip, "Probe.app/Contents/Resources/PrivacyInfo.xcprivacy")); True(!privacy.Contains('$'), "Unresolved privacy placeholders.");
    var binary = zip.GetEntry("Probe.app/Contents/MacOS/Probe")!; True(((binary.ExternalAttributes >> 16) & 0x49) != 0, "Lost executable bit.");
    using var manifest = JsonDocument.Parse(ReadEntry(zip, "package-manifest.json")); True(manifest.RootElement.GetProperty("status").GetString() == "Packaged" && !manifest.RootElement.GetProperty("testedOnTarget").GetBoolean(), "Dishonest execution status.");
    True(CreatorHosts(result.OutputPath).All(h => h == 3), "Lost Unix creator platform.");
});
Check("Mac XML escaped game name", () => Package(FileWith("xml.pck", Pck(4)), TargetPlatform.MacOS, name: "Game & Test"));
Check("Mac ZIP to Windows roundtrip", () => { var original = Pck(4); var first = Package(FileWith("round.pck", original), TargetPlatform.MacOS); var second = Package(first.OutputPath); using var zip = ZipFile.OpenRead(second.OutputPath); True(ReadEntry(zip, "Probe/Probe.pck").SequenceEqual(original), "Roundtrip pack bytes changed."); });
Check("embedded PE footer", () => { var bytes = Pck(4); var fused = WithFooter(pe, bytes); var inspection = GodotPackager.Inspect(FileWith("embedded.exe", fused)); True(inspection.SourceKind == "embedded-pck-footer", "Wrong embedded origin."); });
Check("embedded Mach-O footer", () => { var bytes = Pck(4); var inspection = GodotPackager.Inspect(FileWith("embedded-mac", WithFooter(FakeMach(), bytes))); True(inspection.SourceKind == "embedded-pck-footer", "Wrong MachO origin."); });
Check("embedded PE pck section without footer", () => { var bytes = Pck(4); var prefix = FakePe(); BinaryPrimitives.WriteUInt16LittleEndian(prefix.AsSpan(70), 1); Encoding.ASCII.GetBytes("pck").CopyTo(prefix, 88); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(104), (uint)bytes.Length); BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(108), (uint)prefix.Length); var inspection = GodotPackager.Inspect(FileWith("section.exe", prefix.Concat(bytes).ToArray())); True(inspection.SourceKind == "embedded-pck-pe-section", "Section location failed."); });
Check("embedded absolute V2 filebase rebased", () => { var bytes = Pck(2, flags: 0); ulong filebase = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24)); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), filebase + (uint)pe.Length); var result = Package(FileWith("absolute.exe", WithFooter(pe, bytes))); True(result.Warnings.Any(w => w.Contains("rebased")), "No rebasing evidence."); });
Check("runtime mismatch refused", () => Refuse(() => Package(FileWith("mismatch.pck", Pck(4)), version: "4.5")));
Check("unknown runtime version refused", () => Refuse(() => GodotPackager.Package(new(FileWith("unknown.pck", Pck(4)), windows, Out(), TargetPlatform.Windows, "Probe"))));
Check("encrypted directory refused", () => Refuse(() => GodotPackager.Inspect(FileWith("encrypted.pck", Pck(4, flags: 3)))));
Check("sparse pack refused", () => Refuse(() => GodotPackager.Inspect(FileWith("sparse.pck", Pck(4, flags: 6)))));
Check("unknown format refused", () => { var bytes = Pck(4); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 99); Refuse(() => GodotPackager.Inspect(FileWith("unknown-format.pck", bytes))); });
Check("checksum tampering refused", () => { var bytes = Pck(4); bytes[112] ^= 1; Refuse(() => GodotPackager.Inspect(FileWith("tampered.pck", bytes))); });
Check("directory bounds refused", () => { var bytes = Pck(4); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), ulong.MaxValue); Refuse(() => GodotPackager.Inspect(FileWith("bad-offset.pck", bytes))); });
Check("path traversal refused", () => Refuse(() => GodotPackager.Inspect(FileWith("traversal.pck", Pck(4, extraName: "../bad.gd")))));
Check("case collision refused", () => Refuse(() => GodotPackager.Inspect(FileWith("collision.pck", Pck(4, extraName: "PROJECT.binary")))));
Check("native extension refused", () => Refuse(() => GodotPackager.Inspect(FileWith("native.pck", Pck(4, extraName: "addons/plugin.gdextension")))));
Check("native versioned .so refused", () => Refuse(() => GodotPackager.Inspect(FileWith("native-so.pck", Pck(4, extraName: "plugins/native.so.1")))));
Check("C# required feature refused", () => Refuse(() => GodotPackager.Inspect(FileWith("csharp.pck", Pck(4, features: ["4.7", "C#"])))));
Check("double precision feature refused", () => Refuse(() => GodotPackager.Inspect(FileWith("double.pck", Pck(4, features: ["4.7", "Double Precision"])))));
Check("unknown engine feature refused", () => Refuse(() => GodotPackager.Inspect(FileWith("custom.pck", Pck(4, features: ["4.7", "CustomEngineModule"])))));
Check("newer project feature refused", () => Refuse(() => GodotPackager.Inspect(FileWith("future.pck", Pck(4, features: ["4.8"])))));
Check("ordinary PE without pack refused", () => Refuse(() => GodotPackager.Inspect(windows)));
Check("output overwrite refused", () => { string source = FileWith("overwrite.pck", Pck(4)), existing = FileWith("existing.zip", [1, 2, 3]); Refuse(() => GodotPackager.Package(new(source, windows, existing, TargetPlatform.Windows, "Probe", "4.7.1"))); True(File.ReadAllBytes(existing).SequenceEqual(new byte[] { 1, 2, 3 }), "Output changed."); });
Check("output nested in source refused", () => { string folder = Path.Combine(root, "source-dir"); Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, "game.pck"), Pck(4)); Refuse(() => GodotPackager.Package(new(folder, windows, Path.Combine(folder, "output.zip"), TargetPlatform.Windows, "Probe", "4.7.1"))); });
Check("runtime game payload refused", () => { string supplied = FileWith("runtime-game.exe", WithFooter(pe, Pck(4))); Refuse(() => Package(FileWith("clean.pck", Pck(4)), runtime: supplied)); });
Console.WriteLine($"RESULT {passed} passed; {failed} failed; fixtures={root}");
return failed == 0 ? 0 : 1;

static byte[] FakePe() { var bytes = new byte[256]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 64); bytes[64] = (byte)'P'; bytes[65] = (byte)'E'; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), 0x8664); return bytes; }
static byte[] FakeMach() { var bytes = new byte[64]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xfeedfacf); return bytes; }
static byte[] WithFooter(byte[] native, byte[] pack) { using var bytes = new MemoryStream(); bytes.Write(native); bytes.Write(pack); using var writer = new BinaryWriter(bytes, Encoding.UTF8, true); writer.Write((ulong)pack.Length); writer.Write(0x43504447u); return bytes.ToArray(); }
static byte[] Pck(int format, uint flags = 2, string? extraName = null, string[]? features = null)
{
    var resources = new List<(string Name, byte[] Bytes)> { ("project.binary", ProjectBinary(features ?? ["4.7", "GL Compatibility"])) };
    if (extraName is not null) resources.Add((extraName, Encoding.UTF8.GetBytes("sidecar")));
    using var buffer = new MemoryStream(); using var writer = new BinaryWriter(buffer, Encoding.UTF8, true);
    writer.Write(0x43504447u); writer.Write((uint)format); writer.Write(4u); writer.Write(7u); writer.Write(1u); writer.Write(flags); writer.Write(0UL);
    if (format >= 3) writer.Write(0UL); writer.Write(new byte[64]);
    int directorySize = 4 + resources.Sum(r => 4 + ((Encoding.UTF8.GetByteCount("res://" + r.Name) + 3) / 4) * 4 + 36);
    int fileBase = format == 2 ? 96 + directorySize : 112;
    int directory = format == 2 ? 96 : fileBase + resources.Sum(r => r.Bytes.Length);
    buffer.Position = 24; writer.Write((ulong)fileBase); if (format >= 3) writer.Write((ulong)directory);
    buffer.Position = fileBase; foreach (var resource in resources) writer.Write(resource.Bytes);
    buffer.Position = directory; writer.Write((uint)resources.Count); ulong offset = 0;
    foreach (var resource in resources) { byte[] path = Encoding.UTF8.GetBytes("res://" + resource.Name); int padded = ((path.Length + 3) / 4) * 4; writer.Write((uint)padded); writer.Write(path); writer.Write(new byte[padded - path.Length]); writer.Write(offset); writer.Write((ulong)resource.Bytes.Length); writer.Write(MD5.HashData(resource.Bytes)); writer.Write(0u); offset += (uint)resource.Bytes.Length; }
    return buffer.ToArray();
}
static byte[] ProjectBinary(string[] features)
{
    using var variant = new MemoryStream(); using (var writer = new BinaryWriter(variant, Encoding.UTF8, true)) { writer.Write(34u); writer.Write((uint)features.Length); foreach (string feature in features) { byte[] text = Encoding.UTF8.GetBytes(feature + "\0"); writer.Write((uint)text.Length); writer.Write(text); writer.Write(new byte[(4 - text.Length % 4) % 4]); } }
    using var bytes = new MemoryStream(); using var output = new BinaryWriter(bytes, Encoding.UTF8, true); output.Write(Encoding.ASCII.GetBytes("ECFG")); output.Write(1u); byte[] key = Encoding.UTF8.GetBytes("application/config/features"); output.Write((uint)key.Length); output.Write(key); output.Write((uint)variant.Length); output.Write(variant.ToArray()); return bytes.ToArray();
}
static byte[] MacRuntime()
{
    using var bytes = new MemoryStream(); using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
    {
        var binary = zip.CreateEntry("macos_template.app/Contents/MacOS/godot_macos_release.universal"); binary.ExternalAttributes = unchecked((int)(0x81EDu << 16)); using (var stream = binary.Open()) stream.Write(FakeMach());
        var plist = zip.CreateEntry("macos_template.app/Contents/Info.plist"); using (var writer = new StreamWriter(plist.Open())) writer.Write("<?xml version=\"1.0\"?><plist><dict><key>CFBundleExecutable</key><string>$binary</string><key>CFBundleName</key><string>$name</string><key>CFBundleIdentifier</key><string>$bundle_identifier</string>$liquid_glass_icon</dict></plist>");
        var privacy = zip.CreateEntry("macos_template.app/Contents/Resources/PrivacyInfo.xcprivacy"); using var privacyWriter = new StreamWriter(privacy.Open()); privacyWriter.Write("<?xml version=\"1.0\"?><plist><dict>$priv_tracking $priv_collection</dict></plist>");
    } return bytes.ToArray();
}
static byte[] ReadEntry(ZipArchive archive, string name) { using var source = archive.GetEntry(name)?.Open() ?? throw new Exception("Missing entry " + name); using var bytes = new MemoryStream(); source.CopyTo(bytes); return bytes.ToArray(); }
static List<byte> CreatorHosts(string path) { byte[] bytes = File.ReadAllBytes(path); int end = bytes.Length - 22, cursor = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(end + 16)); int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(end + 10)); var result = new List<byte>(); for (int i = 0; i < count; i++) { result.Add(bytes[cursor + 5]); cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 28)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 30)) + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 32)); } return result; }
