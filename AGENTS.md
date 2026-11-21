# Windows NT Desktop Environment (WinNTDE) overview
- WinNTDE is a WPF .NET Framework 4.0 Desktop environment that replaces explorer.exe
- All windows are done in a single WPF app process, file-explorer, settings etc
- Only special custom utilities are external from the main WPF process
- Single WPF app process writen in C# and .NET Framework 4.0 supporting WinXP-Win11

# WinNTDE Explorer Window
- App is a fullscreen background Window with Desktop Icon support.
- Main background Window responds to screen resolution changes and always fills display
- Background Window supports Wallpaper in settings Window but defaults to black

# WinNTDE Taskbar Window
- Task bar is a WPF Window that overlays other non-fullscreen Windows
- Feature: Win11 like just showing app icon
- Feature: Time shows in right corner
- Feature: Tray icons show in popup like Win11 taskbar

# WinNTDE File Explorer Window
- File-Explorer is a WPF Window
- Feature: copy/paste/cut
- Feature: folders/files viewing and navigation
- Feature: Windows user folders to the left
- Feature: view options, optional thumnails with .png, .jpg, .ppm support ect
- Feature: Loads 3rd party context menus in right-click

# WinNTDE Settings Window
- Settings is a WPF Window
- Feature: WPF supports Light and Dark themes in settings but defaults to Dark
- Feature: Display resolution and orientation settings
- Feature: Audio input/output device settings
- Feature: Power settings
- Feature: About page/system-info
- Feature: Installed Apps page that lets you uninstall apps
- Feature: Windows update page to trigger system update check
- Feature: WiFi settings page
- Feature: BT settings page
- Feature: Taskbar settings for icon alignment to left or center (defaults to center) and auto-hide option