using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DustoreX
{
    /// <summary>
    /// Prime ULTRA on Windows: more frames without touching the game's graphics settings.
    ///
    /// Nothing here lowers the picture. Each step removes something that stands between the game
    /// and the hardware:
    ///  • the game runs on the high-performance GPU (on laptops with two GPUs Windows otherwise
    ///    often picks the integrated one — the largest single gain there is);
    ///  • the «High performance» power plan (or the «Best performance» overlay) while it runs;
    ///  • Windows power throttling (EcoQoS) is switched off for the game's process;
    ///  • high CPU priority and a 1 ms system timer for steady frame pacing;
    ///  • the launcher minimizes, stops drawing and returns its memory.
    /// Everything is put back when the game exits, and on the next start if the PC crashed mid-game.
    /// </summary>
    internal static class Ultra
    {
        private const string HighPerformancePlan = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        private static readonly Guid BestPerformanceOverlay = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
        [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState info, int size);
        [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

        private static int _running;
        private static string RestoreFile { get { return Path.Combine(AppPaths.GetDataFolder(), "ultra-restore.txt"); } }

        /// <summary>Before the game starts: the GPU choice is read by Windows when the process is created.</summary>
        internal static void Prepare(string exePath)
        {
            if (!Edition.IsPrime || string.IsNullOrEmpty(exePath)) return;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences"))
                    if (!(key.GetValue(exePath) is string existing) || !existing.Contains("GpuPreference=2"))
                        key.SetValue(exePath, "GpuPreference=2;");
            }
            catch (Exception) { }
        }

        internal static void Apply(Process game)
        {
            if (!Edition.IsPrime || game == null) return;
            try { game.PriorityClass = ProcessPriorityClass.High; } catch (Exception) { }
            try { game.PriorityBoostEnabled = true; } catch (Exception) { }
            try
            {
                // PROCESS_POWER_THROTTLING_EXECUTION_SPEED | IGNORE_TIMER_RESOLUTION, state 0 = never throttle.
                var state = new PowerThrottlingState { Version = 1, ControlMask = 0x1 | 0x4, StateMask = 0 };
                SetProcessInformation(game.Handle, 4, ref state, Marshal.SizeOf(typeof(PowerThrottlingState)));
            }
            catch (Exception) { }
            timeBeginPeriod(1);
            if (System.Threading.Interlocked.Increment(ref _running) == 1) EnterPowerMode();

            System.Windows.Forms.Form main = System.Windows.Forms.Application.OpenForms.Count > 0 ? System.Windows.Forms.Application.OpenForms[0] : null;
            if (main != null && !main.IsDisposed) main.WindowState = System.Windows.Forms.FormWindowState.Minimized;
            try { SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1)); } catch (Exception) { }

            Task.Run(() =>
            {
                try { game.WaitForExit(); } catch (Exception) { }
                timeEndPeriod(1);
                if (System.Threading.Interlocked.Decrement(ref _running) == 0) LeavePowerMode();
                try
                {
                    if (main != null && !main.IsDisposed)
                        main.BeginInvoke((Action)(() => { if (main.WindowState == System.Windows.Forms.FormWindowState.Minimized) main.WindowState = System.Windows.Forms.FormWindowState.Normal; }));
                }
                catch (Exception) { }
            });
        }

        /// <summary>--ultra-selftest: runs Notepad as the game and records what ULTRA changed and restored.</summary>
        internal static int SelfTest(string report)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");
            string planBefore = ActivePlan();
            Prepare(exe);
            string gpu;
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences")) gpu = key?.GetValue(exe) as string ?? "";
            Process game = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            System.Threading.Thread.Sleep(1500);
            Apply(game);
            System.Threading.Thread.Sleep(1500);
            game.Refresh();
            string priority = game.PriorityClass.ToString();
            string planDuring = ActivePlan();
            Guid overlay; PowerGetEffectiveOverlayScheme(out overlay);
            game.Kill(); game.WaitForExit();
            System.Threading.Thread.Sleep(2500);
            string planAfter = ActivePlan();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true)) key?.DeleteValue(exe, false);
            bool ok = gpu.Contains("GpuPreference=2") && priority == "High" && planAfter == planBefore && !File.Exists(RestoreFile)
                && (planDuring == HighPerformancePlan || overlay == BestPerformanceOverlay);
            File.WriteAllText(report, "gpuPreference=" + gpu + "\npriority=" + priority + "\nplanBefore=" + planBefore + "\nplanDuring=" + planDuring
                + "\noverlayDuring=" + overlay + "\nplanAfter=" + planAfter + "\nrestored=" + (planAfter == planBefore) + "\npass=" + ok + "\n");
            return ok ? 0 : 1;
        }

        /// <summary>Called at launcher start: a game that ended with a crash or power cut leaves the plan to restore.</summary>
        internal static void RestoreAfterCrash()
        {
            if (File.Exists(RestoreFile)) LeavePowerMode();
        }

        private static void EnterPowerMode()
        {
            try
            {
                string plan = ActivePlan();
                Guid overlay;
                string overlayText = PowerGetEffectiveOverlayScheme(out overlay) == 0 ? overlay.ToString() : "";
                File.WriteAllText(RestoreFile, plan + "|" + overlayText);
                if (!string.Equals(plan, HighPerformancePlan, StringComparison.OrdinalIgnoreCase) && !Powercfg("/setactive " + HighPerformancePlan))
                    PowerSetActiveOverlayScheme(BestPerformanceOverlay);   // modern-standby laptops have no High performance plan
            }
            catch (Exception) { }
        }

        private static void LeavePowerMode()
        {
            try
            {
                if (!File.Exists(RestoreFile)) return;
                string[] saved = File.ReadAllText(RestoreFile).Split('|');
                if (saved.Length > 0 && Guid.TryParse(saved[0], out _)) Powercfg("/setactive " + saved[0]);
                if (saved.Length > 1 && Guid.TryParse(saved[1], out Guid overlay)) PowerSetActiveOverlayScheme(overlay);
                File.Delete(RestoreFile);
            }
            catch (Exception) { }
        }

        private static string ActivePlan()
        {
            var start = new ProcessStartInfo("powercfg", "/getactivescheme") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using (Process p = Process.Start(start))
            {
                string text = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                Match m = Regex.Match(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
                return m.Success ? m.Value : "";
            }
        }

        private static bool Powercfg(string arguments)
        {
            var start = new ProcessStartInfo("powercfg", arguments) { UseShellExecute = false, CreateNoWindow = true };
            using (Process p = Process.Start(start)) { p.WaitForExit(5000); return p.HasExited && p.ExitCode == 0; }
        }
    }
}
