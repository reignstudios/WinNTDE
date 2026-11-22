using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;
using Microsoft.CSharp.RuntimeBinder;

namespace WinNTDE
{
    internal sealed class WindowsUpdates
    {
        internal const string InstallCommand = "--install-updates";
        private const string Applicable = "IsInstalled=0 and IsHidden=0";
        private const int Succeeded = 2, SucceededWithErrors = 3;
        private const int AccessDenied = unchecked((int)0x80070005), LicenseUnavailable = unchecked((int)0x80240033);

        internal sealed class Update
        {
            public string Title { get; internal set; }
            internal string Key, License;
            internal bool Exclusive;
        }

        private enum InstallOutcome
        {
            Installed = 0, Incomplete, IncompleteRestart, SelectionChanged, InstallSeparately, RestartFirst, Busy, LicenseNeeded,
            InvalidSelection, PermissionDenied, NotAvailable, Cancelled = 1223, InstalledRestart = 3010
        }

        internal Update[] Available { get; private set; } = new Update[0];
        internal bool Busy { get; private set; }
        internal string Status { get; private set; } = "Check for updates to see what's available.";
        internal event EventHandler Changed;

        internal void Check()
        {
            Run("Checking for updates…", () => ReadAvailable(true));
        }

        internal void Install(Update[] selected)
        {
            if (selected.Length == 0) return;
            var current = Available;
            Run("Downloading and installing selected updates…", () =>
            {
                var outcome = StartInstaller(selected);
                string status = Describe(outcome);
                bool installed = outcome == InstallOutcome.Installed || outcome == InstallOutcome.InstalledRestart;
                if (installed) current = current.Except(selected).ToArray();
                if (installed || outcome == InstallOutcome.Incomplete || outcome == InstallOutcome.IncompleteRestart ||
                    outcome == InstallOutcome.SelectionChanged || (int)outcome < 0)
                {
                    try
                    {
                        var refreshed = ReadAvailable(false);
                        var keys = new HashSet<string>(selected.Select(update => update.Key));
                        current = installed ? refreshed.Item1.Where(update => !keys.Contains(update.Key)).ToArray() : refreshed.Item1;
                    }
                    catch (Exception error) when (IsUpdateError(error)) { status += " Check for updates to refresh the list."; }
                }
                return Tuple.Create(current, status);
            });
        }

        private void Run(string working, Func<Tuple<Update[], string>> work)
        {
            if (Busy) return;
            var dispatcher = Dispatcher.CurrentDispatcher;
            Busy = true;
            Status = working;
            Changed?.Invoke(this, EventArgs.Empty);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tuple<Update[], string> result = null;
                string failure = null;
                try { result = work(); }
                catch (Exception error) when (IsUpdateError(error)) { failure = error.Message; }
                if (dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() =>
                {
                    Busy = false;
                    if (result != null) Available = result.Item1;
                    Status = result == null ? failure : result.Item2;
                    Changed?.Invoke(this, EventArgs.Empty);
                }));
            });
        }

        private static Tuple<Update[], string> ReadAvailable(bool online)
        {
            return Search(online, (session, updates, partial) =>
            {
                var available = new List<Update>();
                int count = ((dynamic)updates).Count;
                for (int index = 0; index < count; index++)
                {
                    object update = ((dynamic)updates).Item(index);
                    try
                    {
                        available.Add(new Update { Title = ((dynamic)update).Title, Key = GetKey(update),
                            License = GetLicense(update), Exclusive = IsExclusive(update) });
                    }
                    finally { SystemSettings.Release(update); }
                }
                string status = count == 0 ? "No applicable updates found." : count + (count == 1 ? " update available." : " updates available.");
                if (partial) status = "Windows Update returned an incomplete list. " + (count == 0 ? "Check again to retry." : status);
                return Tuple.Create(available.ToArray(), status);
            });
        }

        private static T Search<T>(bool online, Func<object, object, bool, T> read)
        {
            object session = null, searcher = null, result = null, updates = null;
            try
            {
                session = Create("Microsoft.Update.Session");
                ((dynamic)session).ClientApplicationID = "WinNTDE";
                searcher = ((dynamic)session).CreateUpdateSearcher();
                ((dynamic)searcher).Online = online;
                result = ((dynamic)searcher).Search(Applicable);
                int code = ((dynamic)result).ResultCode;
                if (code != Succeeded && code != SucceededWithErrors)
                    throw new InvalidOperationException("Windows Update could not complete the check. Status " + code + ".");
                updates = ((dynamic)result).Updates;
                return read(session, updates, code == SucceededWithErrors);
            }
            finally
            {
                SystemSettings.Release(updates);
                SystemSettings.Release(result);
                SystemSettings.Release(searcher);
                SystemSettings.Release(session);
            }
        }

        private static InstallOutcome StartInstaller(Update[] updates)
        {
            string arguments = InstallCommand + " " + string.Join(" ", updates.Select(update => update.Key + (update.License == null ? ":0" : ":1")));
            var start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, arguments) { UseShellExecute = true,
                WorkingDirectory = Environment.SystemDirectory, Verb = IsAdministrator() ? "open" : "runas" };
            try
            {
                using (Process process = Process.Start(start))
                {
                    if (process == null) return InstallOutcome.NotAvailable;
                    process.WaitForExit();
                    return (InstallOutcome)process.ExitCode;
                }
            }
            catch (Win32Exception error) when (error.NativeErrorCode == (int)InstallOutcome.Cancelled) { return InstallOutcome.Cancelled; }
        }

        internal static int InstallFromArguments(string[] arguments)
        {
            var selected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < arguments.Length; index++)
            {
                string[] parts = arguments[index].Split(':');
                Guid id;
                int revision;
                if (parts.Length != 3 || !Guid.TryParseExact(parts[0], "D", out id) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out revision) ||
                    (parts[2] != "0" && parts[2] != "1")) return (int)InstallOutcome.InvalidSelection;
                string key = UpdateKey(id, revision);
                if (selected.ContainsKey(key)) return (int)InstallOutcome.InvalidSelection;
                selected.Add(key, parts[2] == "1");
            }
            if (selected.Count == 0) return (int)InstallOutcome.InvalidSelection;
            if (!IsAdministrator()) return (int)InstallOutcome.PermissionDenied;
            try { return (int)Search(false, (session, updates, partial) => InstallAvailable(session, updates, selected)); }
            catch (COMException error) { return error.ErrorCode; }
            catch (UnauthorizedAccessException) { return AccessDenied; }
            catch (Exception error) when (IsUpdateError(error)) { return (int)InstallOutcome.NotAvailable; }
        }

        private static InstallOutcome InstallAvailable(object session, object available, Dictionary<string, bool> selection)
        {
            object chosen = null, installer = null;
            try
            {
                chosen = Create("Microsoft.Update.UpdateColl");
                int count = ((dynamic)available).Count;
                for (int index = 0; index < count; index++)
                {
                    object update = ((dynamic)available).Item(index);
                    try
                    {
                        bool approvedLicense;
                        if (!selection.TryGetValue(GetKey(update), out approvedLicense)) continue;
                        if (selection.Count > 1 && IsExclusive(update)) return InstallOutcome.InstallSeparately;
                        if (!approvedLicense && !(bool)((dynamic)update).EulaAccepted) return InstallOutcome.LicenseNeeded;
                        ((dynamic)chosen).Add((dynamic)update);
                    }
                    finally { SystemSettings.Release(update); }
                }
                if ((int)((dynamic)chosen).Count != selection.Count) return InstallOutcome.SelectionChanged;
                installer = ((dynamic)session).CreateUpdateInstaller();
                if ((bool)((dynamic)installer).RebootRequiredBeforeInstallation) return InstallOutcome.RestartFirst;
                if ((bool)((dynamic)installer).IsBusy) return InstallOutcome.Busy;
                return DownloadAndInstall(session, installer, chosen);
            }
            finally
            {
                SystemSettings.Release(installer);
                SystemSettings.Release(chosen);
            }
        }

        private static InstallOutcome DownloadAndInstall(object session, object installer, object chosen)
        {
            object downloads = null, downloader = null, downloadResult = null, ready = null, installResult = null;
            try
            {
                downloads = Create("Microsoft.Update.UpdateColl");
                int count = ((dynamic)chosen).Count;
                for (int index = 0; index < count; index++)
                {
                    object update = ((dynamic)chosen).Item(index);
                    try
                    {
                        if (!(bool)((dynamic)update).EulaAccepted) ((dynamic)update).AcceptEula();
                        if (!(bool)((dynamic)update).IsDownloaded) ((dynamic)downloads).Add((dynamic)update);
                    }
                    finally { SystemSettings.Release(update); }
                }
                if ((int)((dynamic)downloads).Count != 0)
                {
                    downloader = ((dynamic)session).CreateUpdateDownloader();
                    ((dynamic)downloader).Updates = (dynamic)downloads;
                    downloadResult = ((dynamic)downloader).Download();
                }

                ready = Create("Microsoft.Update.UpdateColl");
                for (int index = 0; index < count; index++)
                {
                    object update = ((dynamic)chosen).Item(index);
                    try { if ((bool)((dynamic)update).IsDownloaded) ((dynamic)ready).Add((dynamic)update); }
                    finally { SystemSettings.Release(update); }
                }
                int readyCount = ((dynamic)ready).Count;
                if (readyCount == 0) return FailureResult(downloadResult);
                ((dynamic)installer).Updates = (dynamic)ready;
                installResult = ((dynamic)installer).Install();
                bool restart = ((dynamic)installResult).RebootRequired;
                if (readyCount == count && (int)((dynamic)installResult).ResultCode == Succeeded)
                    return restart ? InstallOutcome.InstalledRestart : InstallOutcome.Installed;
                return restart ? InstallOutcome.IncompleteRestart : FailureResult(installResult);
            }
            finally
            {
                SystemSettings.Release(installResult);
                SystemSettings.Release(ready);
                SystemSettings.Release(downloadResult);
                SystemSettings.Release(downloader);
                SystemSettings.Release(downloads);
            }
        }

        private static InstallOutcome FailureResult(object result)
        {
            int error = result == null ? 0 : (int)((dynamic)result).HResult;
            return error < 0 ? (InstallOutcome)error : InstallOutcome.Incomplete;
        }

        private static string GetKey(object update)
        {
            object identity = ((dynamic)update).Identity;
            try { return UpdateKey(Guid.Parse((string)((dynamic)identity).UpdateID), (int)((dynamic)identity).RevisionNumber); }
            finally { SystemSettings.Release(identity); }
        }

        private static string GetLicense(object update)
        {
            if ((bool)((dynamic)update).EulaAccepted) return null;
            try { return (string)((dynamic)update).EulaText ?? ""; }
            catch (COMException error) when (error.ErrorCode == LicenseUnavailable) { return ""; }
        }

        private static bool IsExclusive(object update)
        {
            object behavior = ((dynamic)update).InstallationBehavior;
            try { return (int)((dynamic)behavior).Impact == 2; } // iiRequiresExclusiveHandling
            finally { SystemSettings.Release(behavior); }
        }

        private static string UpdateKey(Guid id, int revision) { return id.ToString("D") + ":" + revision.ToString(CultureInfo.InvariantCulture); }
        private static object Create(string progId) { return Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)); }

        private static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static bool IsUpdateError(Exception error)
        {
            return error is ExternalException || error is InvalidOperationException || error is UnauthorizedAccessException ||
                error is System.Security.SecurityException || error is TypeLoadException || error is RuntimeBinderException ||
                error is FormatException || error is InvalidCastException || error is NotSupportedException;
        }

        private static string Describe(InstallOutcome outcome)
        {
            switch (outcome)
            {
                case InstallOutcome.Installed: return "Selected updates installed.";
                case InstallOutcome.InstalledRestart: return "Selected updates installed. Restart Windows to finish.";
                case InstallOutcome.IncompleteRestart: return "Some selected updates did not install. Restart Windows to finish the updates that did.";
                case InstallOutcome.SelectionChanged: return "The available updates changed. Check for updates and select them again.";
                case InstallOutcome.InstallSeparately: return "An update in the selection must be installed separately.";
                case InstallOutcome.RestartFirst: return "Restart Windows before installing more updates.";
                case InstallOutcome.Busy: return "Windows Update is already installing updates. Try again when it finishes.";
                case InstallOutcome.LicenseNeeded: return "An update requires license approval. Check for updates again to review its terms.";
                case InstallOutcome.Cancelled: return "Update installation was canceled.";
                case InstallOutcome.PermissionDenied:
                case (InstallOutcome)AccessDenied:
                case (InstallOutcome)unchecked((int)0x80246017): return "Windows denied permission to install updates. Administrator approval is required.";
                case InstallOutcome.NotAvailable: return "The Windows Update installer could not start.";
                case InstallOutcome.Incomplete: return "Windows could not install all selected updates.";
                default: return "Windows could not install all selected updates. Error 0x" + ((int)outcome).ToString("X8") + ".";
            }
        }
    }
}
