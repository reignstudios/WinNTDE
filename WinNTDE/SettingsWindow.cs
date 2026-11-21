using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WinNTDE
{
    internal sealed class SettingsWindow : Window
    {
        private static readonly string[] Pages =
            { "Personalization", "Taskbar", "Display", "Audio", "Power", "Installed Apps", "Windows Update", "Wi-Fi", "Bluetooth", "About" };

        private readonly App app;
        private readonly ListBox pages = new ListBox();
        private readonly ScrollViewer body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private StackPanel content;
        private int pageVersion;

        internal SettingsWindow(App app)
        {
            this.app = app;
            Title = "Settings";
            Width = 820;
            Height = 600;
            MinWidth = 470;
            MinHeight = 290;
            NativeDesktop.ThemeWindow(this);
            var root = new DockPanel();
            Content = root;

            var sidebar = new DockPanel { Width = 162 };
            sidebar.SetResourceReference(BackgroundProperty, "Panel");
            DockPanel.SetDock(sidebar, Dock.Left);
            root.Children.Add(sidebar);
            var title = new TextBlock { Text = "Settings", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 16, 8, 12) };
            DockPanel.SetDock(title, Dock.Top);
            sidebar.Children.Add(title);
            pages.Margin = new Thickness(7, 0, 7, 8);
            pages.Background = Brushes.Transparent;
            var items = new Style(typeof(ListBoxItem));
            items.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 7, 8, 7)));
            pages.ItemContainerStyle = items;
            pages.ItemsSource = Pages;
            pages.SelectionChanged += (s, e) => ShowPage(pages.SelectedItem as string);
            sidebar.Children.Add(pages);
            root.Children.Add(body);
            Closed += (s, e) => pageVersion++;
            SelectPage("Personalization");
        }

        internal void SelectPage(string page)
        {
            pages.SelectedItem = page;
            pages.ScrollIntoView(page);
        }

        private void ShowPage(string page)
        {
            if (page == null) return;
            pageVersion++;
            content = new StackPanel { Margin = new Thickness(24, 17, 24, 20) };
            body.Content = content;
            body.ScrollToTop();
            AddText(page, 25, FontWeights.SemiBold).Margin = new Thickness(0, 0, 0, 17);

            switch (page)
            {
                case "Personalization": Personalization(); break;
                case "Taskbar": Taskbar(); break;
                case "Display": Display(); break;
                case "Audio": Audio(); break;
                case "Power": Power(); break;
                case "Installed Apps": InstalledApps(); break;
                case "Windows Update": WindowsUpdate(); break;
                case "Wi-Fi": Wifi(); break;
                case "Bluetooth": Bluetooth(); break;
                case "About": About(); break;
            }
        }

        private TextBlock AddText(string text, double size = 13, FontWeight? weight = null)
        {
            var label = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            content.Children.Add(label);
            return label;
        }

        private TextBlock AddStatus()
        {
            var label = AddText("");
            label.SetResourceReference(ForegroundProperty, "Muted");
            return label;
        }

        private static Button MakeButton(string name, Action action)
        {
            var button = new Button { Content = name, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 7, 8) };
            if (action != null) button.Click += (s, e) => action();
            return button;
        }

        private Button AddButton(string name, Action action = null)
        {
            var button = MakeButton(name, action);
            content.Children.Add(button);
            return button;
        }

        private WrapPanel AddActions()
        {
            var row = new WrapPanel();
            content.Children.Add(row);
            return row;
        }

        private ComboBox AddChoice(string name, IEnumerable options, object selected = null)
        {
            AddText(name, 13, FontWeights.SemiBold);
            var combo = new ComboBox { ItemsSource = options, SelectedItem = selected,
                MinWidth = 175, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(combo, name);
            content.Children.Add(combo);
            return combo;
        }

        private void Run<T>(Button trigger, TextBlock status, string working, Func<T> work, Action<T> done, Action finished = null)
        {
            int version = pageVersion;
            if (trigger != null) trigger.IsEnabled = false;
            status.Text = working;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                T result = default(T);
                string error = null;
                try { result = work(); }
                catch (Exception failure) when (failure is Win32Exception || failure is ExternalException ||
                    failure is IOException || failure is InvalidOperationException || failure is ArgumentException ||
                    failure is UnauthorizedAccessException || failure is System.Security.SecurityException ||
                    failure is ManagementException || failure is NotSupportedException || failure is TypeLoadException || failure is InvalidCastException ||
                    failure is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    error = failure is DllNotFoundException || failure is EntryPointNotFoundException ?
                        "This feature is unavailable on this Windows installation." : failure.Message;
                }
                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (version != pageVersion) return;
                    if (trigger != null) trigger.IsEnabled = true;
                    status.Text = error ?? "";
                    if (error == null) done(result);
                    if (finished != null) finished();
                }));
            });
        }

        private void Personalization()
        {
            var theme = AddChoice("Theme", new[] { "Dark", "Light" }, app.Preferences.LightTheme ? "Light" : "Dark");
            theme.SelectionChanged += (s, e) =>
            {
                app.Preferences.LightTheme = theme.SelectedIndex == 1;
                app.ApplyTheme();
                app.SavePreferences(this);
            };
            AddText("Desktop wallpaper", 13, FontWeights.SemiBold);
            var wallpaper = AddText(string.IsNullOrEmpty(app.Preferences.Wallpaper) ? "Solid black" : app.Preferences.Wallpaper);
            var actions = AddActions();
            actions.Children.Add(MakeButton("Choose image…", () =>
            {
                var picker = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ppm|All files|*.*" };
                if (picker.ShowDialog(this) != true) return;
                if (DesktopFiles.DecodeImage(picker.FileName, 64) == null)
                {
                    MessageBox.Show(this, "WinNTDE could not open that image.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                app.Preferences.Wallpaper = picker.FileName;
                wallpaper.Text = picker.FileName;
                app.SavePreferences(this);
            }));
            actions.Children.Add(MakeButton("Use solid black", () =>
            {
                app.Preferences.Wallpaper = null;
                wallpaper.Text = "Solid black";
                app.SavePreferences(this);
            }));
        }

        private void Taskbar()
        {
            var alignment = AddChoice("Icon alignment", new[] { "Left", "Center" }, app.Preferences.AlignLeft ? "Left" : "Center");
            alignment.SelectionChanged += (s, e) =>
            {
                app.Preferences.AlignLeft = alignment.SelectedIndex == 0;
                app.SavePreferences(this);
            };
            var autoHide = new CheckBox { Content = "Automatically hide the taskbar", IsChecked = app.Preferences.AutoHide,
                Margin = new Thickness(0, 4, 0, 10) };
            autoHide.Click += (s, e) => { app.Preferences.AutoHide = autoHide.IsChecked == true; app.SavePreferences(this); };
            content.Children.Add(autoHide);
        }

        private void Display()
        {
            DesktopFiles.Try(this, () =>
            {
                var current = SystemSettings.CurrentMode();
                var resolutions = SystemSettings.Resolutions(current);
                var landscape = SystemSettings.ToLandscape(current);
                var resolution = AddChoice("Primary display resolution", resolutions,
                    resolutions.FirstOrDefault(item => item.Width == landscape.Item1 && item.Height == landscape.Item2));
                var orientation = AddChoice("Orientation", new[] { "Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)" });
                orientation.SelectedIndex = Math.Max(0, Math.Min(3, current.Orientation));
                AddButton("Apply display mode", () => DesktopFiles.Try(this, () =>
                {
                    var selected = resolution.SelectedItem as SystemSettings.Resolution;
                    if (selected == null) return;
                    var previous = SystemSettings.CurrentMode();
                    var next = SystemSettings.RequestedMode(previous, selected, orientation.SelectedIndex);
                    if (next.Width == previous.Width && next.Height == previous.Height && next.Orientation == previous.Orientation) return;
                    SystemSettings.ChangeDisplay(next, 2);
                    SystemSettings.ChangeDisplay(next, 0);
                    bool committed = false;
                    try
                    {
                        if (ConfirmDisplay())
                        {
                            SystemSettings.ChangeDisplay(next, 1);
                            committed = true;
                        }
                    }
                    finally
                    {
                        if (!committed) SystemSettings.ChangeDisplay(previous, 0);
                        ShowPage("Display");
                    }
                }));
            });
        }

        private bool ConfirmDisplay()
        {
            var prompt = new Window { Title = "Keep these display settings?", Width = 365, Height = 158, Topmost = true,
                Owner = this, ResizeMode = ResizeMode.NoResize };
            NativeDesktop.ThemeWindow(prompt);
            var stack = new StackPanel { Margin = new Thickness(13) };
            var label = new TextBlock { Margin = new Thickness(0, 4, 0, 12) };
            stack.Children.Add(label);
            var buttons = new WrapPanel();
            var revert = MakeButton("Revert", () => prompt.DialogResult = false);
            revert.IsCancel = true;
            buttons.Children.Add(revert);
            buttons.Children.Add(MakeButton("Keep changes", () => prompt.DialogResult = true));
            stack.Children.Add(buttons);
            prompt.Content = stack;
            DateTime deadline = DateTime.UtcNow.AddSeconds(15);
            label.Text = "Reverting in 15 seconds.";
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) =>
            {
                int remaining = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
                if (remaining <= 0) prompt.DialogResult = false;
                else label.Text = "Reverting in " + remaining + " seconds.";
            };
            timer.Start();
            try { return prompt.ShowDialog() == true; }
            finally { timer.Stop(); }
        }

        private void Audio()
        {
            if (SystemSettings.WindowsVersion.Major >= 6)
            {
                foreach (bool input in new[] { false, true })
                {
                    var output = AddChoice(input ? "Default input device" : "Default output device", new object[0]);
                    var status = AddStatus();
                    Run(null, status, "Reading audio devices…", () => AudioDevices.GetDevices(input), devices =>
                    {
                        output.ItemsSource = devices;
                        output.SelectedItem = devices.FirstOrDefault(device => device.Default);
                        if (devices.Length == 0) status.Text = "No active devices.";
                        output.SelectionChanged += (s, e) =>
                        {
                            var device = output.SelectedItem as AudioDevices.Device;
                            if (device == null) return;
                            DesktopFiles.Try(this, () => AudioDevices.SetDefault(device.Id));
                            ShowPage("Audio");
                        };
                    });
                }
            }

            AddText("Output volume", 13, FontWeights.SemiBold);
            var volume = new Slider { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true,
                Width = 225, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6), IsEnabled = false };
            System.Windows.Automation.AutomationProperties.SetName(volume, "Output volume");
            content.Children.Add(volume);
            var apply = AddButton("Set volume", () => DesktopFiles.Try(this, () => AudioDevices.SetVolume((float)(volume.Value / 100))));
            apply.IsEnabled = false;
            var volumeStatus = AddStatus();
            Run(null, volumeStatus, "Reading output volume…", AudioDevices.Volume, value =>
            {
                volume.Value = Math.Round(value * 100);
                volume.IsEnabled = apply.IsEnabled = true;
            });
            AddButton("Windows sound settings", () => DesktopFiles.Try(this, () => SystemSettings.OpenControl("mmsys.cpl")));
        }

        private void Power()
        {
            AddText(SystemSettings.PowerSummary());
            if (SystemSettings.WindowsVersion.Major >= 6)
            {
                var choice = AddChoice("Power plan", new object[0]);
                var status = AddStatus();
                Run(null, status, "Reading power plans…", SystemSettings.PowerPlans, plans =>
                {
                    choice.ItemsSource = plans;
                    choice.SelectedItem = plans.FirstOrDefault(plan => plan.Active);
                    choice.SelectionChanged += (s, e) =>
                    {
                        var plan = choice.SelectedItem as SystemSettings.PowerPlan;
                        if (plan != null && !DesktopFiles.Try(this, () => SystemSettings.SetPowerPlan(plan.Id))) ShowPage("Power");
                    };
                });
            }
            AddButton("Windows power options", () => DesktopFiles.Try(this, () => SystemSettings.OpenControl("powercfg.cpl")));
            var actions = AddActions();
            foreach (var pair in new[] { new { Label = "Sleep", Option = "" }, new { Label = "Sign out", Option = "l" },
                new { Label = "Restart", Option = "r" }, new { Label = "Shut down", Option = "s" } })
            {
                actions.Children.Add(MakeButton(pair.Label, () =>
                {
                    if (MessageBox.Show(this, pair.Label + " now?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    DesktopFiles.Try(this, () => { if (pair.Option == "") SystemSettings.Sleep(); else SystemSettings.EndSession(pair.Option); });
                }));
            }
        }

        private void InstalledApps()
        {
            var filter = new TextBox { Margin = new Thickness(0, 0, 0, 8), MaxWidth = 350, HorizontalAlignment = HorizontalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(filter, "Search installed apps");
            AddText("Search installed apps");
            content.Children.Add(filter);
            var view = new GridView();
            view.Columns.Add(new GridViewColumn { Header = "Name", Width = 225, DisplayMemberBinding = new Binding("Name") });
            view.Columns.Add(new GridViewColumn { Header = "Version", Width = 105, DisplayMemberBinding = new Binding("Version") });
            view.Columns.Add(new GridViewColumn { Header = "Publisher", Width = 165, DisplayMemberBinding = new Binding("Publisher") });
            var list = new ListView { View = view, Height = 240, Margin = new Thickness(0, 0, 0, 10) };
            content.Children.Add(list);
            var actions = AddActions();
            var status = AddStatus();
            var uninstall = MakeButton("Uninstall…", null);
            uninstall.IsEnabled = false;
            actions.Children.Add(uninstall);
            var refresh = MakeButton("Refresh", null);
            actions.Children.Add(refresh);
            actions.Children.Add(MakeButton("Windows installed apps", () => DesktopFiles.Try(this, () =>
            {
                if (SystemSettings.WindowsVersion.Major >= 10) Process.Start("ms-settings:appsfeatures");
                else SystemSettings.OpenControl("appwiz.cpl");
            })));
            var entries = new SystemSettings.InstalledApp[0];
            Action applyFilter = () =>
            {
                string value = filter.Text.Trim();
                list.ItemsSource = entries.Where(item => item.Name.IndexOf(value, StringComparison.CurrentCultureIgnoreCase) >= 0).ToArray();
            };
            Action refreshApps = () => Run(refresh, status, "Reading installed apps…", SystemSettings.InstalledApps, apps =>
            {
                entries = apps;
                applyFilter();
                status.Text = apps.Length + " installed apps";
            });
            filter.TextChanged += (s, e) => applyFilter();
            list.SelectionChanged += (s, e) =>
            {
                var item = list.SelectedItem as SystemSettings.InstalledApp;
                uninstall.IsEnabled = item != null && !string.IsNullOrWhiteSpace(item.Command);
            };
            uninstall.Click += (s, e) =>
            {
                var item = list.SelectedItem as SystemSettings.InstalledApp;
                if (item == null || MessageBox.Show(this, "Start the uninstaller for " + item.Name + "?", Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                DesktopFiles.Try(this, () =>
                {
                    using (var process = SystemSettings.LaunchCommand(item.Command))
                    {
                        status.Text = "Uninstaller started. Refresh after it finishes.";
                    }
                });
            };
            refresh.Click += (s, e) => refreshApps();
            refreshApps();
        }

        private void WindowsUpdate()
        {
            var scan = AddButton("Check for updates");
            var status = AddStatus();
            scan.Click += (s, e) => Run(scan, status, "Checking for updates…", SystemSettings.CheckForUpdates,
                count => status.Text = count == 0 ? "No applicable updates found." : count + (count == 1 ? " update available." : " updates available."));
        }

        private void Wifi()
        {
            var list = new ListBox { Height = 195, Margin = new Thickness(0, 0, 0, 10) };
            content.Children.Add(list);
            var actions = AddActions();
            var connect = MakeButton("Connect", null);
            var disconnect = MakeButton("Disconnect", null);
            var scan = MakeButton("Scan for networks", null);
            actions.Children.Add(connect);
            actions.Children.Add(disconnect);
            actions.Children.Add(scan);
            actions.Children.Add(MakeButton("Windows network settings", () => DesktopFiles.Try(this, () =>
            {
                if (SystemSettings.WindowsVersion.Major >= 10) Process.Start("ms-settings:network-wifi");
                else SystemSettings.OpenControl("ncpa.cpl");
            })));
            var status = AddStatus();
            bool busy = false;
            Action updateButtons = () =>
            {
                var network = list.SelectedItem as Wireless.Network;
                connect.IsEnabled = !busy && network != null && !network.Connected && network.Connectable &&
                    (!string.IsNullOrEmpty(network.Profile) || (network.CanCreateProfile && network.BssType == 1));
                disconnect.IsEnabled = !busy && network != null && network.Connected;
            };
            list.SelectionChanged += (s, e) => updateButtons();
            Action<bool> setBusy = value => { busy = value; updateButtons(); };
            Action<bool> refresh = shouldScan =>
            {
                setBusy(true);
                Run(scan, status, "Reading Wi-Fi networks…", () => Wireless.Networks(shouldScan), networks =>
                {
                    list.ItemsSource = networks;
                    if (networks.Length == 0) status.Text = "No Wi-Fi networks found.";
                }, () => setBusy(false));
            };
            connect.Click += (s, e) =>
            {
                var network = list.SelectedItem as Wireless.Network;
                if (network == null) return;
                string password = network.NeedsPassword ? EnterPassword(network.Name) : "";
                if (password == null) return;
                RunWifiChange(() => Wireless.Connect(network, password), true, list, status, scan, setBusy);
            };
            disconnect.Click += (s, e) =>
            {
                var network = list.SelectedItem as Wireless.Network;
                if (network == null) return;
                RunWifiChange(() => Wireless.Disconnect(network), false, list, status, scan, setBusy);
            };
            scan.Click += (s, e) => refresh(true);
            refresh(false);
        }

        private void RunWifiChange(Action change, bool connecting, ListBox list, TextBlock status, Button scan, Action<bool> setBusy)
        {
            var target = (Wireless.Network)list.SelectedItem;
            setBusy(true);
            Run(scan, status, connecting ? "Connecting…" : "Disconnecting…", () =>
            {
                change();
                Wireless.Network[] networks = null;
                for (int i = 0; i < 6; i++)
                {
                    Thread.Sleep(i == 0 ? 400 : 900);
                    networks = Wireless.Networks(false);
                    bool connected = networks.Any(network => network.Adapter == target.Adapter && network.Name == target.Name && network.Connected);
                    if (connected == connecting)
                        return Tuple.Create(networks, connected ? "Connected to " + target.Name + "." : "Disconnected from " + target.Name + ".");
                }
                return Tuple.Create(networks, "Windows has not confirmed the connection change.");
            }, result =>
            {
                list.ItemsSource = result.Item1;
                list.SelectedItem = result.Item1.FirstOrDefault(network => network.Adapter == target.Adapter && network.Name == target.Name);
                status.Text = result.Item2;
            }, () => setBusy(false));
        }

        private string EnterPassword(string name)
        {
            var prompt = new Window { Title = "Connect to Wi-Fi", Owner = this, Width = 365, Height = 174, ResizeMode = ResizeMode.NoResize };
            NativeDesktop.ThemeWindow(prompt);
            var stack = new StackPanel { Margin = new Thickness(13) };
            stack.Children.Add(new TextBlock { Text = "Password for " + name, Margin = new Thickness(0, 0, 0, 6), TextTrimming = TextTrimming.CharacterEllipsis });
            var password = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 7) };
            System.Windows.Automation.AutomationProperties.SetName(password, "Wi-Fi password");
            stack.Children.Add(password);
            var connect = MakeButton("Connect", () => prompt.DialogResult = true);
            connect.IsDefault = true;
            stack.Children.Add(connect);
            prompt.Content = stack;
            prompt.Loaded += (s, e) => password.Focus();
            return prompt.ShowDialog() == true ? password.Password : null;
        }

        private void Bluetooth()
        {
            var list = new ListBox { Height = 200, Margin = new Thickness(0, 0, 0, 10) };
            content.Children.Add(list);
            var row = AddActions();
            var scan = MakeButton("Scan nearby devices", null);
            row.Children.Add(scan);
            row.Children.Add(MakeButton("Pair or manage devices in Windows", () => DesktopFiles.Try(this, () =>
            {
                if (SystemSettings.WindowsVersion.Major >= 10) Process.Start("ms-settings:bluetooth");
                else if (SystemSettings.WindowsVersion >= new Version(6, 1)) SystemSettings.OpenControl("/name Microsoft.DevicesAndPrinters");
                else SystemSettings.OpenControl("bthprops.cpl");
            })));
            var status = AddStatus();
            Action<bool> refresh = inquiry => Run(scan, status, inquiry ? "Scanning nearby devices…" : "Reading Bluetooth devices…",
                () => Wireless.BluetoothDevices(inquiry), devices =>
                {
                    list.ItemsSource = devices;
                    if (devices.Length == 0) status.Text = "No Bluetooth devices found.";
                });
            scan.Click += (s, e) => refresh(true);
            refresh(false);
        }

        private void About()
        {
            AddText("WinNTDE " + typeof(App).Assembly.GetName().Version.ToString(2), 17, FontWeights.SemiBold);
            AddText(app.IsShell ? "Desktop shell" : "Preview alongside Windows Explorer");
            var status = AddStatus();
            Run(null, status, "Reading system information…", SystemSettings.SystemSummary, summary => status.Text = summary);
        }
    }
}
