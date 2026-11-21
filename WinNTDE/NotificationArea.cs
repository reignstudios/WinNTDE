using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WinNTDE
{
    internal sealed class NotificationArea : IDisposable
    {
        private const string ClassName = "Shell_TrayWnd";
        private readonly List<TrayIcon> icons = new List<TrayIcon>();
        private readonly WindowProcedure procedure;
        private readonly IntPtr instance = GetModuleHandle(null);
        private readonly Func<NativeDesktop.Rect> bounds;
        private readonly Func<bool> autoHide;
        private IntPtr hwnd;
        private bool registered;
        internal event EventHandler Changed;

        internal NotificationArea(Func<NativeDesktop.Rect> bounds, Func<bool> autoHide)
        {
            this.bounds = bounds;
            this.autoHide = autoHide;
            procedure = HandleMessage;
            var windowClass = new WindowClass { Procedure = procedure, Instance = instance, Name = ClassName };
            registered = RegisterClass(ref windowClass) != 0;
            if (!registered) throw new Win32Exception();
            hwnd = CreateWindowEx(0x80, ClassName, null, 0x80000000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Dispose();
                throw new Win32Exception(error);
            }
            NativeDesktop.SendNotifyMessage(NativeDesktop.Broadcast, NativeDesktop.RegisterWindowMessage("TaskbarCreated"),
                IntPtr.Zero, IntPtr.Zero);
        }

        internal IEnumerable<TrayIcon> VisibleIcons { get { return icons.Where(icon => !icon.Hidden && icon.Image != null); } }

        internal void Place(NativeDesktop.Rect area)
        {
            NativeDesktop.SetWindowPos(hwnd, IntPtr.Zero, area.Left, area.Top, area.Width, area.Height, 0x0014);
        }

        internal void RemoveClosedWindows()
        {
            if (icons.RemoveAll(icon => !NativeDesktop.IsWindow(icon.Owner)) > 0) RaiseChanged();
        }

        private void RaiseChanged()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
        }

        private IntPtr HandleMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam)
        {
            if (message != 0x004A || lParam == IntPtr.Zero)
                return DefWindowProc(handle, message, wParam, lParam);
            var copy = (CopyData)Marshal.PtrToStructure(lParam, typeof(CopyData));
            if (copy.Buffer == IntPtr.Zero) return IntPtr.Zero;

            if (copy.Kind == new IntPtr(1))
                return ReadNotifyIcon(copy.Buffer, copy.Size) ? new IntPtr(1) : IntPtr.Zero;
            if (copy.Kind == IntPtr.Zero && copy.Size >= 8)
            {
                int command = Marshal.ReadInt32(copy.Buffer, 4);
                if (command == 4) return new IntPtr(autoHide() ? 3 : 2);
            }
            if (copy.Kind == new IntPtr(3) && copy.Size >= 32)
            {
                // Shell_NotifyIconGetRect queries the top-left and bottom-right corners separately.
                int query = Marshal.ReadInt32(copy.Buffer, 8);
                var bar = bounds();
                if (query == 1) return PackPoint(bar.Right - 80, bar.Top);
                if (query == 2) return PackPoint(bar.Right - 48, bar.Bottom);
            }
            return IntPtr.Zero;
        }

        private bool ReadNotifyIcon(IntPtr data, int size)
        {
            // The shell transport normally uses 32-bit HWND and HICON fields, even on 64-bit Windows.
            // Newer versions can append data after NOTIFYICONDATA. Use its own size to identify the layout.
            if (size < 8 + 24 || Marshal.ReadInt32(data) != 0x34753423) return false;
            int command = Marshal.ReadInt32(data, 4);
            if (command < 0 || command > 4) return false;
            IntPtr value = IntPtr.Add(data, 8);
            int declared = Marshal.ReadInt32(value);
            bool wide = declared == 160 || declared == 944 || declared == 960 || declared == 968 || declared == 976;
            int length = size - 8;
            int tipOffset = wide ? 32 : 24;
            if (length < tipOffset) return false;
            int idOffset = wide ? 16 : 8;
            var owner = new IntPtr(Marshal.ReadInt32(value, wide ? 8 : 4));
            int id = Marshal.ReadInt32(value, idOffset);
            int flags = Marshal.ReadInt32(value, idOffset + 4);
            int callback = Marshal.ReadInt32(value, idOffset + 8);
            int stateOffset = tipOffset + 256;
            int versionOffset = stateOffset + 8 + 512;
            int guidOffset = versionOffset + 4 + 128 + 4;
            Guid guid = Guid.Empty;
            if ((flags & 0x20) != 0 && length >= guidOffset + 16)
                guid = (Guid)Marshal.PtrToStructure(IntPtr.Add(value, guidOffset), typeof(Guid));

            var icon = icons.FirstOrDefault(item => guid != Guid.Empty ? item.Guid == guid :
                item.Owner == owner && item.Id == id);
            if (command == 2)
            {
                if (icon == null) return false;
                icons.Remove(icon);
                RaiseChanged();
                return true;
            }
            if (command == 3) return icon != null;
            if (command == 4)
            {
                if (icon == null || length < versionOffset + 4) return false;
                int version = Marshal.ReadInt32(value, versionOffset);
                if (version != 0 && version != 3 && version != 4) return false;
                icon.Version = version;
                return true;
            }
            if (owner == IntPtr.Zero || !NativeDesktop.IsWindow(owner)) return false;
            if (icon == null)
            {
                if (command != 0) return false;
                icon = new TrayIcon { Guid = guid };
                icons.Add(icon);
            }

            icon.Owner = owner;
            icon.Id = id;
            if ((flags & 1) != 0) icon.Callback = callback;
            if ((flags & 2) != 0)
                icon.Image = NativeDesktop.IconImage(new IntPtr(Marshal.ReadInt32(value, wide ? 24 : 20)), 24);
            if ((flags & 4) != 0)
            {
                int chars = Math.Min(128, (length - tipOffset) / 2);
                if (declared == 152 || declared == 160) chars = Math.Min(64, chars);
                icon.Name = Marshal.PtrToStringUni(IntPtr.Add(value, tipOffset), chars).Split('\0')[0];
            }
            if ((flags & 8) != 0 && length >= stateOffset + 8 && (Marshal.ReadInt32(value, stateOffset + 4) & 1) != 0)
                icon.Hidden = (Marshal.ReadInt32(value, stateOffset) & 1) != 0;
            RaiseChanged();
            return true;
        }

        private static IntPtr PackPoint(int x, int y)
        {
            return new IntPtr((x & 0xFFFF) | (y << 16));
        }

        internal sealed class TrayIcon
        {
            internal Guid Guid;
            internal IntPtr Owner;
            internal int Id, Callback, Version;
            internal bool Hidden;
            internal string Name;
            internal ImageSource Image;

            internal void Click(bool right, bool doubleClick = false)
            {
                if (Callback == 0 || !NativeDesktop.IsWindow(Owner)) return;
                AllowSetForegroundWindow(NativeDesktop.WindowProcess(Owner));
                IntPtr position = new IntPtr(Id);
                if (Version == 4)
                {
                    NativeDesktop.Point point;
                    NativeDesktop.GetCursorPos(out point);
                    position = PackPoint(point.X, point.Y);
                }
                PostMouse(position, right ? 0x0204 : doubleClick ? 0x0203 : 0x0201);
                PostMouse(position, right ? 0x0205 : 0x0202);
                if (Version >= 3 && !doubleClick) PostMouse(position, right ? 0x007B : 0x0400);
            }

            private void PostMouse(IntPtr position, int message)
            {
                IntPtr eventData = Version == 4 ? PackPoint(message, Id) : new IntPtr(message);
                NativeDesktop.PostMessage(Owner, (uint)Callback, position, eventData);
            }
        }

        public void Dispose()
        {
            if (hwnd != IntPtr.Zero) DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
            if (registered) UnregisterClass(ClassName, instance);
            registered = false;
        }

        private delegate IntPtr WindowProcedure(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            internal uint Style;
            internal WindowProcedure Procedure;
            internal int ClassExtra, WindowExtra;
            internal IntPtr Instance, Icon, Cursor, Background;
            internal string MenuName, Name;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CopyData
        {
            internal IntPtr Kind;
            internal int Size;
            internal IntPtr Buffer;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, IntPtr instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int process);
    }
}
