using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DustoreX.AutoConverter;

int passed = 0;
var failures = new List<string>();
Run("NW.js Windows ZIP runtime + app.nw payload", box =>
{
    string input = box.NwPayload(); byte[] before = File.ReadAllBytes(input);
    var result = NwPackager.Package(box.Request(input, box.NwWindows(), TargetPlatform.Windows));
    using var zip = ZipFile.OpenRead(result.OutputPath);
    Assert(Read(zip.GetEntry("Probe/package.nw")!).SequenceEqual(before), "NW application ZIP bytes changed.");
    Assert(zip.GetEntry("Probe/Probe.exe") is not null, "Missing renamed NW executable.");
    CheckManifest(zip, "NW.js");
});
Run("NW.js source www -> Mac -> Windows preserves payload", box =>
{
    box.Text("source/www/package.json", "{\"name\":\"probe\",\"main\":\"index.html\",\"nwjsVersion\":\"0.117.0\"}");
    box.Text("source/www/index.html", "<html>synthetic</html>");
    var mac = NwPackager.Package(box.Request(box.Path("source"), box.NwMac(), TargetPlatform.MacOS));
    using var macZip = ZipFile.OpenRead(mac.OutputPath);
    byte[] payload = Read(macZip.GetEntry("Probe.app/Contents/Resources/app.nw")!);
    Assert(macZip.GetEntry("Probe.app/Contents/MacOS/nwjs")!.ExternalAttributes == Box.Executable, "Mac execute mode changed.");
    Assert(Read(macZip.GetEntry("Probe.app/Contents/Frameworks/Test.framework/Test")!).SequenceEqual(Encoding.UTF8.GetBytes("Versions/A/Test")), "Mac symlink bytes changed.");
    CheckUnixCreator(File.ReadAllBytes(mac.OutputPath));
    var request = box.Request(mac.OutputPath, box.NwWindows(), TargetPlatform.Windows) with { OutputPath = box.Path("roundtrip.zip") };
    var windows = NwPackager.Package(request);
    using var winZip = ZipFile.OpenRead(windows.OutputPath);
    Assert(Read(winZip.GetEntry("Probe/package.nw")!).SequenceEqual(payload), "NW macOS -> Windows changed payload.");
});
Run("NW.js exact source runtime requirement", box => Assert(NwPackager.Inspect(box.NwPayload()).RequiredVersion == "0.117.0", "Wrong runtime version."));
Run("NW.js app version is not runtime version", box => Assert(NwPackager.Inspect(box.NwPayload(version: null)).RequiredVersion is null, "Used application version as NW runtime version."));
Run("NW.js version mismatch is refused", box => Reject(() => NwPackager.Package(box.Request(box.NwPayload(), box.NwWindows(), TargetPlatform.Windows) with { RuntimeVersion = "0.116.0" })));
Run("NW.js native .node is refused", box => Reject(() => NwPackager.Package(box.Request(box.NwPayload(extra: new Item("node_modules/native.node", [1])), box.NwWindows(), TargetPlatform.Windows))));
Run("NW.js evalNWBin compiled JavaScript is refused", box => Reject(() => NwPackager.Package(box.Request(box.NwPayload(extra: Item.Text("main.js", "nw.Window.get().evalNWBin(null,'code.bin')")), box.NwWindows(), TargetPlatform.Windows))));
Run("NW.js payload traversal is refused", box => Reject(() => NwPackager.Package(box.Request(box.NwPayload(extra: Item.Text("../bad", "bad")), box.NwWindows(), TargetPlatform.Windows))));
Run("NW.js missing local main is refused", box => Reject(() => NwPackager.Package(box.Request(box.Zip("bad.nw", Item.Text("package.json", "{\"main\":\"absent.html\"}")), box.NwWindows(), TargetPlatform.Windows))));
Run("NW.js invalid main is refused during Inspect", box => Reject(() => NwPackager.Inspect(box.Zip("bad-main.nw", Item.Text("package.json", "{\"main\":\"https://example.org/app\"}")))));
Run("NW.js conflicting exact versions are refused", box => Reject(() => NwPackager.Inspect(box.Zip("bad-versions.nw", Item.Text("package.json", "{\"main\":\"index.html\",\"nwjsVersion\":\"0.117.0\",\"engines\":{\"nw\":\"0.116.0\"}}"), Item.Text("index.html", "probe")))));
Run("Ren'Py source version from vc_version.py", box => Assert(RenPyPackager.Inspect(box.RenGame()).RequiredVersion == "8.5.3", "RenPy build version was not normalized."));
Run("Ren'Py Windows SDK packaging preserves scripts", box =>
{
    string input = box.RenGame();
    var result = RenPyPackager.Package(box.Request(input, box.RenSdk(), TargetPlatform.Windows) with { RuntimeVersion = "8.5.3" });
    using var zip = ZipFile.OpenRead(result.OutputPath);
    Assert(Read(zip.GetEntry("Probe/game/script.rpy")!).SequenceEqual(Encoding.UTF8.GetBytes("label start:\n    return\n")), "RenPy script changed.");
    Assert(zip.GetEntry("Probe/Probe.exe") is not null && zip.GetEntry("Probe/Probe.py") is not null && zip.GetEntry("Probe/lib/py3-windows-x86_64/Probe.exe") is not null, "Missing native launcher/bootstrap.");
    Assert(zip.GetEntry("Probe/game/saves/slot.sav") is null, "Save files were migrated accidentally.");
    CheckManifest(zip, "Ren'Py");
});
Run("Ren'Py Mac layout and reverse Windows packaging", box =>
{
    var mac = RenPyPackager.Package(box.Request(box.RenGame(), box.RenSdk(), TargetPlatform.MacOS) with { RuntimeVersion = "8.5.3" });
    using var zip = ZipFile.OpenRead(mac.OutputPath);
    Assert(zip.GetEntry("Probe.app/Contents/MacOS/Probe") is not null && zip.GetEntry("Probe.app/Contents/MacOS/librenpython.dylib") is not null, "Missing macOS native runtime.");
    Assert(zip.GetEntry("Probe.app/Contents/Resources/lib/python3.12/os.py") is not null && zip.GetEntry("Probe.app/Contents/Resources/autorun/Probe.py") is not null, "Wrong standard-library/bootstrap layout.");
    CheckUnixCreator(File.ReadAllBytes(mac.OutputPath));
    var windows = RenPyPackager.Package(box.Request(mac.OutputPath, box.RenSdk(), TargetPlatform.Windows) with { OutputPath = box.Path("reverse.zip"), RuntimeVersion = "8.5.3" });
    using var reverse = ZipFile.OpenRead(windows.OutputPath);
    Assert(Read(reverse.GetEntry("Probe/game/script.rpy")!).SequenceEqual(Read(zip.GetEntry("Probe.app/Contents/Resources/autorun/game/script.rpy")!)), "Reverse RenPy transfer changed script.");
});
Run("Ren'Py version mismatch is refused", box => Reject(() => RenPyPackager.Package(box.Request(box.RenGame("8.4.1"), box.RenSdk(), TargetPlatform.Windows) with { RuntimeVersion = "8.5.3" })));
Run("Ren'Py native game module is refused", box => Reject(() => RenPyPackager.Package(box.Request(box.RenGame(extra: new Item("game/plugin.dll", [1])), box.RenSdk(), TargetPlatform.Windows) with { RuntimeVersion = "8.5.3" })));
Run("Ren'Py ctypes source is refused", box => Reject(() => RenPyPackager.Package(box.Request(box.RenGame(extra: Item.Text("game/custom.rpy", "init python:\n    import ctypes\n")), box.RenSdk(), TargetPlatform.Windows) with { RuntimeVersion = "8.5.3" })));
Run("Ren'Py RPA with portable assets is retained", box =>
{
    byte[] archive = Box.Rpa("images/bg.png"); string input = box.RenGame(extra: new Item("game/assets.rpa", archive));
    var result = RenPyPackager.Package(box.Request(input, box.RenSdk(), TargetPlatform.Windows) with { RuntimeVersion = "8.5.3" });
    using var zip = ZipFile.OpenRead(result.OutputPath);
    Assert(Read(zip.GetEntry("Probe/game/assets.rpa")!).SequenceEqual(archive), "RPA changed.");
});
Run("Ren'Py RPA native index entry is refused without pickle execution", box => Reject(() => RenPyPackager.Package(box.Request(box.RenGame(extra: new Item("game/native.rpa", Box.Rpa("plugin.dll"))), box.RenSdk(), TargetPlatform.Windows) with { RuntimeVersion = "8.5.3" })));
Run("Ren'Py renpy game name has no duplicate paths", box =>
{
    var result = RenPyPackager.Package(box.Request(box.RenGame(), box.RenSdk(), TargetPlatform.Windows) with { GameName = "renpy", RuntimeVersion = "8.5.3" });
    using var zip = ZipFile.OpenRead(result.OutputPath);
    Assert(zip.Entries.Select(e => e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == zip.Entries.Count, "Duplicate generated ZIP paths.");
});
Run("Ren'Py main runtime name collision is refused", box =>
{
    string sdk = box.RenSdk();
    using (var zip = ZipFile.Open(sdk, ZipArchiveMode.Update)) { var entry = zip.CreateEntry("sdk/lib/py3-mac-universal/python"); using var target = entry.Open(); target.Write([0]); }
    Reject(() => RenPyPackager.Package(box.Request(box.RenGame(), sdk, TargetPlatform.MacOS) with { GameName = "python", RuntimeVersion = "8.5.3" }));
});
Run("RuntimeCatalog Windows always x64 despite Mac architecture selection", box => Assert(RuntimeCatalog.RuntimeFileName("nw", "0.117.0", TargetPlatform.Windows, "arm64") == "nwjs-v0.117.0-win-x64.zip", "Windows selected an unsupported or unintended architecture."));
Run("RuntimeCatalog URL rejects injected filenames", box => Reject(() => RuntimeCatalog.RuntimeUrl("nw", "0.117.0", "../other.zip")));
Run("RuntimeCatalog cache lock type errors return false", box =>
{
    box.Text("cache/love-11.5-win64.zip", "synthetic bytes");
    foreach (string value in new[] { "[]", "{\"sha256\":42}", "null" })
    {
        box.Text("cache/love-11.5-win64.zip.lock.json", value);
        Assert(!RuntimeCatalog.VerifyCached(box.Path("cache/love-11.5-win64.zip")), "Malformed lock was accepted.");
    }
});
Run("RuntimeCatalog bound cache detects tampering and stale URL", box =>
{
    string path = box.Path("cache/love-11.5-win64.zip"); box.Text("cache/love-11.5-win64.zip", "synthetic cache integrity fixture, not a runtime");
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    object Lock(string url, bool verified = true) => new { schemaVersion = 2, method = "love", file = "love-11.5-win64.zip", url, sha256 = hash, officialChecksum = hash, officialChecksumVerified = verified, version = "11.5" };
    box.Text("cache/love-11.5-win64.zip.lock.json", JsonSerializer.Serialize(Lock(RuntimeCatalog.RuntimeUrl("love", "11.5", "love-11.5-win64.zip"))));
    Assert(RuntimeCatalog.VerifyCached(path), "Valid structural integrity fixture failed.");
    box.Text("cache/love-11.5-win64.zip.lock.json", JsonSerializer.Serialize(Lock("https://example.org/fake.zip")));
    Assert(!RuntimeCatalog.VerifyCached(path), "Foreign cached URL accepted.");
    box.Text("cache/love-11.5-win64.zip.lock.json", JsonSerializer.Serialize(Lock(RuntimeCatalog.RuntimeUrl("love", "11.5", "love-11.5-win64.zip"), false)));
    Assert(!RuntimeCatalog.VerifyCached(path), "Unchecked cache accepted.");
    box.Text("cache/love-11.5-win64.zip.lock.json", JsonSerializer.Serialize(Lock(RuntimeCatalog.RuntimeUrl("love", "11.5", "love-11.5-win64.zip"))));
    box.Text("cache/love-11.5-win64.zip", "tampered"); Assert(!RuntimeCatalog.VerifyCached(path), "Tampered runtime accepted.");
});
if (args.Length == 3 && args[0] == "--official-runtimes")
{
    string runtimeRoot = System.IO.Path.GetFullPath(args[1]), evidenceRoot = System.IO.Path.GetFullPath(args[2]);
    Directory.CreateDirectory(evidenceRoot);
    string nwSource = System.IO.Path.Combine(evidenceRoot, "nw-source"); Directory.CreateDirectory(nwSource);
    File.WriteAllText(System.IO.Path.Combine(nwSource, "package.json"), "{\"name\":\"dustorex-probe\",\"main\":\"index.html\",\"nwjsVersion\":\"0.117.0\"}");
    File.WriteAllText(System.IO.Path.Combine(nwSource, "index.html"), "<html><body>Self-generated DustoreX packaging probe.</body></html>");
    string renSource = System.IO.Path.Combine(evidenceRoot, "renpy-source"); Directory.CreateDirectory(System.IO.Path.Combine(renSource, "game"));
    Directory.CreateDirectory(System.IO.Path.Combine(renSource, "renpy"));
    File.WriteAllText(System.IO.Path.Combine(renSource, "game", "script.rpy"), "label start:\n    return\n");
    File.WriteAllText(System.IO.Path.Combine(renSource, "renpy", "vc_version.py"), "version = '8.5.3.26051504'\n");
    Run("Official NW.js Windows ZIP structural packaging", _ =>
    {
        var result = NwPackager.Package(new(nwSource, System.IO.Path.Combine(runtimeRoot, "nwjs-v0.117.0-win-x64.zip"), System.IO.Path.Combine(evidenceRoot, "nw-windows.zip"), TargetPlatform.Windows, "DustoreXProbe", "0.117.0"));
        using var zip = ZipFile.OpenRead(result.OutputPath); CheckManifest(zip, "NW.js");
        Assert(zip.GetEntry("DustoreXProbe/DustoreXProbe.exe") is not null, "Missing official NW Windows executable.");
    });
    Run("Official NW.js macOS ZIP structural packaging and reverse Windows", _ =>
    {
        var result = NwPackager.Package(new(nwSource, System.IO.Path.Combine(runtimeRoot, "nwjs-v0.117.0-osx-arm64.zip"), System.IO.Path.Combine(evidenceRoot, "nw-macos.zip"), TargetPlatform.MacOS, "DustoreXProbe", "0.117.0"));
        using var zip = ZipFile.OpenRead(result.OutputPath); CheckManifest(zip, "NW.js"); CheckUnixCreator(File.ReadAllBytes(result.OutputPath));
        byte[] expected = Read(zip.GetEntry("DustoreXProbe.app/Contents/Resources/app.nw")!);
        var reverse = NwPackager.Package(new(result.OutputPath, System.IO.Path.Combine(runtimeRoot, "nwjs-v0.117.0-win-x64.zip"), System.IO.Path.Combine(evidenceRoot, "nw-reverse-windows.zip"), TargetPlatform.Windows, "DustoreXProbe", "0.117.0"));
        using var windows = ZipFile.OpenRead(reverse.OutputPath); Assert(Read(windows.GetEntry("DustoreXProbe/package.nw")!).SequenceEqual(expected), "Official NW reverse payload changed.");
    });
    Run("Official Ren'Py Windows SDK structural packaging", _ =>
    {
        var result = RenPyPackager.Package(new(renSource, System.IO.Path.Combine(runtimeRoot, "renpy-8.5.3-sdk.zip"), System.IO.Path.Combine(evidenceRoot, "renpy-windows.zip"), TargetPlatform.Windows, "DustoreXProbe", "8.5.3"));
        using var zip = ZipFile.OpenRead(result.OutputPath); CheckManifest(zip, "Ren'Py");
        Assert(zip.GetEntry("DustoreXProbe/lib/py3-windows-x86_64/DustoreXProbe.exe") is not null, "Missing official RenPy native launcher.");
    });
    Run("Official Ren'Py macOS SDK structural packaging and reverse Windows", _ =>
    {
        var result = RenPyPackager.Package(new(renSource, System.IO.Path.Combine(runtimeRoot, "renpy-8.5.3-sdk.zip"), System.IO.Path.Combine(evidenceRoot, "renpy-macos.zip"), TargetPlatform.MacOS, "DustoreXProbe", "8.5.3"));
        using var zip = ZipFile.OpenRead(result.OutputPath); CheckManifest(zip, "Ren'Py"); CheckUnixCreator(File.ReadAllBytes(result.OutputPath));
        var reverse = RenPyPackager.Package(new(result.OutputPath, System.IO.Path.Combine(runtimeRoot, "renpy-8.5.3-sdk.zip"), System.IO.Path.Combine(evidenceRoot, "renpy-reverse-windows.zip"), TargetPlatform.Windows, "DustoreXProbe", "8.5.3"));
        using var windows = ZipFile.OpenRead(reverse.OutputPath);
        Assert(Read(windows.GetEntry("DustoreXProbe/game/script.rpy")!).SequenceEqual(Read(zip.GetEntry("DustoreXProbe.app/Contents/Resources/autorun/game/script.rpy")!)), "Official RenPy reverse script changed.");
    });
    File.WriteAllText(System.IO.Path.Combine(evidenceRoot, "verification.json"), JsonSerializer.Serialize(new { status = failures.Count == 0 ? "Pass" : "Fail", passed, failed = failures.Count, validation = "Structure, payload bytes and ZIP Unix metadata only; no supplied game/runtime was launched.", macOSExecution = "NotTested", windowsExecution = "NotTested" }, new JsonSerializerOptions { WriteIndented = true }));
}
Console.WriteLine($"RESULT: {passed} passed; {failures.Count} failed. Synthetic, nonrunnable runtime fixtures; no supplied game/runtime was launched.");
foreach (string failure in failures) Console.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;

void Run(string name, Action<Box> action) { try { using var box = new Box(); action(box); passed++; Console.WriteLine("PASS: " + name); } catch (Exception e) { failures.Add(name + ": " + e.Message); Console.WriteLine("FAIL: " + name + ": " + e.Message); } }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Reject(Action action) { try { action(); } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return; } throw new InvalidOperationException("Expected refusal."); }
static byte[] Read(ZipArchiveEntry entry) { using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
static void CheckManifest(ZipArchive zip, string engine) { using var doc = JsonDocument.Parse(Read(zip.GetEntry("package-manifest.json")!)); Assert(doc.RootElement.GetProperty("engine").GetString() == engine && !doc.RootElement.GetProperty("testedOnTarget").GetBoolean(), "Manifest misstates target verification."); }
static void CheckUnixCreator(byte[] zip)
{
    int end = zip.Length - 22; Assert(BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)) == 0x06054b50, "Missing ZIP footer.");
    int cursor = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16))); int entries = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
    for (int i = 0; i < entries; i++) { Assert(BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cursor)) == 0x02014b50 && zip[cursor + 5] == 3, "macOS ZIP is not tagged Unix."); cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cursor + 28)) + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cursor + 30)) + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cursor + 32)); }
    Assert(cursor == end, "Bad ZIP central bounds.");
}

sealed record Item(string Name, byte[] Bytes, int Attributes = Box.Regular) { internal static Item Text(string name, string text, int attributes = Box.Regular) => new(name, Encoding.UTF8.GetBytes(text), attributes); }
sealed class Box : IDisposable
{
    internal const int Regular = unchecked((int)0x81A40000), Executable = unchecked((int)0x81ED0000), Link = unchecked((int)0xA1FF0000);
    private string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DustoreX-runtime-checks-" + Guid.NewGuid().ToString("N"));
    internal Box() => Directory.CreateDirectory(Root);
    internal string Path(string relative) => System.IO.Path.Combine(Root, relative);
    internal string Text(string relative, string text) { string path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); return path; }
    internal string Zip(string relative, params Item[] items) { string path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllBytes(path, ZipBytes(items)); return path; }
    internal PackageRequest Request(string input, string runtime, TargetPlatform target) => new(input, runtime, Path("result.zip"), target, "Probe", "0.117.0");
    internal string NwPayload(string? version = "0.117.0", Item? extra = null) => Zip("game.nw", new[] { Item.Text("package.json", "{\"name\":\"probe\",\"version\":\"1.2.3\",\"main\":\"index.html\"" + (version is null ? "" : ",\"nwjsVersion\":\"" + version + "\"") + "}"), Item.Text("index.html", "<html>synthetic</html>") }.Concat(extra is null ? [] : new[] { extra }).ToArray());
    internal string NwWindows() => Zip("nw-windows.zip", new Item("nw/nw.exe", Pe(), Executable), new Item("nw/nw.dll", Pe()), Item.Text("nw/icudtl.dat", "synthetic"), Item.Text("nw/LICENSE", "synthetic"));
    internal string NwMac() => Zip("nw-mac.zip", Item.Text("nw/nwjs.app/Contents/Info.plist", "<plist><dict><key>CFBundleExecutable</key><string>nwjs</string></dict></plist>"), new Item("nw/nwjs.app/Contents/MacOS/nwjs", MachO(), Executable), Item.Text("nw/nwjs.app/Contents/Frameworks/Test.framework/Versions/A/Test", "synthetic framework"), Item.Text("nw/nwjs.app/Contents/Frameworks/Test.framework/Test", "Versions/A/Test", Link));
    internal string RenGame(string version = "8.5.3", Item? extra = null) => Zip("ren-game.zip", new[] { Item.Text("game/script.rpy", "label start:\n    return\n"), Item.Text("game/saves/slot.sav", "save"), Item.Text("renpy/vc_version.py", "version = '" + version + ".12345678'\n") }.Concat(extra is null ? [] : new[] { extra }).ToArray());
    internal string RenSdk() => Zip("ren-sdk.zip", Item.Text("sdk/renpy.py", "# synthetic launcher, not runnable\n"), Item.Text("sdk/renpy/vc_version.py", "version = '8.5.3.12345678'\n"), Item.Text("sdk/renpy/bootstrap.py", "# synthetic engine\n"), Item.Text("sdk/lib/python3.12/os.py", "# synthetic standard library\n"), new Item("sdk/lib/py3-windows-x86_64/renpy.exe", Pe(), Executable), new Item("sdk/lib/py3-windows-x86_64/librenpython.dll", Pe()), new Item("sdk/lib/py3-mac-universal/renpy", MachO(), Executable), new Item("sdk/lib/py3-mac-universal/librenpython.dylib", MachO(), Executable), Item.Text("sdk/LICENSE.txt", "synthetic runtime only"));
    private static byte[] ZipBytes(Item[] items) { using var buffer = new MemoryStream(); using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)) foreach (var item in items) { var entry = zip.CreateEntry(item.Name); entry.ExternalAttributes = item.Attributes; using var target = entry.Open(); target.Write(item.Bytes); } return buffer.ToArray(); }
    private static byte[] Pe() { byte[] bytes = new byte[512]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(60), 128); bytes[128] = (byte)'P'; bytes[129] = (byte)'E'; BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(132), 0x8664); return bytes; }
    private static byte[] MachO() { byte[] bytes = new byte[128]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xFEEDFACF); return bytes; }
    internal static byte[] Rpa(string file) { byte[] header = Encoding.ASCII.GetBytes("RPA-3.0 0000000000000040 00000000\n"); using var output = new MemoryStream(); output.Write(header); while (output.Length < 64) output.WriteByte(0); using (var compressed = new ZLibStream(output, CompressionLevel.Optimal, true)) { compressed.Write(new byte[] { 0x80, 2, (byte)'}', (byte)'X' }); byte[] text = Encoding.UTF8.GetBytes(file); Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, text.Length); compressed.Write(length); compressed.Write(text); compressed.Write(new byte[] { (byte)'q', 0, (byte)'.' }); } return output.ToArray(); }
    public void Dispose() { string path = System.IO.Path.GetFullPath(Root); string temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\','/') + System.IO.Path.DirectorySeparatorChar; if (!path.StartsWith(temporary,StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(path).StartsWith("DustoreX-runtime-checks-",StringComparison.Ordinal)) throw new IOException("Refused fixture cleanup."); Directory.Delete(path,true); }
}
