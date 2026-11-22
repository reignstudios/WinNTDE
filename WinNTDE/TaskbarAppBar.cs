using System;
using System.Runtime.InteropServices;

namespace WinNTDE
{
    internal sealed class TaskbarAppBar
    {
        private const uint Register = 0, Remove = 1, QueryPosition = 2, SetPosition = 3, WindowPositionChanged = 9;
        private const uint BottomEdge = 3;
        internal const int PositionChanged = 1; // ABN_POSCHANGED
        internal const int FullscreenChanged = 2; // ABN_FULLSCREENAPP
        internal static readonly int CallbackMessage = (int)NativeDesktop.RegisterWindowMessage("WinNTDE.AppBar");
        private readonly IntPtr window;
        private readonly NotificationArea notifications;
        private IntPtr registeredTray;
        private NativeDesktop.Rect? requested, reserved;

        internal TaskbarAppBar(IntPtr window, NotificationArea notifications)
        {
            this.window = window;
            this.notifications = notifications;
        }

        internal NativeDesktop.Rect? Reserve(NativeDesktop.Rect preferred)
        {
            WithExplorerTray(tray =>
            {
                if (registeredTray != tray) ClearRegistration();
                if (tray == IntPtr.Zero || CallbackMessage == 0) return;

                var data = CreateData();
                if (registeredTray == IntPtr.Zero)
                {
                    if (SHAppBarMessage(Register, ref data) == UIntPtr.Zero) return;
                    registeredTray = tray;
                }

                data.Edge = BottomEdge;
                data.Bounds = preferred;
                if (SHAppBarMessage(QueryPosition, ref data) == UIntPtr.Zero)
                {
                    ReleaseRegistration(tray);
                    return;
                }
                data.Bounds.Top = data.Bounds.Bottom - preferred.Height;
                if (requested.HasValue && requested.Value.Equals(data.Bounds)) return;
                var proposed = data.Bounds;
                if (SHAppBarMessage(SetPosition, ref data) == UIntPtr.Zero)
                {
                    ReleaseRegistration(tray);
                    return;
                }
                requested = proposed;
                reserved = data.Bounds;
            });
            return reserved;
        }

        internal void NotifyPositionChanged()
        {
            if (registeredTray == IntPtr.Zero) return;
            WithExplorerTray(tray =>
            {
                if (tray != registeredTray) return;
                var data = CreateData();
                SHAppBarMessage(WindowPositionChanged, ref data);
            });
        }

        internal void Release()
        {
            if (registeredTray != IntPtr.Zero) WithExplorerTray(ReleaseRegistration);
        }

        private void ReleaseRegistration(IntPtr tray)
        {
            if (tray != IntPtr.Zero && registeredTray == tray)
            {
                var data = CreateData();
                SHAppBarMessage(Remove, ref data);
            }
            ClearRegistration();
        }

        private void ClearRegistration()
        {
            registeredTray = IntPtr.Zero;
            requested = reserved = null;
        }

        private void WithExplorerTray(Action<IntPtr> action)
        {
            if (notifications != null) notifications.WithExplorerTray(action);
            else action(NativeDesktop.FindWindow("Shell_TrayWnd", null));
        }

        private AppBarData CreateData()
        {
            return new AppBarData { Size = (uint)Marshal.SizeOf(typeof(AppBarData)), Window = window, Callback = (uint)CallbackMessage };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            internal uint Size;
            internal IntPtr Window;
            internal uint Callback, Edge;
            internal NativeDesktop.Rect Bounds;
            internal IntPtr Parameter;
        }

        [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);
    }
}
