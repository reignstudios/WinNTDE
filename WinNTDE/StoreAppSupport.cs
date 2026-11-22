using System;
using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Threading;

namespace WinNTDE
{
    internal sealed class StoreAppSupport
    {
        internal static bool IsSupported { get { return SystemSettings.WindowsVersion >= new Version(6, 2); } }
        internal string Status { get; private set; }
        internal event EventHandler Changed;

        internal void Start(ShellPreferences preferences, bool isShell, Dispatcher dispatcher)
        {
            bool enabled = preferences.StartStoreAppServices;
            long changeBoot = preferences.StoreAppServicesChangedBoot;
            if (!IsSupported || (!enabled && changeBoot == 0)) return;
            if (!isShell)
            {
                Status = "Service startup runs when WinNTDE is the Windows shell.";
                return;
            }

            Status = "Checking Store app services…";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string result;
                try { result = Initialize(enabled, changeBoot); }
                catch (InvalidOperationException error) { result = error.Message; }
                if (dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Status = result;
                    if (Changed != null) Changed(this, EventArgs.Empty);
                }));
            });
        }

        internal static void SaveSetting(ShellPreferences preferences, bool enabled)
        {
            bool previousEnabled = preferences.StartStoreAppServices;
            if (previousEnabled == enabled) return;
            long previousBoot = preferences.StoreAppServicesChangedBoot;
            long boot = ReadBootTime();
            preferences.StartStoreAppServices = enabled;
            // Switching back on the same boot cancels the pending change.
            preferences.StoreAppServicesChangedBoot = previousBoot == boot ? 0 : boot;
            try { preferences.Save(); }
            catch
            {
                preferences.StartStoreAppServices = previousEnabled;
                preferences.StoreAppServicesChangedBoot = previousBoot;
                throw;
            }
        }

        private static string Initialize(bool enabled, long changeBoot)
        {
            // Until Windows restarts, keep using the previous value of the setting.
            bool pending = changeBoot != 0 && changeBoot == ReadBootTime();
            if (pending) enabled = !enabled;
            if (!enabled) return pending ? "Restart Windows to start the Store app services." : "";

            var services = SystemSettings.WindowsVersion.Major >= 10
                ? new[] { "BrokerInfrastructure", "StateRepository", "AppXSvc", "ClipSVC" }
                : new[] { "BrokerInfrastructure", "AppXSvc", "WSService" };
            var statuses = new string[services.Length];
            for (int i = 0; i < services.Length; i++) statuses[i] = StartService(services[i]);
            return string.Join(Environment.NewLine, statuses);
        }

        private static string StartService(string name)
        {
            try
            {
                using (var service = new ServiceController(name))
                {
                    var status = service.Status;
                    if (status == ServiceControllerStatus.Running) return name + " is running.";
                    if (status == ServiceControllerStatus.StartPending) return name + " is starting.";
                    if (status != ServiceControllerStatus.Stopped) return name + " is " + status + ".";
                    service.Start();
                    return name + " start requested.";
                }
            }
            catch (Exception error) when (error is InvalidOperationException || error is Win32Exception ||
                error is UnauthorizedAccessException || error is SecurityException)
            {
                var nativeError = error as Win32Exception ?? error.InnerException as Win32Exception;
                if (nativeError != null && nativeError.NativeErrorCode == 1056) return name + " is already running.";
                return name + " — " + (nativeError == null ? error.Message : nativeError.Message);
            }
        }

        private static long ReadBootTime()
        {
            try
            {
                using (var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
                using (var systems = query.Get())
                {
                    foreach (ManagementObject system in systems)
                        using (system)
                        {
                            var boot = system["LastBootUpTime"] as string;
                            if (!string.IsNullOrEmpty(boot))
                                return ManagementDateTimeConverter.ToDateTime(boot).ToUniversalTime().Ticks;
                        }
                }
            }
            catch (Exception error) when (error is ManagementException || error is COMException ||
                error is UnauthorizedAccessException || error is SecurityException || error is ArgumentException)
            {
                throw new InvalidOperationException("WinNTDE could not determine when Windows last restarted.", error);
            }
            throw new InvalidOperationException("WinNTDE could not determine when Windows last restarted.");
        }
    }
}
