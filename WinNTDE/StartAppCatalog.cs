using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WinNTDE
{
    internal sealed class StartApp
    {
        private readonly string shortcut, appId;
        private ImageSource icon;
        private bool hasIcon;

        private StartApp(string key, string name, string group, string shortcut, string appId)
        {
            Key = key;
            Name = name;
            Group = group;
            this.shortcut = shortcut;
            this.appId = appId;
        }

        internal string Key { get; private set; }
        public string Name { get; private set; }
        public string Group { get; private set; }
        public ImageSource Icon
        {
            get
            {
                if (!hasIcon)
                {
                    hasIcon = true;
                    icon = (shortcut == null ? DesktopFiles.NamespaceIcon(@"shell:AppsFolder\" + appId) :
                        DesktopFiles.FileIcon(shortcut, false)) ?? NativeDesktop.ApplicationIcon;
                }
                return icon;
            }
        }

        internal static StartApp FromShortcut(string key, FileInfo file, string group)
        {
            return new StartApp(key, Path.GetFileNameWithoutExtension(file.Name), group, file.FullName, null);
        }

        internal static StartApp FromPackage(string name, string id)
        {
            return new StartApp("package:" + id, name, null, null, id);
        }

        internal void Launch()
        {
            if (shortcut != null) DesktopFiles.OpenPath(shortcut);
            else StartAppCatalog.ActivatePackage(appId);
        }

        internal bool IsDefault(string name, string package)
        {
            return shortcut != null ? Name.Equals(name, StringComparison.CurrentCultureIgnoreCase) :
                appId.StartsWith(package + "_", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class StartAppCatalog
    {
        private static readonly HashSet<string> ShortcutExtensions = new HashSet<string>(
            new[] { ".lnk", ".url", ".appref-ms", ".exe", ".msc" }, StringComparer.OrdinalIgnoreCase);
        private const CompareOptions NameComparison = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        private static readonly Guid ActivationManagerClass = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");

        internal static StartApp[] Read()
        {
            var entries = new Dictionary<string, StartApp>(StringComparer.OrdinalIgnoreCase);
            ReadShortcuts(entries, Environment.SpecialFolder.Programs, Environment.SpecialFolder.Startup, true);
            ReadShortcuts(entries, Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.CommonStartup, true);
            ReadShortcuts(entries, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.Startup, false);
            ReadShortcuts(entries, Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonStartup, false);

            if (SystemSettings.WindowsVersion >= new Version(6, 2))
                ReadPackagedApps(entries);

            return entries.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(app => app.Group, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private static void ReadShortcuts(IDictionary<string, StartApp> entries, Environment.SpecialFolder folder,
            Environment.SpecialFolder startupFolder, bool includeSubfolders)
        {
            string root = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(root)) return;

            string startup = includeSubfolders ? Environment.GetFolderPath(startupFolder) : null;
            var folders = new Stack<DirectoryInfo>();
            var directory = new DirectoryInfo(root);
            folders.Push(directory);
            int relativeIndex = directory.FullName.TrimEnd(Path.DirectorySeparatorChar).Length + 1;
            string prefix = includeSubfolders ? "programs:" : "start:";

            while (folders.Count != 0)
            {
                FileSystemInfo[] children;
                try { children = folders.Pop().GetFileSystemInfos(); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                catch (System.Security.SecurityException) { continue; }

                foreach (var child in children)
                {
                    if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    var subfolder = child as DirectoryInfo;
                    if (subfolder != null)
                    {
                        if (includeSubfolders && (subfolder.Attributes & FileAttributes.ReparsePoint) == 0 &&
                            !subfolder.FullName.Equals(startup, StringComparison.OrdinalIgnoreCase)) folders.Push(subfolder);
                        continue;
                    }
                    var file = (FileInfo)child;
                    if (!ShortcutExtensions.Contains(file.Extension)) continue;
                    string relative = file.FullName.Substring(relativeIndex);
                    string key = prefix + relative;
                    if (!entries.ContainsKey(key))
                        entries.Add(key, StartApp.FromShortcut(key, file, Path.GetDirectoryName(relative)));
                }
            }
        }

        private static void ReadPackagedApps(IDictionary<string, StartApp> entries)
        {
            object shell = null, folder = null, items = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
                if (folder == null) return;
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = ((dynamic)items).Item(i);
                        string family = ((dynamic)item).ExtendedProperty("System.AppUserModel.PackageFamilyName") as string;
                        if (string.IsNullOrEmpty(family)) continue;
                        string id = ((dynamic)item).ExtendedProperty("System.AppUserModel.ID") as string;
                        string name = ((dynamic)item).Name;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(id)) continue;
                        var app = StartApp.FromPackage(name, id);
                        entries[app.Key] = app;
                    }
                    catch (COMException) { }
                    catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    finally { SystemSettings.Release(item); }
                }
            }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally
            {
                SystemSettings.Release(items);
                SystemSettings.Release(folder);
                SystemSettings.Release(shell);
            }
        }

        internal static StartApp[] Search(StartApp[] apps, string query)
        {
            var words = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            var comparer = CultureInfo.CurrentCulture.CompareInfo;
            return apps.Where(app => words.All(word => comparer.IndexOf(app.Name, word, NameComparison) >= 0))
                .OrderBy(app => comparer.Compare(app.Name, query, NameComparison) == 0 ? 0 :
                    comparer.IsPrefix(app.Name, query, NameComparison) ? 1 : 2).ToArray();
        }

        internal static string[] DefaultPins(StartApp[] apps)
        {
            var defaults = new[]
            {
                Tuple.Create("Notepad", "Microsoft.WindowsNotepad"),
                Tuple.Create("Calculator", "Microsoft.WindowsCalculator"),
                Tuple.Create("Paint", "Microsoft.Paint")
            };
            return defaults.Select(item => apps.FirstOrDefault(app => app.IsDefault(item.Item1, item.Item2)))
                .Where(app => app != null).Select(app => app.Key).ToArray();
        }

        internal static void ActivatePackage(string appId)
        {
            object manager = Activator.CreateInstance(Type.GetTypeFromCLSID(ActivationManagerClass, true));
            try
            {
                CoAllowSetForegroundWindow(manager, IntPtr.Zero);
                uint process;
                ((IApplicationActivationManager)manager).ActivateApplication(appId, null, 0, out process);
            }
            finally { SystemSettings.Release(manager); }
        }

        [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IApplicationActivationManager
        {
            void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint process);
        }

        [DllImport("ole32.dll")]
        private static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object server, IntPtr reserved);
    }
}
