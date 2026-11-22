using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinNTDE
{
    internal static class NativeDesktop
    {
        internal const int DisplayChanged = 0x007E;
        internal const int HotKey = 0x0312;
        internal const int SettingChanged = 0x001A;
        internal const int SetWorkAreaAction = 0x002F;
        private const int Caption = 0x00C00000, Layered = 0x00080000, Transparent = 0x0020;
        private const int ToolWindow = 0x0080, AppWindow = 0x00040000, NoActivate = 0x08000000;
        internal static readonly IntPtr Broadcast = new IntPtr(0xFFFF);
        internal static readonly IntPtr Bottom = new IntPtr(1);
        internal static readonly IntPtr Topmost = new IntPtr(-1);
        internal static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;
        private static readonly bool SupportsCaptionColors = Environment.OSVersion.Version >= new Version(10, 0, 22000);
        private static readonly bool SupportsDwm = Environment.OSVersion.Version.Major >= 6;
        private static readonly bool SupportsCloaking = Environment.OSVersion.Version >= new Version(6, 2);
        private static readonly Lazy<BitmapSource> DefaultAppIcon = new Lazy<BitmapSource>(
            () => IconImage(LoadIcon(IntPtr.Zero, new IntPtr(32512)), 32));

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
            internal int Width { get { return Right - Left; } }
            internal int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            internal int X, Y;
        }

        internal delegate bool EnumWindow(IntPtr hwnd, IntPtr data);

        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindow callback, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern bool SetShellWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetLastActivePopup(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect bounds);
        [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool SendNotifyMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out int process);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr hwnd, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll", EntryPoint = "GetClassLongW")] private static extern uint GetClassLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLong64(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int size);
        [DllImport("user32.dll")] internal static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint value, ref Rect rect, uint flags);
        [DllImport("user32.dll")] internal static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
        [DllImport("user32.dll")] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr resource);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

        internal static Rect Screen { get { return new Rect { Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) }; } }

        internal static Rect WorkArea
        {
            get
            {
                Rect bounds = Screen;
                SystemParametersInfo(0x0030, 0, ref bounds, 0);
                return bounds;
            }
        }

        internal static bool SetWorkArea(Rect bounds)
        {
            // SPIF_SENDCHANGE notifies maximized windows without persisting the session work area.
            return SystemParametersInfo(SetWorkAreaAction, 0, ref bounds, 2);
        }

        internal static void RegisterShell(IntPtr hwnd)
        {
            try { SetShellWindow(hwnd); }
            catch (EntryPointNotFoundException) { }
        }

        internal static bool IsApplicationWindow(IntPtr hwnd)
        {
            return IsWindowVisible(hwnd) && HasTaskButton(hwnd, GetWindowLong(hwnd, -20)) && !IsCloaked(hwnd);
        }

        private static bool HasTaskButton(IntPtr hwnd, int style)
        {
            return (style & AppWindow) != 0 || ((style & (ToolWindow | NoActivate)) == 0 && GetWindow(hwnd, 4) == IntPtr.Zero);
        }

        private static bool IsCloaked(IntPtr hwnd)
        {
            int cloaked;
            return SupportsCloaking && DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0;
        }

        internal static IntPtr FindFullscreenWindow(IntPtr taskbar, IntPtr desktop, IntPtr foreground)
        {
            IntPtr shellDesktop = GetShellWindow();
            if (foreground == IntPtr.Zero || foreground == taskbar || foreground == desktop || foreground == shellDesktop)
                return IntPtr.Zero;

            var screen = Screen;
            const uint RootOwner = 3; // GA_ROOTOWNER
            IntPtr root = GetAncestor(foreground, RootOwner);
            if (root == IntPtr.Zero) root = foreground;
            int process = WindowProcess(foreground);
            IntPtr fullscreen = IntPtr.Zero;
            // The active app can display an owned video popup without handing it focus.
            EnumWindows((window, data) =>
            {
                if (window == desktop || window == shellDesktop) return false;
                if (window != taskbar && IsWindowVisible(window) && (window == foreground || GetAncestor(window, RootOwner) == root ||
                    WindowProcess(window) == process) && IsFullscreen(window, screen))
                {
                    fullscreen = window;
                    return false;
                }
                return window != root;
            }, IntPtr.Zero);
            return fullscreen;
        }

        private static bool IsFullscreen(IntPtr window, Rect screen)
        {
            Rect bounds;
            if (IsIconic(window) || !GetWindowRect(window, out bounds) ||
                !CoversScreen(bounds, screen) || IsCloaked(window) || GetProp(window, "NonRudeHWND") != IntPtr.Zero ||
                (IsZoomed(window) && (GetWindowLong(window, -16) & Caption) != 0)) return false;

            int style = GetWindowLong(window, -20);
            if ((style & Layered) != 0)
            {
                if ((style & Transparent) != 0) return false;
                uint key, flags;
                byte alpha;
                if (GetLayeredWindowAttributes(window, out key, out alpha, out flags) && (flags & 2) != 0 && alpha == 0)
                    return false;
            }

            Rect frame;
            if (SupportsDwm && DwmGetWindowAttribute(window, 9, out frame, Marshal.SizeOf(typeof(Rect))) == 0 &&
                frame.Width > 0 && frame.Height > 0) bounds = frame;
            if (!CoversScreen(bounds, screen)) return false;
            var name = new StringBuilder(32);
            GetClassName(window, name, name.Capacity);
            string windowClass = name.ToString();
            return windowClass != "WorkerW" && windowClass != "Progman";
        }

        private static bool CoversScreen(Rect bounds, Rect screen)
        {
            return bounds.Left <= screen.Left && bounds.Top <= screen.Top && bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom;
        }

        internal static int WindowProcess(IntPtr hwnd)
        {
            int process;
            GetWindowThreadProcessId(hwnd, out process);
            return process;
        }

        internal static string WindowTitle(IntPtr hwnd)
        {
            var text = new StringBuilder(512);
            GetWindowText(hwnd, text, text.Capacity);
            return text.ToString();
        }

        internal static ImageSource WindowIcon(IntPtr hwnd)
        {
            IntPtr icon;
            SendMessageTimeout(hwnd, 0x007F, new IntPtr(2), IntPtr.Zero, 0x0002, 35, out icon);
            if (icon == IntPtr.Zero) icon = ClassIcon(hwnd, -34);
            if (icon == IntPtr.Zero) icon = ClassIcon(hwnd, -14);
            if (icon == IntPtr.Zero && WindowProcess(hwnd) == ProcessId)
            {
                var window = HwndSource.FromHwnd(hwnd);
                if (window != null && window.RootVisual is ExplorerWindow) return DesktopFiles.FolderIcon;
            }
            return icon == IntPtr.Zero ? ApplicationIcon : IconImage(icon, 24);
        }

        internal static ImageSource ApplicationIcon { get { return DefaultAppIcon.Value; } }

        private static IntPtr ClassIcon(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8 ? GetClassLong64(hwnd, index) : new IntPtr(unchecked((int)GetClassLong32(hwnd, index)));
        }

        internal static BitmapSource IconImage(IntPtr icon, int size)
        {
            if (icon == IntPtr.Zero) return null;
            IntPtr copy = CopyIcon(icon);
            if (copy == IntPtr.Zero) return null;
            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(copy, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(size, size));
                source.Freeze();
                return source;
            }
            catch (ArgumentException) { return null; }
            finally { DestroyIcon(copy); }
        }

        internal static void PlaceDesktop(IntPtr hwnd)
        {
            Rect screen = Screen;
            SetWindowPos(hwnd, Bottom, screen.Left, screen.Top, screen.Width, screen.Height, 0x0010);
        }

        internal static void ThemeWindow(Window window)
        {
            window.SetResourceReference(FrameworkElement.StyleProperty, "ShellWindow");
            window.SourceInitialized += (s, e) => UpdateTitleBar(window, ((App)Application.Current).Preferences.LightTheme);
            Rect work = WorkArea;
            window.MaxWidth = work.Width / ChromeScale;
            window.MaxHeight = work.Height / ChromeScale;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        internal static void UpdateTitleBar(Window window, bool light)
        {
            if (!SupportsCaptionColors || window.WindowStyle == WindowStyle.None) return;
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            const int ImmersiveDarkMode = 20, CaptionColor = 35, TextColor = 36;
            const uint DefaultColor = 0xFFFFFFFF;
            uint dark = light ? 0u : 1u;
            uint caption = light ? DefaultColor : ToColorRef((SolidColorBrush)window.FindResource("Surface"));
            uint text = light ? DefaultColor : ToColorRef((SolidColorBrush)window.FindResource("Text"));
            DwmSetWindowAttribute(hwnd, ImmersiveDarkMode, ref dark, sizeof(uint));
            DwmSetWindowAttribute(hwnd, CaptionColor, ref caption, sizeof(uint));
            DwmSetWindowAttribute(hwnd, TextColor, ref text, sizeof(uint));
        }

        private static uint ToColorRef(SolidColorBrush brush)
        {
            Color color = brush.Color;
            return (uint)(color.R | (color.G << 8) | (color.B << 16));
        }

        internal static double ChromeScale
        {
            get
            {
                var app = Application.Current;
                var source = app.MainWindow == null ? null : PresentationSource.FromVisual(app.MainWindow);
                return source == null || source.CompositionTarget == null ? 1 : source.CompositionTarget.TransformToDevice.M11;
            }
        }
    }
}
