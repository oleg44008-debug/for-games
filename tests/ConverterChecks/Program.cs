using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DustoreX.AutoConverter;

namespace ConverterChecks;

internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = [];

    private static int Main()
    {
        Console.WriteLine("DustoreX AutoConverter regression checks — synthetic fixtures only.");
        Console.WriteLine("Synthetic PE/Mach-O fixtures verify packaging, never target execution.");

        Run("Pure .love -> Windows: payload bytes, manifest and source preserved", box =>
        {
            string input = box.Love();
            byte[] before = File.ReadAllBytes(input);
            PackageResult result = Package(box, input, box.WindowsRuntime(), TargetPlatform.Windows);
            Assert(File.ReadAllBytes(input).SequenceEqual(before), "Source .love was modified.");
            using ZipArchive zip = ZipFile.OpenRead(result.OutputPath);
            ZipArchiveEntry executable = Single(zip.Entries.Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)));
            byte[] fused = Read(executable);
            Assert(fused.AsSpan(512).SequenceEqual(before), "Windows fusion did not preserve .love bytes.");
            Assert(HashEquals(result.PayloadSha256, before), "Payload hash disagrees with source payload.");
            CheckManifest(zip, result);
        });

        Run("Pure .love -> macOS: payload, Unix execute bits and runtime symlink preserved", box =>
        {
            string input = box.Love();
            string runtime = box.MacRuntime(symlink: true);
            PackageResult result = Package(box, input, runtime, TargetPlatform.MacOS);
            using ZipArchive zip = ZipFile.OpenRead(result.OutputPath);
            ZipArchiveEntry game = Single(zip.Entries.Where(e => e.FullName.EndsWith(".love", StringComparison.OrdinalIgnoreCase)));
            Assert(Read(game).SequenceEqual(File.ReadAllBytes(input)), "Mac payload bytes changed.");
            ZipArchiveEntry executable = Single(zip.Entries.Where(e => e.FullName.Contains("/Contents/MacOS/", StringComparison.Ordinal)));
            Assert((executable.ExternalAttributes >> 16 & 0x1FF) == 0x1ED, "Unix mode 0755 was not preserved.");
            ZipArchiveEntry link = Single(zip.Entries.Where(e => (e.ExternalAttributes >> 16 & 0xF000) == 0xA000));
            Assert(Encoding.UTF8.GetString(Read(link)) == "Versions/A/Synthetic", "Framework symlink changed.");
            byte[] outputBytes = File.ReadAllBytes(result.OutputPath);
            foreach (int header in FixtureBox.CentralHeaders(outputBytes))
            {
                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(outputBytes.AsSpan(header + 28));
                string name = Encoding.UTF8.GetString(outputBytes, header + 46, nameLength);
                if (name.Contains("/Contents/MacOS/", StringComparison.Ordinal)
                    || name.EndsWith("/Synthetic.framework/Synthetic", StringComparison.Ordinal))
                    Assert(outputBytes[header + 5] == 3, "Mac ZIP entry lost Unix creator-host metadata: " + name);
            }
            CheckManifest(zip, result);
        });

        Run("Mac .app ZIP -> Windows: embedded .love survives byte for byte", box =>
        {
            string input = box.MacGame();
            byte[] expected;
            using (ZipArchive original = ZipFile.OpenRead(input))
                expected = Read(original.GetEntry("Game.app/Contents/Resources/game.love")!);
            PackageResult result = Package(box, input, box.WindowsRuntime(), TargetPlatform.Windows);
            Assert(HashEquals(result.PayloadSha256, expected), "Embedded Mac payload hash changed.");
            using ZipArchive zip = ZipFile.OpenRead(result.OutputPath);
            byte[] fused = Read(Single(zip.Entries.Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))));
            Assert(fused.AsSpan(512).SequenceEqual(expected), "Mac -> Windows fused payload differs.");
        });

        Run("Windows fused exe -> macOS: EOCD payload extracted without PE prefix", box =>
        {
            byte[] payload = FixtureBox.ZipBytes(ZipItem.Text("main.lua", "function love.draw() end\n"));
            string input = box.Write("fused-game.exe", FixtureBox.PortableExecutable().Concat(payload).ToArray());
            PackageResult result = Package(box, input, box.MacRuntime(), TargetPlatform.MacOS);
            using ZipArchive zip = ZipFile.OpenRead(result.OutputPath);
            byte[] actual = Read(Single(zip.Entries.Where(e => e.FullName.EndsWith(".love", StringComparison.OrdinalIgnoreCase))));
            Assert(actual.SequenceEqual(payload), "Fused exe prefix leaked into Mac payload.");
            Assert(HashEquals(result.PayloadSha256, payload), "Fused payload hash disagrees.");
        });

        Run("Loose LÖVE project directory -> Windows", box =>
        {
            box.Text("source/main.lua", "function love.draw() end\n");
            box.Text("source/assets/readme.txt", "Synthetic asset\n");
            PackageResult result = Package(box, box.Path("source"), box.WindowsRuntime(), TargetPlatform.Windows);
            using ZipArchive output = ZipFile.OpenRead(result.OutputPath);
            byte[] fused = Read(Single(output.Entries.Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))));
            using var payloadStream = new MemoryStream(fused.AsSpan(512).ToArray());
            using var payload = new ZipArchive(payloadStream, ZipArchiveMode.Read);
            Assert(payload.GetEntry("main.lua") != null && payload.GetEntry("assets/readme.txt") != null,
                "Loose project lost files.");
            Assert(File.Exists(box.Path("source/main.lua")), "Loose source modified.");
        });

        Run("Complete Windows runtime ZIP can be used without extraction", box =>
        {
            string input = box.Love();
            PackageResult result = Package(box, input, WindowsRuntimeZip(box), TargetPlatform.Windows);
            using ZipArchive output = ZipFile.OpenRead(result.OutputPath);
            byte[] fused = Read(Single(output.Entries.Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))));
            Assert(fused.AsSpan(512).SequenceEqual(File.ReadAllBytes(input)), "Windows ZIP runtime lost payload.");
        });

        Run("Mac bundle rename preserves executable and rewrites safe plist metadata", box =>
        {
            string input = box.Love();
            string runtime = box.MacRuntime();
            const string title = "Synthetic Game & Friends";
            PackageResult result = LovePackager.Package(new PackageRequest(input, runtime, box.Path("result.zip"), TargetPlatform.MacOS, title, "11.5"));
            using ZipArchive output = ZipFile.OpenRead(result.OutputPath);
            ZipArchiveEntry plist = Single(output.Entries.Where(e => e.FullName.EndsWith("/Contents/Info.plist", StringComparison.Ordinal)));
            using var stream = new MemoryStream(Read(plist));
            XDocument xml = XDocument.Load(stream);
            XElement dict = xml.Root!.Element("dict")!;
            string? Value(string key) => dict.Elements("key").FirstOrDefault(e => e.Value == key)?.ElementsAfterSelf().FirstOrDefault()?.Value;
            Assert(Value("CFBundleExecutable") == "love", "Bundle executable metadata was changed.");
            Assert(output.GetEntry(title + ".app/Contents/MacOS/love") != null, "Bundle executable was lost after root rename.");
            Assert(Value("CFBundleName") == title && Value("CFBundleDisplayName") == title, "XML escaping or bundle display name failed.");
            Assert(Value("CFBundleIdentifier")?.StartsWith("org.dustorex.game.", StringComparison.Ordinal) == true, "Bundle identity was not rewritten.");
            Assert(Value("CFBundleDocumentTypes") == null, "Bundled game still registers the generic .love association.");
            Assert(!output.Entries.Any(e => e.FullName.Contains("_CodeSignature", StringComparison.Ordinal)), "Stale signature resources retained.");
        });

        Run("Mac app resource native sidecar outside embedded payload cannot be silently lost", box =>
        {
            string input = box.Zip("mac-native-sidecar.zip",
                ZipItem.Text("Game.app/Contents/Info.plist", FixtureBox.Plist("love")),
                new ZipItem("Game.app/Contents/MacOS/love", FixtureBox.MachO(), FixtureBox.ExecutableFile),
                new ZipItem("Game.app/Contents/Resources/game.love",
                    FixtureBox.ZipBytes(ZipItem.Text("main.lua", "function love.draw() end\n"))),
                new ZipItem("Game.app/Contents/Resources/game_plugin.dylib", FixtureBox.MachO()));
            RejectPackage(box, input);
        });

        Run("Existing destination is never overwritten", box =>
        {
            string input = box.Love();
            string output = box.Text("result.zip", "sentinel-existing-output");
            string runtime = box.WindowsRuntime();
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows)));
            Assert(File.ReadAllText(output) == "sentinel-existing-output", "Existing destination overwritten.");
        });

        Run("Output inside source directory is rejected before creating files", box =>
        {
            box.Text("source/main.lua", "function love.draw() end\n");
            string output = box.Path("source/nested-output.zip");
            string runtime = box.WindowsRuntime();
            Reject(() => LovePackager.Package(Request(box.Path("source"), runtime, output, TargetPlatform.Windows)));
            Assert(!File.Exists(output), "Nested source output created.");
        });

        Run("Output inside runtime directory is rejected", box =>
        {
            string runtime = box.WindowsRuntime();
            string input = box.Love();
            string output = System.IO.Path.Combine(runtime, "nested-output.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows)));
            Assert(!File.Exists(output), "Nested runtime output created.");
        });

        Run("Input and output path equality is rejected", box =>
        {
            string input = box.Love();
            byte[] original = File.ReadAllBytes(input);
            string runtime = box.WindowsRuntime();
            Reject(() => LovePackager.Package(Request(input, runtime, input, TargetPlatform.Windows)));
            Assert(File.ReadAllBytes(input).SequenceEqual(original), "Equal-path request destroyed input.");
        });

        Run("Source traversal entry is rejected", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("../escape.txt", "bad")])));
        Run("Source absolute archive entry is rejected", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("/escape.txt", "bad")])));
        Run("Source slash/backslash normalized duplicate is rejected", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("data/a.txt", "a"), ZipItem.Text("data\\a.txt", "b")])));
        Run("Source case collision is rejected", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("data/A.txt", "a"), ZipItem.Text("data/a.txt", "b")])));
        Run("Source Unicode normalization collision is rejected", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("café.txt", "a"), ZipItem.Text("cafe\u0301.txt", "b")])));
        Run("Source ZIP symlink is rejected", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("payload-link", "main.lua", FixtureBox.SymbolicLink)])));

        foreach (string extension in new[] { ".dll", ".dylib", ".so", ".so.1", ".node" })
        {
            string ext = extension;
            Run("Native payload dependency " + ext + " blocks runtime repack", box => RejectPackage(box,
                box.Love(extra: [new ZipItem("native/plugin" + ext, [1, 2, 3])])));
        }

        Run("Lua ffi.load requires manual port", box => RejectPackage(box,
            box.Love(main: "local ffi = require('ffi')\nlocal n = ffi.load('plugin')\n")));
        Run("Lua package.loadlib requires manual port", box => RejectPackage(box,
            box.Love(main: "local n = package.loadlib('native/plugin.dll', 'start')\n")));

        Run("Native PE payload without extension is rejected", box => RejectPackage(box,
            box.Love(extra: [new ZipItem("plugins/native", FixtureBox.PortableExecutable())])));
        Run("Native Mach-O payload without extension is rejected", box => RejectPackage(box,
            box.Love(extra: [new ZipItem("plugins/native", FixtureBox.MachO())])));
        Run("Archive payload with a lone backslash path cannot silently retain nonportable names", box => RejectPackage(box,
            box.Love(extra: [ZipItem.Text("assets\\hero.txt", "synthetic asset")])));

        Run("Known game/runtime LÖVE version mismatch is rejected", box =>
        {
            string input = box.Love(extra: [ZipItem.Text("conf.lua", "function love.conf(t) t.version = '11.4' end\n")]);
            string runtime = box.WindowsRuntime();
            string output = box.Path("result.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows, "11.5")));
            Assert(!File.Exists(output), "Version mismatch produced output.");
        });

        Run("Missing runtime version produces a warning rather than a Tested claim", box =>
        {
            string input = box.Love();
            string runtime = box.WindowsRuntime();
            PackageResult result = LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.Windows, null));
            Assert(result.Warnings.Count > 0, "Unknown runtime version was accepted without warning.");
            using ZipArchive output = ZipFile.OpenRead(result.OutputPath);
            CheckManifest(output, result);
        });

        Run("Commented or unrelated config version is not inferred as LÖVE requirement", box =>
        {
            string input = box.Love(extra: [ZipItem.Text("conf.lua", "-- t.version = '11.4'\nlocal app = { version = '1.0' }\napp.version = '1.0'\nfunction love.conf(t) t.window.width = 800 end\n")]);
            PackageResult result = Package(box, input, box.WindowsRuntime(), TargetPlatform.Windows);
            Assert(result.Status == "Packaged", "Non-LÖVE version was treated as runtime requirement.");
        });

        Run("Missing main.lua is rejected", box => RejectPackage(box,
            box.Zip("empty.love", ZipItem.Text("readme.txt", "no Lua entrypoint"))));

        Run("Declared ZIP expansion above budget is rejected without allocation", box =>
        {
            byte[] zip = FixtureBox.ZipBytes(ZipItem.Text("main.lua", "function love.draw() end\n"));
            PatchFirstUncompressedLength(zip, checked((uint)(SafetyLimits.MaxUncompressedBytes + 1)));
            RejectPackage(box, box.Write("oversized.love", zip));
        });

        Run("ZIP entry count budget is enforced", box =>
        {
            var entries = new List<ZipItem> { ZipItem.Text("main.lua", "function love.draw() end\n") };
            for (int i = 0; i < SafetyLimits.MaxEntries; i++) entries.Add(ZipItem.Text($"data/{i}.txt", ""));
            RejectPackage(box, box.Zip("many.love", entries.ToArray()));
        });

        Run("Lua script scan budget is enforced", box =>
        {
            byte[] script = Encoding.UTF8.GetBytes("--" + new string('x', checked((int)SafetyLimits.MaxScriptBytes)));
            RejectPackage(box, box.Zip("huge-script.love", new ZipItem("main.lua", script)));
        });

        Run("Windows runtime missing SDL is rejected", box =>
        {
            string input = box.Love();
            string runtime = box.WindowsRuntime(omitSdl: true);
            string output = box.Path("result.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows)));
            Assert(!File.Exists(output), "Incomplete runtime produced output.");
        });

        Run("Mac runtime missing CFBundleExecutable target is rejected", box =>
        {
            string input = box.Love();
            string runtime = box.MacRuntime(executable: "missing");
            Reject(() => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.MacOS)));
        });

        Run("Mac runtime missing Unix executable mode is rejected", box =>
        {
            string input = box.Love();
            string runtime = box.Zip("mac-no-mode.zip",
                ZipItem.Text("love.app/Contents/Info.plist", FixtureBox.Plist("love")),
                new ZipItem("love.app/Contents/MacOS/love", FixtureBox.MachO()));
            Reject(() => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.MacOS)));
        });

        Run("Mac runtime already containing a game payload is rejected", box =>
        {
            string input = box.Love();
            string runtime = box.Zip("mac-used-runtime.zip",
                ZipItem.Text("love.app/Contents/Info.plist", FixtureBox.Plist("love")),
                new ZipItem("love.app/Contents/MacOS/love", FixtureBox.MachO(), FixtureBox.ExecutableFile),
                new ZipItem("love.app/Contents/Resources/old-game.love",
                    FixtureBox.ZipBytes(ZipItem.Text("main.lua", "function love.draw() end\n"))));
            Reject(() => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.MacOS)));
        });

        Run("Ordinary Windows exe without LÖVE payload is rejected", box => RejectPackage(box,
            box.Write("ordinary.exe", FixtureBox.PortableExecutable())));

        Run("Lua bytecode cannot be certified as portable Lua source", box => RejectPackage(box,
            box.Zip("bytecode.love", new ZipItem("main.lua", [0x1b, (byte)'L', (byte)'u', (byte)'a', 0x51, 0]))));

        Run("Mac runtime escaping framework symlink is rejected", box =>
        {
            string input = box.Love();
            string runtime = box.MacRuntime(symlink: true, symlinkTarget: "../../../../../../escape");
            Reject(() => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.MacOS)));
        });

        Run("Runtime archive traversal is rejected", box =>
        {
            string input = box.Love();
            string runtime = WindowsRuntimeZip(box, ZipItem.Text("../escape.txt", "bad"));
            Reject(() => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.Windows)));
        });

        Run("Runtime archive case collision is rejected", box =>
        {
            string input = box.Love();
            string runtime = WindowsRuntimeZip(box, ZipItem.Text("LICENSE.TXT", "duplicate"));
            Reject(() => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), TargetPlatform.Windows)));
        });

        Run("Generated Windows executable cannot collide with a runtime directory", box =>
        {
            string input = box.Love();
            string runtime = WindowsRuntimeZip(box,
                new ZipItem("Synthetic Game.exe/", [], unchecked((int)0x41ED0000)),
                ZipItem.Text("Synthetic Game.exe/nested.txt", "synthetic runtime directory"));
            string output = box.Path("result.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows)));
            Assert(!File.Exists(output), "Generated file/directory collision produced output.");
        });

        Run("Generated Mac game.love cannot collide with a runtime directory", box =>
        {
            string input = box.Love();
            string runtime = box.Zip("mac-love-dir-runtime.zip",
                ZipItem.Text("love.app/Contents/Info.plist", FixtureBox.Plist("love")),
                new ZipItem("love.app/Contents/MacOS/love", FixtureBox.MachO(), FixtureBox.ExecutableFile),
                new ZipItem("love.app/Contents/Resources/game.love/", [], unchecked((int)0x41ED0000)));
            string output = box.Path("result.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.MacOS)));
            Assert(!File.Exists(output), "Mac game.love directory collision produced output.");
        });

        Run("Lua version reassignment is marked unknown rather than inferred from first literal", box =>
        {
            string input = box.Love(extra: [ZipItem.Text("conf.lua",
                "function love.conf(t) t.version = '11.4'; t.version = os.getenv('LOVE_VERSION') end\n")]);
            PackageResult result = Package(box, input, box.WindowsRuntime(), TargetPlatform.Windows);
            AssertUnknownDeclaredVersion(result);
        });

        Run("Redefined love.conf leaves required runtime version unknown", box =>
        {
            string input = box.Love(extra: [ZipItem.Text("conf.lua",
                "function love.conf(t) t.version = '11.4' end\nfunction love.conf(t) t.window.width = 800 end\n")]);
            PackageResult result = Package(box, input, box.WindowsRuntime(), TargetPlatform.Windows);
            AssertUnknownDeclaredVersion(result);
        });

        Run("Conflicting symlink and directory ZIP metadata is rejected", box =>
        {
            string input = box.Love();
            string runtime = box.Zip("mac-symlink-dir-runtime.zip",
                ZipItem.Text("love.app/Contents/Info.plist", FixtureBox.Plist("love")),
                new ZipItem("love.app/Contents/MacOS/love", FixtureBox.MachO(), FixtureBox.ExecutableFile),
                ZipItem.Text("love.app/Contents/Frameworks/link/", "Versions/A", FixtureBox.SymbolicLink));
            string output = box.Path("result.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.MacOS)));
            Assert(!File.Exists(output), "Conflicting symlink/directory metadata produced output.");
        });

        Run("Ambiguous Mac game ZIP with two payloads is rejected", box => RejectPackage(box,
            box.MacGame(duplicatePayload: true)));

        Run("Invalid input leaves no finished or temporary output", box =>
        {
            string input = box.Love(extra: [ZipItem.Text("../bad", "bad")]);
            string runtime = box.WindowsRuntime();
            string outputDirectory = box.Path("outputs");
            Directory.CreateDirectory(outputDirectory);
            string output = System.IO.Path.Combine(outputDirectory, "result.zip");
            Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows)));
            Assert(!Directory.EnumerateFileSystemEntries(outputDirectory).Any(), "Failed operation left output artifacts.");
        });

        Run("Analyzer identifies portable LÖVE and separate target validation", box =>
        {
            AnalysisReport report = BuildAnalyzer.Analyze(box.Love(), TargetPlatform.MacOS);
            Assert(report.Engine == "LÖVE" && report.Route == "RuntimeRepackCandidate", "Analyzer missed portable LÖVE.");
            Assert(report.Warnings.Any(w => w.Contains("target", StringComparison.OrdinalIgnoreCase)), "Analyzer omitted target validation limits.");
        });

        Run("Analyzer blocks LÖVE archive with native dependencies", box =>
        {
            AnalysisReport report = BuildAnalyzer.Analyze(box.Love(extra: [new ZipItem("plugin.dll", FixtureBox.PortableExecutable(true))]), TargetPlatform.MacOS);
            Assert(report.Engine == "LÖVE" && report.Route == "Blocked", "Analyzer falsely permits native LÖVE game.");
        });

        Run("Analyzer recognizes Mac bundle rather than assuming Windows", box =>
        {
            AnalysisReport report = BuildAnalyzer.Analyze(box.MacGame(), TargetPlatform.Windows);
            Assert(report.DetectedPlatform == "macOS app bundle" && report.Engine == "LÖVE", "Analyzer missed macOS LÖVE bundle.");
        });

        Run("Analyzer never promises machine-code Windows-to-Mac conversion", box =>
        {
            AnalysisReport report = BuildAnalyzer.Analyze(box.Write("ordinary.exe", FixtureBox.PortableExecutable()), TargetPlatform.MacOS);
            Assert(report.DetectedPlatform.StartsWith("Windows PE", StringComparison.Ordinal) && report.Route == "UnsupportedWithoutSource", "Analyzer promises unsupported PE conversion.");
        });

        Console.WriteLine($"RESULT: {_passed} passed; {Failures.Count} failed.");
        foreach (string failure in Failures) Console.WriteLine("FAILURE: " + failure);
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Run(string name, Action<FixtureBox> body)
    {
        try
        {
            using var box = new FixtureBox();
            body(box);
            _passed++;
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            Failures.Add(name + " — " + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine("FAIL: " + name + " — " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static PackageRequest Request(string input, string runtime, string output,
        TargetPlatform target, string? version = "11.5") => new(input, runtime, output, target, "Synthetic Game", version);

    private static PackageResult Package(FixtureBox box, string input, string runtime, TargetPlatform target)
        => LovePackager.Package(Request(input, runtime, box.Path("result.zip"), target));

    private static void RejectPackage(FixtureBox box, string input)
    {
        string runtime = box.WindowsRuntime();
        string output = box.Path("result.zip");
        Reject(() => LovePackager.Package(Request(input, runtime, output, TargetPlatform.Windows)));
        Assert(!File.Exists(output), "Rejected input produced output.");
    }

    private static void Reject(Action body)
    {
        try { body(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { return; }
        throw new InvalidOperationException("Operation should have rejected the fixture.");
    }

    private static void CheckManifest(ZipArchive archive, PackageResult result)
    {
        ZipArchiveEntry entry = Single(archive.Entries.Where(e => e.FullName.EndsWith("package-manifest.json", StringComparison.Ordinal)));
        using JsonDocument json = JsonDocument.Parse(Read(entry));
        JsonElement root = json.RootElement;
        Assert(result.Status == "Packaged", "PackageResult misstates verification status.");
        Assert(root.GetProperty("status").GetString() == "Packaged", "Manifest status is not Packaged.");
        Assert(root.GetProperty("testedOnTarget").ValueKind == JsonValueKind.False, "Manifest claims target execution.");
        Assert(root.GetProperty("payloadSha256").GetString() == result.PayloadSha256, "Manifest payload hash differs.");
        Assert(result.SourceSha256.Length == 64 && result.RuntimeSha256.Length == 64, "Missing provenance hashes.");
    }

    private static void AssertUnknownDeclaredVersion(PackageResult result)
    {
        using ZipArchive output = ZipFile.OpenRead(result.OutputPath);
        ZipArchiveEntry manifest = Single(output.Entries.Where(e => e.FullName.EndsWith("package-manifest.json", StringComparison.Ordinal)));
        using JsonDocument json = JsonDocument.Parse(Read(manifest));
        Assert(json.RootElement.GetProperty("declaredLoveVersion").ValueKind == JsonValueKind.Null,
            "Ambiguous Lua configuration was treated as a verified runtime version.");
    }

    private static string WindowsRuntimeZip(FixtureBox box, params ZipItem[] extra)
    {
        return box.Zip("windows-runtime.zip", new[]
        {
            new ZipItem("love.exe", FixtureBox.PortableExecutable()),
            new ZipItem("love.dll", FixtureBox.PortableExecutable(isDll: true)),
            new ZipItem("lua51.dll", FixtureBox.PortableExecutable(isDll: true)),
            new ZipItem("SDL2.dll", FixtureBox.PortableExecutable(isDll: true)),
            ZipItem.Text("license.txt", "Synthetic runtime only.\n")
        }.Concat(extra).ToArray());
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using Stream source = entry.Open();
        using var target = new MemoryStream();
        source.CopyTo(target);
        return target.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool HashEquals(string hash, byte[] bytes) => string.Equals(hash, Sha(bytes), StringComparison.OrdinalIgnoreCase);

    private static T Single<T>(IEnumerable<T> items)
    {
        T[] values = items.ToArray();
        Assert(values.Length == 1, $"Expected one result, got {values.Length}.");
        return values[0];
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void PatchFirstUncompressedLength(byte[] zip, uint length)
    {
        Assert(BinaryPrimitives.ReadUInt32LittleEndian(zip) == 0x04034b50, "Fixture missing local ZIP header.");
        BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(22), length);
        for (int i = 30; i + 46 < zip.Length; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) != 0x02014b50) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(zip.AsSpan(i + 24), length);
            return;
        }
        throw new InvalidOperationException("Fixture missing central ZIP header.");
    }
}
