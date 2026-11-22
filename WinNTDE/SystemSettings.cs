using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinNTDE
{
    internal static class SystemSettings
    {
        internal static readonly Version WindowsVersion = Environment.OSVersion.Version;
        private const int DmOrientation = 0x80, DmWidth = 0x80000, DmHeight = 0x100000;
        private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        private const string ExplorerExecutable = "explorer.exe";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DisplayMode
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] private string deviceName;
            private ushort specVersion, driverVersion;
            internal ushort Size, DriverExtra;
            internal int Fields;
            private int x, y;
            internal int Orientation;
            private int fixedOutput;
            private short color, duplex, yResolution, ttOption, collate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] private string formName;
            private short logPixels;
            internal int Bits, Width, Height;
            private int displayFlags;
            internal int Frequency;
            private int icmMethod, icmIntent, mediaType, ditherType, reserved1, reserved2, panningWidth, panningHeight;
        }

        internal sealed class Resolution
        {
            internal int Width, Height;
            public override string ToString() { return Width + " × " + Height; }
        }

        internal sealed class PowerPlan
        {
            internal Guid Id;
            internal string Name;
            internal bool Active;
            public override string ToString() { return Name; }
        }

        internal sealed class InstalledApp
        {
            public string Name { get; set; }
            public string Version { get; set; }
            public string Publisher { get; set; }
            internal string Command { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PowerStatus
        {
            internal byte Ac, Battery, Percent, Reserved;
            internal int TimeLeft, FullTime;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string device, int index, ref DisplayMode mode);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string device, ref DisplayMode mode, IntPtr window, uint flags, IntPtr parameters);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
        [DllImport("powrprof.dll")] private static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subgroup,
            uint access, uint index, out Guid value, ref uint size);
        [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, ref Guid scheme,
            IntPtr subgroup, IntPtr powerSetting, byte[] buffer, ref uint size);
        [DllImport("powrprof.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
            [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool disableWake);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

        private static DisplayMode NewMode()
        {
            return new DisplayMode { Size = (ushort)Marshal.SizeOf(typeof(DisplayMode)) };
        }

        internal static DisplayMode CurrentMode()
        {
            var mode = NewMode();
            if (!EnumDisplaySettings(null, -1, ref mode)) throw new InvalidOperationException("Windows could not read the primary display mode.");
            return mode;
        }

        internal static Resolution[] Resolutions(DisplayMode current)
        {
            var sizes = new HashSet<Tuple<int, int>>();
            sizes.Add(ToLandscape(current));
            var mode = NewMode();
            for (int index = 0; EnumDisplaySettings(null, index, ref mode); index++)
            {
                if (mode.Bits >= 24) sizes.Add(ToLandscape(mode));
                mode = NewMode();
            }
            return sizes.OrderByDescending(size => size.Item1).ThenByDescending(size => size.Item2)
                .Select(size => new Resolution { Width = size.Item1, Height = size.Item2 }).ToArray();
        }

        internal static Tuple<int, int> ToLandscape(DisplayMode mode)
        {
            return mode.Orientation % 2 == 0 ? Tuple.Create(mode.Width, mode.Height) : Tuple.Create(mode.Height, mode.Width);
        }

        internal static DisplayMode RequestedMode(DisplayMode current, Resolution resolution, int orientation)
        {
            int width = orientation % 2 == 0 ? resolution.Width : resolution.Height;
            int height = orientation % 2 == 0 ? resolution.Height : resolution.Width;
            if (current.Width == width && current.Height == height && current.Orientation == orientation) return current;

            current.Width = width;
            current.Height = height;
            current.Orientation = orientation;
            // Let the driver select a supported refresh rate when changing resolution.
            current.Fields = DmWidth | DmHeight | DmOrientation;
            return current;
        }

        internal static void ChangeDisplay(DisplayMode mode, uint flags)
        {
            int result = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            if (result != 0) throw new InvalidOperationException("Windows could not apply this display mode (result " + result + ").");
        }

        internal static string PowerSummary()
        {
            PowerStatus status;
            if (!GetSystemPowerStatus(out status)) return "Power status unavailable.";
            if (status.Battery == 128) return "No battery detected.";
            string life = status.Percent <= 100 ? status.Percent + "% battery" : "Battery level unknown";
            return status.Ac == 1 ? life + " · Plugged in" : status.Ac == 0 ? life + " · On battery" : life;
        }

        internal static PowerPlan[] PowerPlans()
        {
            var plans = new List<PowerPlan>();
            if (WindowsVersion.Major < 6) return plans.ToArray();
            IntPtr pointer;
            Check(PowerGetActiveScheme(IntPtr.Zero, out pointer));
            Guid active;
            try { active = (Guid)Marshal.PtrToStructure(pointer, typeof(Guid)); }
            finally { LocalFree(pointer); }

            for (uint index = 0; ; index++)
            {
                Guid id;
                uint size = 16;
                uint result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 16, index, out id, ref size);
                if (result == 259) break;
                Check(result);
                size = 0;
                PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
                string name = id.ToString();
                if (size > 0)
                {
                    var bytes = new byte[size];
                    if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, bytes, ref size) == 0)
                        name = System.Text.Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                }
                plans.Add(new PowerPlan { Id = id, Name = name, Active = id == active });
            }
            return plans.ToArray();
        }

        internal static void SetPowerPlan(Guid id) { Check(PowerSetActiveScheme(IntPtr.Zero, ref id)); }

        internal static void Sleep()
        {
            if (!SetSuspendState(false, false, false)) throw new Win32Exception();
        }

        internal static void EndSession(string option)
        {
            string arguments = WindowsVersion.Major >= 6 ? "/" + option : "-" + option;
            if (option != "l") arguments += WindowsVersion.Major >= 6 ? " /t 0" : " -t 0";
            Process.Start(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments);
        }

        internal static InstalledApp[] InstalledApps()
        {
            const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            var apps = new List<InstalledApp>();
            var views = Environment.Is64BitOperatingSystem ? new[] { RegistryView.Registry64, RegistryView.Registry32 } :
                new[] { RegistryView.Default };
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (RegistryView view in views)
            {
                using (var root = RegistryKey.OpenBaseKey(hive, view))
                using (var key = ReadableSubKey(root, uninstall))
                {
                    if (key == null) continue;
                    foreach (string id in key.GetSubKeyNames())
                    using (var entry = ReadableSubKey(key, id))
                    {
                        if (entry == null || Equals(entry.GetValue("SystemComponent"), 1) || entry.GetValue("ParentKeyName") != null) continue;
                        string name = entry.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        string command = entry.GetValue("UninstallString") as string;
                        Guid msi;
                        if (Equals(entry.GetValue("WindowsInstaller"), 1) && Guid.TryParse(id, out msi))
                            command = "msiexec.exe /x " + msi.ToString("B");
                        if (Equals(entry.GetValue("NoRemove"), 1)) command = null;
                        apps.Add(new InstalledApp { Name = name, Version = entry.GetValue("DisplayVersion") as string,
                            Publisher = entry.GetValue("Publisher") as string, Command = command });
                    }
                }
            }
            return apps.GroupBy(item => Tuple.Create(item.Name, item.Version, item.Command)).Select(group => group.First())
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private static RegistryKey ReadableSubKey(RegistryKey root, string path)
        {
            try { return root.OpenSubKey(path); }
            catch (System.Security.SecurityException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static Process LaunchCommand(string command)
        {
            string value = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
            if (value.Length == 0) throw new ArgumentException("Enter a program, file or folder.");
            if (File.Exists(value) || Directory.Exists(value) || Regex.IsMatch(value, "^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase))
                return Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });

            string program;
            int end;
            if (value[0] == '"')
            {
                end = value.IndexOf('"', 1);
                if (end < 0) throw new ArgumentException("The program path is missing its closing quotation mark.");
                program = value.Substring(1, end - 1);
                end++;
            }
            else
            {
                var executable = Regex.Match(value, @"^.+?\.(exe|com|bat|cmd)(?=\s|$)", RegexOptions.IgnoreCase);
                int firstSpace = value.IndexOfAny(new[] { ' ', '\t' });
                end = firstSpace < 0 ? value.Length : firstSpace;
                if (executable.Success && (executable.Length <= end || File.Exists(executable.Value))) end = executable.Length;
                program = value.Substring(0, end);
            }
            return Process.Start(new ProcessStartInfo(program, value.Substring(end).TrimStart()) { UseShellExecute = true });
        }

        internal static string SystemSummary()
        {
            string summary = Environment.OSVersion.VersionString + "   ·   " + (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit");
            using (var query = new ManagementObjectSearcher("SELECT Caption, Version, TotalVisibleMemorySize FROM Win32_OperatingSystem"))
            using (var results = query.Get())
            {
                foreach (ManagementObject result in results)
                using (result)
                {
                    double ramMiB = Convert.ToDouble(result["TotalVisibleMemorySize"]) / 1024;
                    return result["Caption"] + "   ·   " + result["Version"] + "\n" + Environment.MachineName + "   ·   " +
                        (Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit") + "   ·   " +
                        Environment.ProcessorCount + " logical processors   ·   " + ramMiB.ToString("N0") + " MB RAM";
                }
            }
            return summary;
        }

        internal static void OpenControl(string panel)
        {
            Process.Start(Path.Combine(Environment.SystemDirectory, "control.exe"), panel);
        }

        internal static void OpenManagementConsole(string snapIn)
        {
            string console = Path.Combine(Environment.SystemDirectory, "mmc.exe");
            string path = Path.Combine(Environment.SystemDirectory, snapIn);
            using (Process.Start(new ProcessStartInfo(console, "\"" + path + "\"") { UseShellExecute = true })) { }
        }

        internal static void OpenTerminal(bool elevated)
        {
            const int Cancelled = 1223;
            try
            {
                if (WindowsVersion.Major >= 10)
                {
                    var terminal = new FileInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        @"Microsoft\WindowsApps\wt.exe"));
                    if (terminal.Exists && (terminal.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        try { StartTerminal(terminal.FullName, elevated); return; }
                        catch (Win32Exception error) when (error.NativeErrorCode != Cancelled) { }
                    }
                }
                StartTerminal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), elevated);
            }
            catch (Win32Exception error) when (error.NativeErrorCode == Cancelled) { }
        }

        internal static bool IsExplorerRunning()
        {
            int sessionId;
            using (var current = Process.GetCurrentProcess()) sessionId = current.SessionId;
            var explorers = Process.GetProcessesByName("explorer");
            try { return explorers.Any(process => process.SessionId == sessionId); }
            finally { foreach (var process in explorers) process.Dispose(); }
        }

        internal static ProcessStartInfo PrepareExplorerShell()
        {
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string executable = Path.Combine(windows, ExplorerExecutable);
            if (string.IsNullOrEmpty(windows) || !File.Exists(executable))
                throw new FileNotFoundException("Windows Explorer could not be found.", executable);

            RestoreExplorerShell();
            return new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = windows };
        }

        private static void RestoreExplorerShell()
        {
            object userShell = ReadWinlogonShell(Registry.CurrentUser);
            bool machineUsesWinNTDE;
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            {
                string machineShell = ReadWinlogonShell(machine) as string;
                machineUsesWinNTDE = IsWinNTDEShell(machineShell);
                if ((machineUsesWinNTDE || userShell == null) &&
                    !string.Equals(machineShell, ExplorerExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    try { WriteExplorerShell(machine); }
                    catch (Exception error) when (error is UnauthorizedAccessException || error is SecurityException)
                    {
                        RestoreMachineShellElevated();
                    }
                }
            }

            if (userShell != null && (!machineUsesWinNTDE || IsWinNTDEShell(userShell as string)) &&
                !string.Equals(userShell as string, ExplorerExecutable, StringComparison.OrdinalIgnoreCase))
                WriteExplorerShell(Registry.CurrentUser);
        }

        private static bool IsWinNTDEShell(string command)
        {
            if (command == null) return false;
            string executable = command.Trim().Trim('"');
            return string.Equals(executable, "WinNTDE.exe", StringComparison.OrdinalIgnoreCase) ||
                executable.EndsWith(@"\WinNTDE.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static object ReadWinlogonShell(RegistryKey root)
        {
            using (var key = root.OpenSubKey(WinlogonKey))
                return key == null ? null : key.GetValue("Shell", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static void WriteExplorerShell(RegistryKey root)
        {
            using (var key = root.OpenSubKey(WinlogonKey, true))
            {
                if (key == null) throw new InvalidOperationException("The Winlogon registry key could not be opened.");
                key.SetValue("Shell", ExplorerExecutable, RegistryValueKind.String);
            }
        }

        private static void RestoreMachineShellElevated()
        {
            string arguments = "add \"HKLM\\" + WinlogonKey + "\" /v Shell /t REG_SZ /d " + ExplorerExecutable + " /f";
            if (Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess) arguments += " /reg:64";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "reg.exe"), arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("The registry update could not be started.");
                process.WaitForExit();
                if (process.ExitCode != 0) throw new InvalidOperationException("The Windows Explorer shell setting could not be restored.");
            }
        }

        private static void StartTerminal(string executable, bool elevated)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = elevated ? "runas" : "open",
                WorkingDirectory = elevated ? Environment.SystemDirectory : DesktopFiles.Home };
            using (Process.Start(start)) { }
        }

        internal static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        internal static void Check(uint error)
        {
            if (error != 0) throw new Win32Exception(unchecked((int)error));
        }
    }
}
