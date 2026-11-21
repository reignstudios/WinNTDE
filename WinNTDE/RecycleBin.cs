using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinNTDE
{
    internal static class RecycleBin
    {
        internal const string Location = "shell:RecycleBinFolder";
        internal const string DisplayName = "Recycle Bin";
        private const int RecycleBinFolder = 10;
        private static readonly Guid TransferHandler = new Guid("D5E346A1-F753-4932-B403-4574800E2498");
        internal static event EventHandler Changed;

        internal static FileEntry[] Read(bool details)
        {
            var entries = new List<FileEntry>();
            object shell = null, folder = null, items = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true));
                folder = ((dynamic)shell).NameSpace(RecycleBinFolder);
                if (folder == null) throw new IOException("Windows could not open the Recycle Bin.");
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = ((dynamic)items).Item(i);
                        if (item == null) continue;
                        entries.Add(ReadItem(item, details));
                    }
                    catch (COMException) { } // A recycled item can disappear while the folder is being read.
                    finally { SystemSettings.Release(item); }
                }
                return entries.ToArray();
            }
            finally
            {
                SystemSettings.Release(items);
                SystemSettings.Release(folder);
                SystemSettings.Release(shell);
            }
        }

        private static FileEntry ReadItem(dynamic item, bool details)
        {
            string name = item.Name;
            string path = item.Path;
            bool isFolder = item.IsFolder;
            if (!details) return new FileEntry(name, path, isFolder);

            try
            {
                string kind = item.Type;
                object date = item.ModifyDate;
                string modified = date is DateTime ? ((DateTime)date).ToString("g", CultureInfo.CurrentCulture) : null;
                long size = isFolder ? 0 : Convert.ToInt64(item.Size, CultureInfo.InvariantCulture);
                return new FileEntry(name, path, isFolder, kind, modified,
                    !isFolder && size >= 0 ? (size / 1024.0).ToString("N1") + " KB" : null);
            }
            catch (COMException) { return new FileEntry(name, path, isFolder); }
        }

        internal static void MoveToBin(Window owner, string[] paths)
        {
            try
            {
                if (SystemSettings.WindowsVersion.Major < 6)
                {
                    DesktopFiles.Delete(owner, paths, true);
                    return;
                }

                // RecycleItem requests a recycle operation directly and has no permanent-delete fallback.
                IShellItem bin = OpenShellItem(Location);
                try
                {
                    foreach (string path in paths) RecycleItem(path, bin);
                }
                finally { SystemSettings.Release(bin); }
            }
            finally { NotifyChanged(); }
        }

        private static void RecycleItem(string path, IShellItem bin)
        {
            IShellItem item = null, parent = null, destination = null;
            ITransferSource transfer = null;
            try
            {
                item = OpenShellItem(path);
                parent = item.GetParent();
                Guid handler = TransferHandler;
                Guid id = typeof(ITransferSource).GUID;
                transfer = parent.BindToHandler(IntPtr.Zero, ref handler, ref id);
                transfer.RecycleItem(item, bin, 0, out destination);
                if (destination == null) throw new IOException("Windows did not return a recycled item.");
            }
            catch (COMException error)
            {
                throw new IOException("Windows could not move \"" + Path.GetFileName(path) + "\" to the Recycle Bin.", error);
            }
            finally
            {
                SystemSettings.Release(destination);
                SystemSettings.Release(transfer);
                SystemSettings.Release(parent);
                SystemSettings.Release(item);
            }
        }

        internal static void Empty(Window owner)
        {
            try
            {
                const uint NoConfirmation = 1, NoSound = 4;
                Marshal.ThrowExceptionForHR(SHEmptyRecycleBin(new WindowInteropHelper(owner).Handle, null, NoConfirmation | NoSound));
            }
            finally { NotifyChanged(); }
        }

        private static void NotifyChanged()
        {
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        private static IShellItem OpenShellItem(string path)
        {
            Guid id = typeof(IShellItem).GUID;
            IShellItem item;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, ref id, out item));
            return item;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid id, out IShellItem item);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr owner, string root, uint flags);

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            ITransferSource BindToHandler(IntPtr context, ref Guid handler, ref Guid id);
            IShellItem GetParent();
        }

        [ComImport, Guid("00ADB003-BDE9-45C6-8E29-D09F9353E108"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITransferSource
        {
            void Advise();
            void Unadvise();
            void SetProperties();
            void OpenItem();
            void MoveItem();
            void RecycleItem(IShellItem item, IShellItem bin, uint flags, out IShellItem destination);
        }
    }
}
