using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace WinNTDE.Activator
{
    internal static class ClickOnceLauncher
    {
        [STAThread]
        private static void Main()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string activator = Path.Combine(directory, "WinNTDE.Activator.exe");
            try
            {
                if (!File.Exists(activator))
                    throw new FileNotFoundException("WinNTDE.Activator.exe is missing from the installation.");

                var start = new ProcessStartInfo(activator) { UseShellExecute = true, WorkingDirectory = directory };
                if (Environment.OSVersion.Version.Major >= 6) start.Verb = "runas";
                using (var process = Process.Start(start))
                    if (process == null) throw new InvalidOperationException("Windows could not open the Activator.");
            }
            catch (Win32Exception error) when (error.NativeErrorCode == 1223)
            {
                // The user canceled the UAC prompt.
            }
            catch (Exception error) when (error is IOException || error is Win32Exception || error is InvalidOperationException)
            {
                MessageBox.Show(error.Message, "WinNTDE Activator", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
