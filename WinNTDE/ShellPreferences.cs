using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace WinNTDE
{
    internal sealed class ShellPreferences
    {
        private readonly string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinNTDE", "preferences.xml");

        internal string Wallpaper { get; set; }
        internal bool LightTheme { get; set; }
        internal bool AlignLeft { get; set; }
        internal bool AutoHide { get; set; }
        internal bool Thumbnails { get; set; }
        internal bool Details { get; set; }
        internal event EventHandler Changed;

        internal ShellPreferences()
        {
            Details = true;
            try
            {
                if (!File.Exists(file)) return;
                var values = XElement.Load(file);
                Wallpaper = (string)values.Element("Wallpaper");
                LightTheme = ReadBool(values, "LightTheme", false);
                AlignLeft = ReadBool(values, "AlignLeft", false);
                AutoHide = ReadBool(values, "AutoHide", false);
                Thumbnails = ReadBool(values, "Thumbnails", false);
                Details = ReadBool(values, "Details", true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (XmlException) { }
        }

        private static bool ReadBool(XElement values, string name, bool fallback)
        {
            bool value;
            return bool.TryParse((string)values.Element(name), out value) ? value : fallback;
        }

        internal void Save()
        {
            if (Changed != null) Changed(this, EventArgs.Empty);
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            string pending = file + ".tmp";
            new XElement("Preferences", new XElement("Wallpaper", Wallpaper ?? ""), new XElement("LightTheme", LightTheme),
                new XElement("AlignLeft", AlignLeft), new XElement("AutoHide", AutoHide),
                new XElement("Thumbnails", Thumbnails), new XElement("Details", Details)).Save(pending);
            if (File.Exists(file)) File.Replace(pending, file, null);
            else File.Move(pending, file);
        }
    }
}
