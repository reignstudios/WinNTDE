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
                Close();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is SecurityException || error is Win32Exception || error is InvalidOperationException)
            {
                ShowError(error.Message);
            }
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
            using (var key = Registry.CurrentUser.CreateSubKey(WinlogonKey))
            {
                if (key == null) throw new InvalidOperationException("The Winlogon setting could not be opened.");
                object previous = key.GetValue(ShellValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                var previousKind = previous == null ? RegistryValueKind.Unknown : key.GetValueKind(ShellValue);
                key.SetValue(ShellValue, command, RegistryValueKind.String);

                try { restart(); }
                catch
                {
                    if (Equals(key.GetValue(ShellValue), command))
                    {
                        if (previous == null) key.DeleteValue(ShellValue, false);
                        else key.SetValue(ShellValue, previous, previousKind);
                    }
                    throw;
                }
            }
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
