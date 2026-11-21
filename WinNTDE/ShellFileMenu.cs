using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinNTDE
{
    internal static class ShellFileMenu
    {
        private const uint First = 1, Last = 0x6FFF, Open = 0x7001;

        internal static bool Show(Window window, string[] paths, Action open = null)
        {
            if (paths.Length == 0) return false;
            var pidls = new IntPtr[paths.Length];
            IShellFolder folder = null;
            IContextMenu context = null;
            try
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    uint attributes;
                    Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out attributes));
                }

                Guid folderId = typeof(IShellFolder).GUID;
                IntPtr firstChild;
                Marshal.ThrowExceptionForHR(SHBindToParent(pidls[0], ref folderId, out folder, out firstChild));
                var children = pidls.Select(ILFindLastID).ToArray();
                children[0] = firstChild;
                var hwnd = new WindowInteropHelper(window).Handle;
                Guid contextId = typeof(IContextMenu).GUID;
                Marshal.ThrowExceptionForHR(folder.GetUIObjectOf(hwnd, (uint)children.Length, children, ref contextId, IntPtr.Zero, out context));
                NativeDesktop.Point cursor;
                NativeDesktop.GetCursorPos(out cursor);
                return ShowMenu(window, context, Path.GetDirectoryName(paths[0]), cursor, open);
            }
            catch (Exception error) when (IsShellMenuError(error)) { return false; }
            finally
            {
                SystemSettings.Release(context);
                SystemSettings.Release(folder);
                foreach (IntPtr pidl in pidls)
                    if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

        internal static bool ShowFolderBackground(Window window, string path, UIElement anchor)
        {
            IContextMenu context = null;
            try
            {
                context = CreateFolderBackgroundMenu(new WindowInteropHelper(window).Handle, path);
                Point point = anchor.PointToScreen(new Point(0, anchor.RenderSize.Height));
                var position = new NativeDesktop.Point { X = (int)point.X, Y = (int)point.Y };
                return ShowMenu(window, context, path, position);
            }
            catch (Exception error) when (IsShellMenuError(error)) { return false; }
            finally { SystemSettings.Release(context); }
        }

        private static IContextMenu CreateFolderBackgroundMenu(IntPtr hwnd, string path)
        {
            IntPtr pidl = IntPtr.Zero;
            IShellFolder desktop = null, folder = null;
            try
            {
                uint attributes;
                Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attributes));
                Marshal.ThrowExceptionForHR(SHGetDesktopFolder(out desktop));
                Guid folderId = typeof(IShellFolder).GUID;
                Marshal.ThrowExceptionForHR(desktop.BindToObject(pidl, IntPtr.Zero, ref folderId, out folder));
                Guid menuId = typeof(IContextMenu).GUID;
                IContextMenu context;
                // The folder's view object supplies the background menu, including the shell's New submenu.
                Marshal.ThrowExceptionForHR(folder.CreateViewObject(hwnd, ref menuId, out context));
                return context;
            }
            finally
            {
                SystemSettings.Release(folder);
                SystemSettings.Release(desktop);
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }
        }

        private static bool ShowMenu(Window window, IContextMenu context, string directory, NativeDesktop.Point position, Action open = null)
        {
            if (context == null) return false;
            var hwnd = new WindowInteropHelper(window).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            if (source == null) return false;
            IntPtr menu = CreatePopupMenu();
            try
            {
                if (menu == IntPtr.Zero) return false;
                Marshal.ThrowExceptionForHR(context.QueryContextMenu(menu, 0, First, Last,
                    (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0x100u : 0u));

                if (open != null)
                {
                    InsertMenu(menu, 0, 0x400, new UIntPtr(Open), "Open in WinNTDE");
                    InsertMenu(menu, 1, 0xC00, UIntPtr.Zero, null);
                }
                if (GetMenuItemCount(menu) <= 0) return false;

                var context3 = context as IContextMenu3;
                var context2 = context3 == null ? context as IContextMenu2 : null;
                HwndSourceHook hook = (IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    IntPtr result = IntPtr.Zero;
                    if (message == 0x0117 || message == 0x002B || message == 0x002C || message == 0x0120)
                    {
                        if (context3 != null) handled = context3.HandleMenuMsg2(message, wParam, lParam, out result) == 0;
                        else if (context2 != null && message != 0x0120) handled = context2.HandleMenuMsg(message, wParam, lParam) == 0;
                    }
                    return result;
                };

                NativeDesktop.SetForegroundWindow(hwnd);
                source.AddHook(hook);
                uint command;
                try { command = TrackPopupMenuEx(menu, 0x0102, position.X, position.Y, hwnd, IntPtr.Zero); }
                finally { source.RemoveHook(hook); }
                if (command == Open && open != null) open();
                else if (command >= First && command <= Last)
                {
                    var verb = new IntPtr(command - First);
                    var invoke = new InvokeCommand
                    {
                        Size = (uint)Marshal.SizeOf(typeof(InvokeCommand)),
                        Mask = 0x00004000 | 0x20000000,
                        Owner = hwnd,
                        Verb = verb,
                        VerbUnicode = verb,
                        DirectoryUnicode = directory,
                        Show = 1,
                        Point = position
                    };
                    DesktopFiles.Try(window, () => Marshal.ThrowExceptionForHR(context.InvokeCommand(ref invoke)));
                }
                return true;
            }
            finally { if (menu != IntPtr.Zero) DestroyMenu(menu); }
        }

        private static bool IsShellMenuError(Exception error)
        {
            return error is COMException || error is IOException || error is UnauthorizedAccessException ||
                error is ArgumentException || error is InvalidCastException || error is NotImplementedException;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct InvokeCommand
        {
            internal uint Size, Mask;
            internal IntPtr Owner, Verb, Parameters, Directory;
            internal int Show;
            internal uint HotKey;
            internal IntPtr Icon, Title, VerbUnicode, ParametersUnicode;
            [MarshalAs(UnmanagedType.LPWStr)] internal string DirectoryUnicode;
            internal IntPtr TitleUnicode;
            internal NativeDesktop.Point Point;
        }

        [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            void ParseDisplayName();
            void EnumObjects();
            [PreserveSig] int BindToObject(IntPtr pidl, IntPtr context, ref Guid iid, out IShellFolder folder);
            void BindToStorage();
            void CompareIDs();
            [PreserveSig] int CreateViewObject(IntPtr owner, ref Guid iid, out IContextMenu context);
            void GetAttributesOf();
            [PreserveSig] int GetUIObjectOf(IntPtr owner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)]
                IntPtr[] children, ref Guid iid, IntPtr reserved, out IContextMenu context);
        }

        [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
            [PreserveSig] int InvokeCommand(ref InvokeCommand info);
            void GetCommandString();
        }

        [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            void QueryContextMenu();
            void InvokeCommand();
            void GetCommandString();
            [PreserveSig] int HandleMenuMsg(int message, IntPtr wParam, IntPtr lParam);
        }

        [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu3
        {
            void QueryContextMenu();
            void InvokeCommand();
            void GetCommandString();
            void HandleMenuMsg();
            [PreserveSig] int HandleMenuMsg2(int message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint mask, out uint attributes);
        [DllImport("shell32.dll")]
        private static extern int SHBindToParent(IntPtr pidl, ref Guid id, out IShellFolder folder, out IntPtr child);
        [DllImport("shell32.dll")] private static extern int SHGetDesktopFolder(out IShellFolder desktop);
        [DllImport("shell32.dll")] private static extern IntPtr ILFindLastID(IntPtr pidl);
        [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool InsertMenu(IntPtr menu, uint position, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);
    }
}
