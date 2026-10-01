using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ConverterChecks;

// Every runtime/executable in this harness is synthetic data for package tests.
// None is runnable, downloaded, installed, or taken from a user's game library.
internal sealed class FixtureBox : IDisposable
{
    internal const int RegularFile = unchecked((int)0x81A40000); // Unix regular 0644.
    internal const int ExecutableFile = unchecked((int)0x81ED0000); // Unix regular 0755.
    internal const int SymbolicLink = unchecked((int)0xA1FF0000); // Unix link 0777.
    internal string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "DustoreX-synthetic-checks-" + Guid.NewGuid().ToString("N"));

    internal FixtureBox() => Directory.CreateDirectory(Root);
    internal string Path(string relative) => System.IO.Path.Combine(Root, relative);

    internal string Write(string relative, byte[] bytes)
    {
        string path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    internal string Text(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    internal string Zip(string relative, params ZipItem[] files) => Write(relative, ZipBytes(files));

    internal string Love(string relative = "game.love", string main = "function love.draw() end\n",
        params ZipItem[] extra)
    {
        return Zip(relative, new[] { ZipItem.Text("main.lua", main) }.Concat(extra).ToArray());
    }

    internal string WindowsRuntime(string relative = "windows-runtime", bool omitSdl = false)
    {
        Text(relative + "/license.txt", "Synthetic runtime fixture only; no license granted.\n");
        Write(relative + "/love.exe", PortableExecutable());
        Write(relative + "/love.dll", PortableExecutable(isDll: true));
        Write(relative + "/lua51.dll", PortableExecutable(isDll: true));
        if (!omitSdl) Write(relative + "/SDL2.dll", PortableExecutable(isDll: true));
        return Path(relative);
    }

    internal string MacRuntime(string relative = "mac-runtime.zip", bool symlink = false,
        string executable = "love", string symlinkTarget = "Versions/A/Synthetic")
    {
        var files = new List<ZipItem>
        {
            ZipItem.Text("love.app/Contents/Info.plist", Plist(executable)),
            new("love.app/Contents/MacOS/love", MachO(), ExecutableFile),
            ZipItem.Text("love.app/Contents/Resources/license.txt", "Synthetic runtime only.\n"),
            ZipItem.Text("love.app/Contents/_CodeSignature/CodeResources", "Synthetic stale signature resources only.\n")
        };
        if (symlink)
        {
            files.Add(ZipItem.Text("love.app/Contents/Frameworks/Synthetic.framework/Versions/A/Synthetic", "synthetic framework"));
            files.Add(ZipItem.Text("love.app/Contents/Frameworks/Synthetic.framework/Synthetic", symlinkTarget, SymbolicLink));
        }
        return Zip(relative, files.ToArray());
    }

    internal string MacGame(string relative = "mac-game.zip", bool duplicatePayload = false)
    {
        var files = new List<ZipItem>
        {
            ZipItem.Text("Game.app/Contents/Info.plist", Plist("love")),
            new("Game.app/Contents/MacOS/love", MachO(), ExecutableFile),
            new("Game.app/Contents/Resources/game.love", ZipBytes(ZipItem.Text("main.lua", "function love.draw() end\n")), RegularFile)
        };
        if (duplicatePayload) files.Add(new("Other.app/Contents/Resources/game.love",
            ZipBytes(ZipItem.Text("main.lua", "function love.draw() end\n")), RegularFile));
        return Zip(relative, files.ToArray());
    }

    internal static string Plist(string executable) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict>" +
        "<key>CFBundleExecutable</key><string>" + executable + "</string>" +
        "<key>CFBundleIdentifier</key><string>org.example.synthetic-love</string>" +
        "<key>CFBundleName</key><string>Synthetic LÖVE</string>" +
        "<key>CFBundleShortVersionString</key><string>11.5</string>" +
        "<key>CFBundleDocumentTypes</key><array><dict><key>CFBundleTypeName</key><string>LÖVE file</string></dict></array>" +
        "</dict></plist>";

    internal static byte[] PortableExecutable(bool isDll = false)
    {
        byte[] result = new byte[512];
        result[0] = (byte)'M'; result[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0x3c), 0x80);
        result[0x80] = (byte)'P'; result[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x94), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x96), (ushort)(isDll ? 0x2022 : 0x0022));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x98), 0x020b);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x98 + 32), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x98 + 36), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x98 + 56), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x98 + 60), 512);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x98 + 68), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x98 + 108), 16);
        return result;
    }

    internal static byte[] MachO()
    {
        byte[] result = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 0x01000007);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), 2);
        return result;
    }

    internal static byte[] ZipBytes(params ZipItem[] files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipItem file in files)
            {
                ZipArchiveEntry entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
                entry.ExternalAttributes = file.Attributes;
                using Stream target = entry.Open();
                target.Write(file.Bytes);
            }
        }
        byte[] bytes = stream.ToArray();
        foreach (int header in CentralHeaders(bytes)) bytes[header + 5] = 3; // Unix creator host.
        return bytes;
    }

    internal static IEnumerable<int> CentralHeaders(byte[] zip)
    {
        int end = -1;
        for (int i = zip.Length - 22; i >= Math.Max(0, zip.Length - 65557); i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i)) == 0x06054b50)
            {
                end = i;
                break;
            }
        }
        if (end < 0) throw new InvalidDataException("Synthetic ZIP has no EOCD.");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        int cursor = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16)));
        for (int i = 0; i < count; i++)
        {
            if (cursor < 0 || cursor + 46 > zip.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(cursor)) != 0x02014b50)
                throw new InvalidDataException("Synthetic ZIP central directory is malformed.");
            yield return cursor;
            cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cursor + 28))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cursor + 30))
                + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(cursor + 32));
        }
    }

    public void Dispose()
    {
        // Root is a directly created, unique fixture directory; never a user-supplied path.
        string full = System.IO.Path.GetFullPath(Root);
        string expected = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd('\\', '/')
            + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase)
            || !System.IO.Path.GetFileName(full).StartsWith("DustoreX-synthetic-checks-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refused fixture cleanup outside temporary fixture root.");
        Directory.Delete(full, recursive: true);
    }
}

internal sealed record ZipItem(string Name, byte[] Bytes, int Attributes = FixtureBox.RegularFile)
{
    internal static ZipItem Text(string name, string text, int attributes = FixtureBox.RegularFile)
        => new(name, Encoding.UTF8.GetBytes(text), attributes);
}
