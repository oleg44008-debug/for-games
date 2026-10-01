using System.Runtime.InteropServices;

namespace DustoreLauncherV.Mac.Services;

/// <summary>New imported copies retain the downloaded archive's quarantine marker.</summary>
internal static class MacQuarantine
{
    private const string Attribute = "com.apple.quarantine";
    private const int NoAttribute = 93; // Darwin ENOATTR.
    private const int NotSupported = 45; // Darwin ENOTSUP (e.g. a source volume without xattrs).

    public static void Preserve(string sourceArchive, string importedApp)
    {
        if (!OperatingSystem.IsMacOS()) return;
        byte[]? value = Read(sourceArchive);
        if (value is not null) Write(importedApp, value);
    }

    internal static byte[]? Read(string path)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        nint size = GetXattr(path, Attribute, null, 0, 0, 0);
        if (size < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is NoAttribute or NotSupported) return null;
            throw new IOException("Не удалось прочитать атрибут quarantine исходного пакета. errno=" + error);
        }
        if (size > 64 * 1024) throw new InvalidDataException("Атрибут quarantine исходного пакета слишком велик.");
        byte[] value = new byte[(int)size];
        nint actual = GetXattr(path, Attribute, value, (nuint)value.Length, 0, 0);
        if (actual < 0) throw new IOException("Не удалось прочитать атрибут quarantine исходного пакета. errno=" + Marshal.GetLastPInvokeError());
        if (actual != size) throw new IOException("Атрибут quarantine исходного пакета изменился во время импорта.");
        return value;
    }

    internal static void Write(string path, byte[] value)
    {
        if (!OperatingSystem.IsMacOS()) return;
        // No removal or clearing: copies keep the same marker, so normal macOS checks still apply.
        if (SetXattr(path, Attribute, value, (nuint)value.Length, 0, 0) != 0)
            throw new IOException("Не удалось сохранить quarantine у новой копии приложения. errno=" + Marshal.GetLastPInvokeError());
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "getxattr", SetLastError = true)]
    private static extern nint GetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[]? value, nuint size, uint position, int options);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "setxattr", SetLastError = true)]
    private static extern int SetXattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, byte[] value, nuint size, uint position, int options);
}
