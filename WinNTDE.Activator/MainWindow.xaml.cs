using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace WinNTDE.Activator
{
    public partial class MainWindow : Window
    {
        private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        private const string ShellValue = "Shell";
        private const string MissingShell = "Place WinNTDE.Activator.exe in the same folder as WinNTDE.exe.";
        private readonly string shellPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WinNTDE.exe");

        public MainWindow()
        {
            InitializeComponent();
            ShellPath.Text = shellPath;
            if (!File.Exists(shellPath))
            {
                ActivateButton.IsEnabled = false;
                ShowError(MissingShell);
            }
        }

        private void ActivateClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                ActivateShell(shellPath, RestartWindows);
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error is SecurityException)
            {
                ShowError("Administrator access is required to change the Windows shell.");
                return;
            }
            catch (Exception error) when (error is IOException || error is Win32Exception || error is InvalidOperationException)
            {
                ShowError(error.Message);
                return;
            }
            Close();
        }

        private void CancelClicked(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ShowError(string message)
        {
            Status.Text = message;
            Status.Visibility = Visibility.Visible;
        }

        private static void ActivateShell(string executable, Action restart)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException(MissingShell, executable);

            string command = "\"" + executable + "\"";
            var view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (var key = machine.OpenSubKey(WinlogonKey, true))
            using (var user = Registry.CurrentUser.OpenSubKey(WinlogonKey, true))
            {
                if (key == null) throw new InvalidOperationException("The Winlogon setting could not be opened.");
                SetShellAndRestart(key, user, command, restart);
            }
        }

        private static void SetShellAndRestart(RegistryKey machine, RegistryKey user, string command, Action restart)
        {
            object previousUserShell = ReadShell(user);
            if (previousUserShell != null && !IsLegacyUserShell(previousUserShell as string))
                throw new InvalidOperationException("This account already has a custom Windows shell. Clear that per-user setting before activating WinNTDE.");

            object previousMachineShell = ReadShell(machine);
            var previousMachineKind = previousMachineShell == null ? RegistryValueKind.Unknown : machine.GetValueKind(ShellValue);
            var previousUserKind = previousUserShell == null ? RegistryValueKind.Unknown : user.GetValueKind(ShellValue);
            bool userShellRemoved = false;
            machine.SetValue(ShellValue, command, RegistryValueKind.String);
            try
            {
                if (!Equals(ReadShell(user), previousUserShell))
                    throw new InvalidOperationException("The per-user Windows shell changed during activation. Try again.");
                if (previousUserShell != null)
                {
                    user.DeleteValue(ShellValue, false);
                    userShellRemoved = true;
                }
                restart();
            }
            catch
            {
                try
                {
                    if (Equals(ReadShell(machine), command))
                    {
                        if (previousMachineShell == null) machine.DeleteValue(ShellValue, false);
                        else machine.SetValue(ShellValue, previousMachineShell, previousMachineKind);
                    }
                }
                finally
                {
                    if (userShellRemoved && ReadShell(user) == null)
                        user.SetValue(ShellValue, previousUserShell, previousUserKind);
                }
                throw;
            }
        }

        private static object ReadShell(RegistryKey key)
        {
            return key == null ? null : key.GetValue(ShellValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        private static bool IsLegacyUserShell(string command)
        {
            if (command == null) return false;
            string executable = command.Trim().Trim('"');
            return executable.Length == 0 || string.Equals(executable, "explorer.exe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(executable, "WinNTDE.exe", StringComparison.OrdinalIgnoreCase) ||
                executable.EndsWith(@"\WinNTDE.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static void RestartWindows()
        {
            string arguments = Environment.OSVersion.Version.Major >= 6 ? "/r /t 0" : "-r -t 0";
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Windows could not start the restart command.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not restart. The restart command returned " + process.ExitCode + ".");
            }
        }
    }
}
