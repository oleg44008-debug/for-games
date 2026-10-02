using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ConverterChecks;
using DustoreX.AutoConverter;
using SystemChecks;

// Synthetic static fixtures only. No games, runtimes, or generated shell scripts are executed.
var suite = new CheckSuite();
#if CORE_ONLY
await suite.RunAsync("Pure Core assembly has no browser, ASP.NET or WinForms dependency", box =>
{
    var assembly = typeof(ConversionEngine).Assembly;
    CheckSuite.Assert(assembly.GetName().Name == "DustoreX.AutoConverter.Core", "Checks referenced the CLI/browser host instead of the shared pure Core.");
    CheckSuite.Assert(assembly.GetType("DustoreX.AutoConverter.UiServer") is null, "Browser server was compiled into Core.");
    CheckSuite.Assert(!assembly.GetReferencedAssemblies().Any(a => a.Name is not null && (a.Name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || a.Name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal) || a.Name == "System.Windows.Forms")), "Core acquired a web or native UI dependency.");
});
#endif
foreach (TargetPlatform target in Enum.GetValues<TargetPlatform>())
{
    await suite.RunAsync($"LÖVE portable payload plans {target} with exact source version", box =>
    {
        string input = box.Love(extra: [ZipItem.Text("conf.lua", "function love.conf(t) t.version = '11.4' end")]);
        ConversionPlan plan = ConversionEngine.Inspect(input, target);
        CheckSuite.Assert(plan.Method == "love" && plan.CanConvert && plan.RuntimeVersion == "11.4", "Valid LÖVE source failed plan selection/version preservation.");
        CheckSuite.Assert(plan.Target == ConversionEngine.TargetName(target), "Requested target was lost.");
    });
    await suite.RunAsync($"NW.js portable payload plans {target}", box =>
    {
        string input = box.Zip("web.zip", ZipItem.Text("package.json", "{\"main\":\"index.html\",\"nwjsVersion\":\"0.92.0\"}"), ZipItem.Text("index.html", "<!doctype html><p>Synthetic</p>"));
        ConversionPlan plan = ConversionEngine.Inspect(input, target);
        CheckSuite.Assert(plan.Method == "nw" && plan.CanConvert && plan.RuntimeVersion == "0.92.0", "Valid NW.js payload failed plan selection/version preservation.");
    });
    await suite.RunAsync($"Ren'Py portable payload plans {target}", box =>
    {
        box.Text("renpy-game/game/script.rpy", "label start:\n    \"Synthetic fixture\"\n    return\n");
        box.Text("renpy-game/renpy/vc_version.py", "version = '8.5.3'\n");
        ConversionPlan plan = ConversionEngine.Inspect(box.Path("renpy-game"), target);
        CheckSuite.Assert(plan.Method == "renpy" && plan.CanConvert && plan.RuntimeVersion == "8.5.3", "Valid Ren'Py payload failed plan selection/version preservation.");
    });
    await suite.RunAsync($"Unrecognized text rejects automatic conversion to {target}", box =>
    {
        ConversionPlan plan = ConversionEngine.Inspect(box.Text("notes.txt", "A synthetic unrecognized document."), target);
        CheckSuite.Assert(!plan.CanConvert && plan.Method == "unsupported", "Unrecognized data was presented as automatically convertible.");
        CheckSuite.Assert(plan.Warnings.Any(w => w.StartsWith("Godot:", StringComparison.Ordinal)), "Backend rejection evidence disappeared.");
    });
}
await suite.RunAsync("Actual Windows executable plans Wine and exposes dependency hint", box =>
{
    ConversionPlan plan = ConversionEngine.Inspect(box.Write("game.exe", FixtureBox.PortableExecutable()), TargetPlatform.MacOS);
    CheckSuite.Assert(plan.Method == "wine" && plan.CanConvert && plan.Detail.Contains("Wine", StringComparison.Ordinal), "Windows executable did not explain required Wine dependency.");
});
await suite.RunAsync("Actual Windows executable already targets Windows", box =>
{
    ConversionPlan plan = ConversionEngine.Inspect(box.Write("game.exe", FixtureBox.PortableExecutable()), TargetPlatform.Windows);
    CheckSuite.Assert(plan.Method == "native" && !plan.CanConvert && plan.Detail.Contains("Windows", StringComparison.Ordinal), "Same-target Windows build was unnecessarily converted.");
});
await suite.RunAsync("Actual Mac executable already targets macOS", box =>
{
    ConversionPlan plan = ConversionEngine.Inspect(box.Write("game", FixtureBox.MachO()), TargetPlatform.MacOS);
    CheckSuite.Assert(plan.Method == "native" && !plan.CanConvert && plan.Detail.Contains("macOS", StringComparison.Ordinal), "Same-target Mach-O build was not recognized.");
});
await suite.RunAsync("Mac app bundle already targets macOS", box =>
{
    box.Text("Game.app/Contents/Info.plist", FixtureBox.Plist("game"));
    box.Write("Game.app/Contents/MacOS/game", FixtureBox.MachO());
    ConversionPlan plan = ConversionEngine.Inspect(box.Path("Game.app"), TargetPlatform.MacOS);
    CheckSuite.Assert(plan.Method == "native" && !plan.CanConvert, "Same-target Mac bundle was not recognized.");
});
await suite.RunAsync("Mac native executable cannot silently become Windows executable", box =>
{
    ConversionPlan plan = ConversionEngine.Inspect(box.Write("game", FixtureBox.MachO()), TargetPlatform.Windows);
    CheckSuite.Assert(plan.Method == "unsupported" && !plan.CanConvert, "Native Mach-O was presented as a portable Windows rebuild.");
});
await suite.RunAsync("Fake EXE extension cannot select Wine", box =>
{
    ConversionPlan plan = ConversionEngine.Inspect(box.Text("fake.exe", "synthetic text"), TargetPlatform.MacOS);
    CheckSuite.Assert(plan.Method == "unsupported" && !plan.CanConvert, "EXE extension was trusted without a PE header.");
});
await suite.RunAsync("Invalid target, architecture and missing input are rejected", box =>
{
    string input = box.Love();
    CheckSuite.Reject(() => ConversionEngine.Inspect(input, (TargetPlatform)987));
    CheckSuite.Reject(() => ConversionEngine.Inspect(input, TargetPlatform.MacOS, "../x64"));
    CheckSuite.Reject(() => ConversionEngine.Inspect(box.Path("missing"), TargetPlatform.Windows));
});
await suite.RunAsync("Unknown source version carries explicit compatibility warning", box =>
{
    ConversionPlan plan = ConversionEngine.Inspect(box.Love(), TargetPlatform.MacOS);
    CheckSuite.Assert(plan.Method == "love" && plan.RuntimeVersion == "11.5" && plan.Warnings.Any(w => w.Contains("Версия исходного", StringComparison.Ordinal)), "Fallback engine version was silently assumed.");
});
await suite.RunAsync("Unsafe native LÖVE payload is refused before runtime selection", box =>
{
    string input = box.Love(extra: [new ZipItem("native.dll", FixtureBox.PortableExecutable(isDll: true))]);
    CheckSuite.Reject(() => ConversionEngine.Inspect(input, TargetPlatform.MacOS));
});
await suite.RunAsync("NW missing main preserves adapter refusal instead of portable claim", box =>
{
    string input = box.Zip("invalid-web.zip", ZipItem.Text("package.json", "{\"name\":\"synthetic\"}"), ZipItem.Text("index.html", "<p>Synthetic</p>"));
    ConversionPlan plan = ConversionEngine.Inspect(input, TargetPlatform.MacOS);
    CheckSuite.Assert(!plan.CanConvert && plan.Warnings.Any(w => w.StartsWith("Nw:", StringComparison.Ordinal)), "Invalid NW app did not retain adapter refusal.");
});
await suite.RunAsync("Explicit method cannot override inspected engine", async box =>
{
    await CheckSuite.RejectAsync(() => ConversionEngine.ConvertAsync(new ConversionRequest(box.Love(), TargetPlatform.MacOS, "Synthetic", box.Path("out.zip"), RuntimePath: box.MacRuntime(), Method: "wine")));
    CheckSuite.Assert(!File.Exists(box.Path("out.zip")), "Rejected method still wrote a package.");
});
await suite.RunAsync("Cancellation prevents output and runtime access", async box =>
{
    using var stop = new CancellationTokenSource(); stop.Cancel();
    bool cancelled = false;
    try { await ConversionEngine.ConvertAsync(new ConversionRequest(box.Love(), TargetPlatform.MacOS, "Synthetic", box.Path("out.zip"), RuntimePath: box.Path("missing-runtime.zip")), cancellation: stop.Token); }
    catch (OperationCanceledException) { cancelled = true; }
    CheckSuite.Assert(cancelled && !File.Exists(box.Path("out.zip")), "Cancellation failed to stop before packaging/runtime access.");
});
await suite.RunAsync("Orchestrator packages supplied runtime without claiming target testing", async box =>
{
    PackageResult result = await ConversionEngine.ConvertAsync(new ConversionRequest(box.Love(), TargetPlatform.MacOS, "Synthetic", box.Path("out.zip"), RuntimeVersion: "11.5", RuntimePath: box.MacRuntime()));
    using var zip = ZipFile.OpenRead(result.OutputPath);
    using var manifest = JsonDocument.Parse(Read(zip, "package-manifest.json"));
    CheckSuite.Assert(result.Status == "Packaged" && !manifest.RootElement.GetProperty("testedOnTarget").GetBoolean(), "Packaging was mislabeled as target execution.");
});
await suite.RunAsync("Runtime cache needs artifact and matching checksum lock", box =>
{
    string file = RuntimeCatalog.RuntimeFileName("nw", "0.92.0", TargetPlatform.Windows, "x64");
    string path = box.Path("cache/" + file);
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Missing artifact was treated cached.");
    box.Text("cache/" + file, "synthetic runtime checksum fixture");
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Missing checksum lock was treated cached.");
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    WriteCacheLock(path, hash);
    CheckSuite.Assert(RuntimeCatalog.VerifyCached(path), "Matching checksum was rejected.");
    File.AppendAllText(path, "modified");
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Modified runtime passed checksum verification.");
});
foreach (string badLock in new[] { "{", "{}", "{\"sha256\":42}", "[]" })
{
    await suite.RunAsync("Malformed checksum lock is safely rejected: " + badLock, box =>
    {
        string path = box.Text("runtime.zip", "synthetic runtime data");
        box.Text("runtime.zip.lock.json", badLock);
        CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Malformed cache lock was accepted.");
    });
}
await suite.RunAsync("Runtime cache hit is resolved without network download", async box =>
{
    string cache = box.Path("cache"), file = RuntimeCatalog.RuntimeFileName("nw", "0.92.0", TargetPlatform.Windows, "x64");
    string path = box.Text("cache/" + file, "synthetic checksum-only fixture; intentionally not a real runtime");
    WriteCacheLock(path, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    string resolved = await RuntimeCatalog.ResolveAsync("nw", "0.92.0", TargetPlatform.Windows, "x64", cacheDirectory: cache);
    CheckSuite.Assert(Path.GetFullPath(resolved) == Path.GetFullPath(path), "Verified runtime cache was not used.");
});
await suite.RunAsync("Runtime cache refuses legacy and unverified provenance locks", box =>
{
    string file = RuntimeCatalog.RuntimeFileName("nw", "0.92.0", TargetPlatform.Windows, "x64");
    string path = box.Text(file, "synthetic cache data");
    string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    File.WriteAllText(path + ".lock.json", JsonSerializer.Serialize(new { sha256 = hash }));
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Legacy integrity-only lock was treated as verified official provenance.");
    WriteCacheLock(path, hash, verified: false);
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Unverified runtime provenance was accepted.");
    WriteCacheLock(path, hash, url: "https://attacker.example.invalid/runtime.zip");
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Arbitrary runtime host was accepted as official.");
    WriteCacheLock(path, hash, official: new string('0', 64));
    CheckSuite.Assert(!RuntimeCatalog.VerifyCached(path), "Mismatch with recorded official checksum was accepted.");
});
await suite.RunAsync("Catalog honors exact engine versions, architectures and official hosts", box =>
{
    CheckSuite.Assert(RuntimeCatalog.RuntimeFileName("godot", "4.7.1", TargetPlatform.MacOS, "arm64") == "Godot_v4.7.1-stable_export_templates.tpz", "Godot runtime version drifted.");
    CheckSuite.Assert(RuntimeCatalog.RuntimeFileName("nw", "0.92.0", TargetPlatform.MacOS, "arm64").Contains("osx-arm64", StringComparison.Ordinal), "Mac runtime architecture was lost.");
    foreach (string method in new[] { "love", "godot", "nw", "renpy" })
    {
        string version = method == "godot" ? "4.7.1" : RuntimeCatalog.DefaultVersion(method);
        var uri = new Uri(RuntimeCatalog.RuntimeUrl(method, version, RuntimeCatalog.RuntimeFileName(method, version, TargetPlatform.MacOS, "arm64")));
        CheckSuite.Assert(uri.Scheme == "https" && new[] { "github.com", "dl.nwjs.io", "www.renpy.org" }.Contains(uri.Host), "Catalog does not use a fixed official HTTPS host.");
    }
    CheckSuite.Reject(() => RuntimeCatalog.RuntimeFileName("love", "../../bad", TargetPlatform.Windows, "x64"));
    CheckSuite.Reject(() => RuntimeCatalog.RuntimeFileName("unknown", "1.0", TargetPlatform.Windows, "x64"));
});
await suite.RunAsync("Wine wrapper quotes hostile-looking relative filename and preserves payload", box =>
{
    string name = "a'$(touch INJECTED);.exe";
    byte[] executable = FixtureBox.PortableExecutable();
    string input = box.Zip("source.zip", new ZipItem("nested/" + name, executable), ZipItem.Text("nested/asset.txt", "synthetic asset"));
    PackageResult result = CompatibilityPackager.Package(new PackageRequest(input, "", box.Path("wine.zip"), TargetPlatform.MacOS, "Synthetic & Game"));
    using var zip = ZipFile.OpenRead(result.OutputPath);
    string shell = Read(zip, "Synthetic & Game.app/Contents/MacOS/launch");
    CheckSuite.Assert(shell.Contains("exec \"$WINE\" 'a'\"'\"'$(touch INJECTED);.exe' \"$@\"", StringComparison.Ordinal), "Filename was interpolated into unquoted shell code.");
    CheckSuite.Assert(!File.Exists(box.Path("INJECTED")), "Generated script was unexpectedly executed.");
    using var payload = zip.GetEntry("Synthetic & Game.app/Contents/Resources/game/nested/" + name)!.Open();
    using var copy = new MemoryStream(); payload.CopyTo(copy);
    CheckSuite.Assert(copy.ToArray().SequenceEqual(executable), "Wrapper altered the Windows executable.");
    var document = XDocument.Parse(Read(zip, "Synthetic & Game.app/Contents/Info.plist"));
    CheckSuite.Assert(document.Descendants("string").Any(e => e.Value == "Synthetic & Game"), "Game name was not safely XML encoded.");
    using var manifest = JsonDocument.Parse(Read(zip, "DustoreX-conversion.json"));
    CheckSuite.Assert(result.Status == "RequiresWine" && !manifest.RootElement.GetProperty("testedOnTarget").GetBoolean() && result.Warnings.Any(w => w.Contains("Wine", StringComparison.Ordinal)), "Wine dependency or untested status was omitted.");
    CheckSuite.Assert((zip.GetEntry("Synthetic & Game.app/Contents/MacOS/launch")!.ExternalAttributes >> 16 & 0x49) == 0x49, "Wrapper script lacks executable mode.");
    CheckSuite.Assert(FixtureBox.CentralHeaders(File.ReadAllBytes(result.OutputPath)).All(h => File.ReadAllBytes(result.OutputPath)[h + 5] == 3), "Mac archive does not declare Unix creator metadata.");
});
await suite.RunAsync("Wine wrapper rejects Windows target and fake executable", box =>
{
    string input = box.Zip("source.zip", new ZipItem("game.exe", FixtureBox.PortableExecutable()));
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(input, "", box.Path("out.zip"), TargetPlatform.Windows, "Synthetic")));
    string fake = box.Zip("fake.zip", ZipItem.Text("fake.exe", "synthetic text"));
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(fake, "", box.Path("out.zip"), TargetPlatform.MacOS, "Synthetic")));
});
await suite.RunAsync("Wine wrapper forbids overwrites and nested output", box =>
{
    box.Write("source/game.exe", FixtureBox.PortableExecutable());
    string output = box.Text("already.zip", "preserve me");
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(box.Path("source"), "", output, TargetPlatform.MacOS, "Synthetic")));
    CheckSuite.Assert(File.ReadAllText(output) == "preserve me", "Existing output was changed.");
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(box.Path("source/game.exe"), "", box.Path("source/nested.zip"), TargetPlatform.MacOS, "Synthetic")));
});
await suite.RunAsync("Wine nested EXE uses its directory and preserves relative game resources", box =>
{
    string input = box.Zip("nested-game.zip", new ZipItem("GameRoot/Game.exe", FixtureBox.PortableExecutable()),
        ZipItem.Text("GameRoot/assets/dialogue.txt", "synthetic dialogue resource"), ZipItem.Text("Readme.txt", "synthetic outer file"));
    PackageResult result = CompatibilityPackager.Package(new PackageRequest(input, "", box.Path("nested-wine.zip"), TargetPlatform.MacOS, "Synthetic"));
    using var zip = ZipFile.OpenRead(result.OutputPath);
    string script = Read(zip, "Synthetic.app/Contents/MacOS/launch");
    string expected = "cd -- \"$HERE/../Resources/game\"/'GameRoot'\nexec \"$WINE\" 'Game.exe' \"$@\"";
    CheckSuite.Assert(script.Contains(expected, StringComparison.Ordinal), "Nested game was launched from the archive root instead of beside its resources.");
    CheckSuite.Assert(Read(zip, "Synthetic.app/Contents/Resources/game/GameRoot/assets/dialogue.txt") == "synthetic dialogue resource", "Nested resources were omitted or moved away from the executable.");
    CheckSuite.Assert(Read(zip, "Synthetic.app/Contents/Resources/game/Readme.txt") == "synthetic outer file", "Source-root sibling file was omitted.");
});
await suite.RunAsync("Wine wrapper refuses arbitrary output extension without creating a file", box =>
{
    string input = box.Zip("source.zip", new ZipItem("Game.exe", FixtureBox.PortableExecutable()));
    string output = box.Path("output.exe");
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(input, "", output, TargetPlatform.MacOS, "Synthetic")));
    CheckSuite.Assert(!File.Exists(output) && !Directory.EnumerateFiles(box.Root, "*.partial-*", SearchOption.AllDirectories).Any(), "Invalid output extension left an artifact behind.");
});
await suite.RunAsync("Wine wrapper refuses existing directory output and preserves its contents", box =>
{
    string input = box.Zip("source.zip", new ZipItem("Game.exe", FixtureBox.PortableExecutable()));
    string output = box.Path("existing.zip");
    box.Text("existing.zip/keep.txt", "preserve directory contents");
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(input, "", output, TargetPlatform.MacOS, "Synthetic")));
    CheckSuite.Assert(Directory.Exists(output) && File.ReadAllText(Path.Combine(output, "keep.txt")) == "preserve directory contents", "Existing directory was altered.");
    CheckSuite.Assert(!Directory.EnumerateFiles(box.Root, "*.partial-*", SearchOption.AllDirectories).Any(), "Directory-output rejection left an artifact behind.");
});

#if !CORE_ONLY
int httpIndex = Array.IndexOf(args, "--ui-url");
if (args.Contains("--self-host", StringComparer.Ordinal)) await SelfHostedChecks.RunAsync(suite);
else if (httpIndex >= 0)
{
    if (httpIndex + 1 >= args.Length) throw new ArgumentException("--ui-url needs the complete URL including #token.");
    await HttpChecks.RunAsync(suite, args[httpIndex + 1]);
}
#endif
await suite.RunAsync("Unity Mono build: main EXE found beside its _Data folder, crash handler ignored", box =>
{
    byte[] versionHeader = Encoding.ASCII.GetBytes("\0\0\0\0\0\0\0\u0016\0\0\0\02021.3.16f1\0synthetic serialized file");
    string input = box.Zip("unity-mono.zip",
        new ZipItem("KONTUR/UnityCrashHandler64.exe", FixtureBox.PortableExecutable().Concat(new byte[4096]).ToArray()),
        new ZipItem("KONTUR/KONTUR.exe", FixtureBox.PortableExecutable()),
        new ZipItem("KONTUR/UnityPlayer.dll", FixtureBox.PortableExecutable(isDll: true)),
        new ZipItem("KONTUR/KONTUR_Data/globalgamemanagers", versionHeader),
        new ZipItem("KONTUR/KONTUR_Data/Managed/Assembly-CSharp.dll", FixtureBox.PortableExecutable(isDll: true)),
        new ZipItem("KONTUR/MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", FixtureBox.PortableExecutable(isDll: true)));
    GameExecutable? exe = GameExecutableFinder.Find(input);
    CheckSuite.Assert(exe?.Path == "KONTUR/KONTUR.exe" && exe.Reason.Contains("_Data", StringComparison.Ordinal), "Unity main executable was not chosen by its data folder: " + exe?.Path);
    CheckSuite.Assert(exe!.Skipped.Contains("KONTUR/UnityCrashHandler64.exe"), "Crash handler was not reported as skipped.");
    ConversionPlan plan = ConversionEngine.Inspect(input, TargetPlatform.MacOS);
    CheckSuite.Assert(plan.Method == "wine" && plan.CanConvert && plan.Engine == "Unity 2021.3.16f1", "Unity build was not planned with its version: " + plan.Engine);
    CheckSuite.Assert(plan.Detail.Contains("KONTUR/KONTUR.exe", StringComparison.Ordinal) && plan.Detail.Contains("Mono", StringComparison.Ordinal), "Plan does not name the chosen EXE and scripting backend.");
    CheckSuite.Assert(plan.Warnings.Any(w => w.Contains("DirectX", StringComparison.Ordinal)), "Plan does not explain why Unity is not runtime-swapped like Godot.");
    PackageResult result = CompatibilityPackager.Package(new PackageRequest(input, "", box.Path("unity-wine.zip"), TargetPlatform.MacOS, "KONTUR"));
    using var zip = ZipFile.OpenRead(result.OutputPath);
    string script = Read(zip, "KONTUR.app/Contents/MacOS/launch");
    CheckSuite.Assert(script.Contains("/'KONTUR'\nexec \"$WINE\" 'KONTUR.exe' \"$@\"", StringComparison.Ordinal), "Wine wrapper did not start the Unity game executable.");
});
await suite.RunAsync("Unity IL2CPP 32-bit build reports backend and WoW64 requirement", box =>
{
    byte[] x86 = FixtureBox.PortableExecutable();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(x86.AsSpan(0x84), 0x14c);
    string input = box.Zip("unity-il2cpp.zip",
        new ZipItem("Game.exe", x86), new ZipItem("UnityCrashHandler32.exe", x86),
        new ZipItem("GameAssembly.dll", FixtureBox.PortableExecutable(isDll: true)),
        new ZipItem("Game_Data/data.unity3d", Encoding.ASCII.GetBytes("UnityFS\0\0\0\0\u00085.x.x\02019.4.40f1\0bundle")),
        ZipItem.Text("Game_Data/il2cpp_data/Metadata/global-metadata.dat", "synthetic"));
    ConversionPlan plan = ConversionEngine.Inspect(input, TargetPlatform.MacOS);
    CheckSuite.Assert(plan.CanConvert && plan.Engine == "Unity 2019.4.40f1" && plan.Detail.Contains("IL2CPP", StringComparison.Ordinal) && plan.Detail.Contains("x86", StringComparison.Ordinal), "IL2CPP x86 facts missing: " + plan.Engine + " / " + plan.Detail);
    CheckSuite.Assert(plan.Warnings.Any(w => w.Contains("32-бит", StringComparison.Ordinal)), "32-bit Wine requirement was not explained.");
});
await suite.RunAsync("Godot console wrapper, Unreal shipping binary and redistributables are not chosen", box =>
{
    string godot = box.Zip("godot.zip", new ZipItem("Game.console.exe", FixtureBox.PortableExecutable().Concat(new byte[8192]).ToArray()),
        new ZipItem("Game.exe", FixtureBox.PortableExecutable()), ZipItem.Text("Game.pck", "synthetic pck"));
    CheckSuite.Assert(GameExecutableFinder.Find(godot)?.Path == "Game.exe", "Godot console wrapper was chosen.");
    string unreal = box.Zip("unreal.zip", new ZipItem("Hollow.exe", FixtureBox.PortableExecutable()),
        new ZipItem("Hollow/Binaries/Win64/Hollow-Win64-Shipping.exe", FixtureBox.PortableExecutable().Concat(new byte[8192]).ToArray()),
        new ZipItem("Engine/Extras/Redist/en-us/UE4PrereqSetup_x64.exe", FixtureBox.PortableExecutable().Concat(new byte[16384]).ToArray()));
    CheckSuite.Assert(GameExecutableFinder.Find(unreal)?.Path == "Hollow.exe", "Unreal bootstrap was not chosen: " + GameExecutableFinder.Find(unreal)?.Path);
    string redist = box.Zip("redist.zip", new ZipItem("_CommonRedist/vcredist/2019/VC_redist.x64.exe", FixtureBox.PortableExecutable().Concat(new byte[16384]).ToArray()),
        new ZipItem("NightRide.exe", FixtureBox.PortableExecutable()), new ZipItem("unins000.exe", FixtureBox.PortableExecutable()));
    CheckSuite.Assert(GameExecutableFinder.Find(redist)?.Path == "NightRide.exe", "Redistributable or uninstaller was chosen.");
});
await suite.RunAsync("A build with only helper executables is refused instead of guessed", box =>
{
    string input = box.Zip("helpers.zip", new ZipItem("UnityCrashHandler64.exe", FixtureBox.PortableExecutable()),
        new ZipItem("redist/vcredist_x64.exe", FixtureBox.PortableExecutable()));
    CheckSuite.Assert(GameExecutableFinder.Find(input) is null, "A helper executable was presented as the game.");
    ConversionPlan plan = ConversionEngine.Inspect(input, TargetPlatform.MacOS);
    CheckSuite.Assert(!plan.CanConvert, "A build without a game executable was offered for conversion.");
    CheckSuite.Reject(() => CompatibilityPackager.Package(new PackageRequest(input, "", box.Path("helpers-wine.zip"), TargetPlatform.MacOS, "Synthetic")));
});
return suite.Report();

static string Read(ZipArchive archive, string name)
{
    using var reader = new StreamReader(archive.GetEntry(name)?.Open() ?? throw new InvalidDataException("Missing artifact entry " + name), Encoding.UTF8);
    return reader.ReadToEnd();
}

static void WriteCacheLock(string path, string hash, bool verified = true, string? url = null, string? official = null)
{
    // Emulates a previously verified persisted catalog record to test local integrity.
    // This synthetic record is not evidence of authenticity or a real official download.
    string file = Path.GetFileName(path);
    File.WriteAllText(path + ".lock.json", JsonSerializer.Serialize(new
    {
        schemaVersion = 2, method = "nw", file, version = "0.92.0",
        url = url ?? RuntimeCatalog.RuntimeUrl("nw", "0.92.0", file),
        sha256 = hash, officialChecksum = official ?? hash, officialChecksumVerified = verified
    }));
}
