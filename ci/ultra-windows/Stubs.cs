// The real Ultra.cs from DUSTORE LAUNCHER V, tested on a GitHub Windows runner with minimal stand-ins
// for the two launcher types it touches.
namespace DustoreX
{
    internal static class Edition { internal static readonly bool IsPrime = true; }
    internal static class AppPaths
    {
        internal static string GetDataFolder()
        {
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dustore-ultra-test");
            System.IO.Directory.CreateDirectory(folder);
            return folder;
        }
    }
    internal static class Program
    {
        private static int Main(string[] args) => Ultra.SelfTest(args.Length > 0 ? args[0] : "ultra-selftest.txt");
    }
}
