using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNTDE
{
    internal sealed class FileEntry : INotifyPropertyChanged
    {
        private readonly bool thumbnails;
        private ImageSource icon;
        private bool iconRequested;

        internal FileEntry(FileSystemInfo info, bool thumbnails, bool details)
        {
            Path = info.FullName;
            IsFolder = info is DirectoryInfo;
            Name = info.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !IsFolder
                ? System.IO.Path.GetFileNameWithoutExtension(info.Name) : info.Name;
            this.thumbnails = thumbnails;
            if (!details) return;
            Kind = IsFolder ? "Folder" : string.IsNullOrEmpty(info.Extension) ? "File" :
                info.Extension.Substring(1).ToUpperInvariant() + " file";
            try
            {
                Modified = info.LastWriteTime.ToString("g");
                if (!IsFolder) Size = (((FileInfo)info).Length / 1024.0).ToString("N1") + " KB";
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal FileEntry(string name, string path, bool isFolder, string kind = null, string modified = null, string size = null)
        {
            Name = name;
            Path = path;
            IsFolder = isFolder;
            Kind = kind;
            Modified = modified;
            Size = size;
        }

        public string Name { get; private set; }
        public string Path { get; private set; }
        public string Kind { get; private set; }
        public string Modified { get; private set; }
        public string Size { get; private set; }
        internal bool IsFolder { get; private set; }

        public ImageSource Icon
        {
            get
            {
                if (iconRequested) return icon;
                iconRequested = true;
                icon = DesktopFiles.FileIcon(Path, IsFolder);
                if (thumbnails && !IsFolder && DesktopFiles.IsImage(Path))
                {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        var preview = DesktopFiles.DecodeImage(Path, 80);
                        if (preview == null || dispatcher.HasShutdownStarted) return;
                        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                        {
                            icon = preview;
                            if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Icon"));
                        }));
                    });
                }
                return icon;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    internal static class DesktopFiles
    {
        private const string DropEffect = "Preferred DropEffect";
        private static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        internal static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.Personal); } }
        internal static ImageSource FolderIcon { get { return FileIcon("Folder", true); } }

        internal static string SharedDesktop
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory); }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfoNative
        {
            internal IntPtr Icon;
            internal int IconIndex;
            internal uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileOperation
        {
            internal IntPtr Owner;
            internal uint Operation;
            internal string Source;
            internal string Destination;
            internal ushort Flags;
            [MarshalAs(UnmanagedType.Bool)] internal bool Aborted;
            internal IntPtr NameMappings;
            internal string ProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string path, uint attributes, out FileInfoNative info, uint size, uint flags);
        [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW")]
        private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, out FileInfoNative info, uint size, uint flags);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint mask, out uint attributes);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref FileOperation operation);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string source, string destination, uint flags);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

        internal static IEnumerable<FileEntry> ReadFolder(string path, bool thumbnails, bool details = false)
        {
            return new DirectoryInfo(path).EnumerateFileSystemInfos()
                .Where(info => (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(info => new FileEntry(info, thumbnails, details));
        }

        internal static ImageSource FileIcon(string path, bool isFolder)
        {
            string extension = System.IO.Path.GetExtension(path);
            bool generic = isFolder || !(new[] { ".exe", ".lnk", ".ico", ".url", ".appref-ms" }).Contains(extension, StringComparer.OrdinalIgnoreCase);
            string key = isFolder ? "<folder>" : extension;
            ImageSource image;
            if (generic && icons.TryGetValue(key, out image)) return image;

            const uint Icon = 0x100, UseAttributes = 0x10;
            FileInfoNative info;
            SHGetFileInfo(isFolder ? "Folder" : path, isFolder ? 0x10u : 0x80u, out info,
                (uint)Marshal.SizeOf(typeof(FileInfoNative)), Icon | (generic ? UseAttributes : 0));
            image = ReadIcon(info.Icon);
            if (generic && icons.Count < 256) icons[key] = image;
            return image;
        }

        internal static ImageSource NamespaceIcon(string name)
        {
            IntPtr pidl;
            uint attributes;
            if (SHParseDisplayName(name, IntPtr.Zero, out pidl, 0, out attributes) < 0 || pidl == IntPtr.Zero) return null;
            try
            {
                FileInfoNative info;
                SHGetFileInfo(pidl, 0, out info, (uint)Marshal.SizeOf(typeof(FileInfoNative)), 0x108);
                return ReadIcon(info.Icon);
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }

        private static ImageSource ReadIcon(IntPtr icon)
        {
            try { return NativeDesktop.IconImage(icon, 32); }
            finally { if (icon != IntPtr.Zero) NativeDesktop.DestroyIcon(icon); }
        }

        internal static ListBoxItem ItemAt(object source)
        {
            var visual = source as Visual;
            while (visual != null && !(visual is ListBoxItem))
                visual = VisualTreeHelper.GetParent(visual) as Visual;
            return visual as ListBoxItem;
        }

        internal static void Open(FileEntry entry)
        {
            OpenPath(entry.Path, entry.IsFolder);
        }

        internal static void OpenPath(string path, bool isFolder = false)
        {
            string folder = isFolder ? path : LinkedFolder(path);
            if (folder != null) ((App)Application.Current).OpenExplorer(folder);
            else Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path) });
        }

        private static string LinkedFolder(string path)
        {
            if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return null;
            object link = null;
            try
            {
                link = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true));
                ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
                var buffer = new StringBuilder(260);
                ((IShellLink)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
                string target = buffer.ToString();
                return Directory.Exists(target) ? target : null;
            }
            catch (COMException) { return null; }
            finally { SystemSettings.Release(link); }
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLink
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, IntPtr findData, uint flags);
        }

        internal static bool Try(Window owner, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is Win32Exception || error is ExternalException || error is ArgumentException ||
                error is InvalidOperationException || error is NotSupportedException || error is InvalidCastException ||
                error is System.Security.SecurityException)
            {
                MessageBox.Show(owner, error.Message, owner.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        internal static void PutOnClipboard(IEnumerable<FileEntry> items, bool move)
        {
            var files = new StringCollection();
            files.AddRange(items.Select(item => item.Path).ToArray());
            if (files.Count == 0) return;
            var data = new DataObject();
            data.SetFileDropList(files);
            byte[] effect = BitConverter.GetBytes(move ? 2 : 5);
            data.SetData(DropEffect, new MemoryStream(effect), false);
            Clipboard.SetDataObject(data, true);
        }

        internal static void Paste(Window owner, string destination)
        {
            uint sequence = GetClipboardSequenceNumber();
            IDataObject clipboard = Clipboard.GetDataObject();
            var paths = clipboard == null ? null : clipboard.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            var stream = clipboard.GetData(DropEffect, false) as MemoryStream;
            byte[] effect = stream == null ? new byte[0] : stream.ToArray();
            bool move = effect.Length >= 4 && (BitConverter.ToInt32(effect, 0) & 2) != 0;
            if (Transfer(new System.Windows.Interop.WindowInteropHelper(owner).Handle, paths, destination, move) &&
                move && sequence == GetClipboardSequenceNumber()) Clipboard.Clear();
        }

        private static bool Transfer(IntPtr owner, string[] paths, string destination, bool move)
        {
            string folder = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar);
            string[] fullPaths = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string fullPath in fullPaths)
            {
                string source = fullPath.TrimEnd(Path.DirectorySeparatorChar);
                if (folder.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                    folder.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("A folder cannot be pasted into itself or one of its subfolders.");
            }

            // A copy into the source folder receives a new name. Other collisions use the system prompt.
            var sources = fullPaths.Where(path => NeedsTransfer(path, destination, move)).ToArray();
            if (sources.Length == 0) return true;
            bool sameFolder = !move && sources.Any(path => string.Equals(Path.GetDirectoryName(path), destination,
                StringComparison.OrdinalIgnoreCase));
            var operation = new FileOperation
            {
                Owner = owner,
                Operation = move ? 1u : 2u,
                Source = string.Join("\0", sources) + "\0\0",
                Destination = destination + "\0\0",
                Flags = (ushort)(sameFolder ? 8 : 0)
            };
            return RunFileOperation(ref operation);
        }

        internal static void Delete(Window owner, string[] paths, bool recycle = false)
        {
            if (paths.Length == 0) return;
            const ushort NoConfirmation = 0x10, AllowUndo = 0x40, WarnIfPermanent = 0x4000;
            var operation = new FileOperation
            {
                Owner = new System.Windows.Interop.WindowInteropHelper(owner).Handle,
                Operation = 3,
                Source = string.Join("\0", paths.Select(Path.GetFullPath)) + "\0\0",
                Flags = recycle ? (ushort)(NoConfirmation | AllowUndo | WarnIfPermanent) : NoConfirmation
            };
            RunFileOperation(ref operation);
        }

        internal static string Rename(FileEntry item, string name)
        {
            if (string.Equals(Path.GetFileName(item.Path), name, StringComparison.Ordinal)) return item.Path;
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.EndsWith(" ", StringComparison.Ordinal) || name.EndsWith(".", StringComparison.Ordinal))
                throw new ArgumentException("Enter a valid name with no trailing spaces or periods.");
            if (Regex.IsMatch(name, @"^ *(?:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|(?:COM|LPT)[1-9¹²³]) *(?:\.|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new ArgumentException("That name is reserved by Windows.");

            string destination = Path.Combine(Path.GetDirectoryName(item.Path), name);
            if (!MoveFileEx(item.Path, destination, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 80 || error == 183) throw new IOException("A file or folder with that name already exists.");
                throw new Win32Exception(error);
            }
            return destination;
        }

        private static bool RunFileOperation(ref FileOperation operation)
        {
            int result = SHFileOperation(ref operation);
            if (result != 0 && !operation.Aborted)
                throw new IOException("Windows could not complete the file operation (0x" + result.ToString("X") + ").");
            return !operation.Aborted && result == 0;
        }

        private static bool NeedsTransfer(string path, string destination, bool move)
        {
            if (move && string.Equals(Path.GetDirectoryName(path), destination, StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(path) || Directory.Exists(path)) return true;
            string target = Path.Combine(destination, Path.GetFileName(path));
            if (move && (File.Exists(target) || Directory.Exists(target))) return false;
            throw new FileNotFoundException("The clipboard item is no longer available.", path);
        }

        internal static bool IsImage(string path)
        {
            return new[] { ".png", ".jpg", ".jpeg", ".ppm", ".bmp", ".gif", ".tif", ".tiff", ".ico" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }

        internal static BitmapSource DecodeImage(string path, int width)
        {
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    BitmapSource result;
                    if (Path.GetExtension(path).Equals(".ppm", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var buffer = new BufferedStream(stream)) result = ReadPpm(buffer, width);
                    }
                    else
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = width;
                        bitmap.StreamSource = stream;
                        bitmap.EndInit();
                        result = bitmap;
                    }
                    result.Freeze();
                    return result;
                }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                error is NotSupportedException || error is ArgumentException || error is FormatException ||
                error is OverflowException || error is InvalidOperationException)
            {
                return null;
            }
        }

        private static BitmapSource ReadPpm(Stream stream, int maxEdge)
        {
            string format = PpmToken(stream);
            if (format != "P3" && format != "P6") throw new FormatException();
            int width = PpmNumber(stream), height = PpmNumber(stream), maximum = PpmNumber(stream);
            if (width < 1 || height < 1 || maximum < 1 || maximum > 65535 || (long)width * height > 32000000)
                throw new FormatException();

            int step = (Math.Max(width, height) - 1) / maxEdge + 1;
            int outWidth = (width - 1) / step + 1, outHeight = (height - 1) / step + 1;
            var pixels = new byte[outWidth * outHeight * 3];
            // Consume each input sample in order, keeping only the pixels used in the preview.
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            for (int channel = 0; channel < 3; channel++)
            {
                int value = format == "P3" ? PpmNumber(stream) : PpmByte(stream);
                if (format == "P6" && maximum > 255) value = (value << 8) | PpmByte(stream);
                if (value < 0 || value > maximum) throw new FormatException();
                if (x % step == 0 && y % step == 0)
                    pixels[(y / step * outWidth + x / step) * 3 + channel] = (byte)((value * 255 + maximum / 2) / maximum);
            }
            return BitmapSource.Create(outWidth, outHeight, 96, 96, PixelFormats.Rgb24, null, pixels, outWidth * 3);
        }

        private static int PpmByte(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException();
            return value;
        }

        private static int PpmNumber(Stream stream)
        {
            return int.Parse(PpmToken(stream), NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private static string PpmToken(Stream stream)
        {
            int character = PpmByte(stream);
            while (char.IsWhiteSpace((char)character) || character == '#')
            {
                if (character == '#')
                    do { character = PpmByte(stream); } while (character != '\n' && character != '\r');
                character = PpmByte(stream);
            }
            var token = new StringBuilder();
            do
            {
                if (token.Length == 12) throw new FormatException();
                token.Append((char)character);
                character = stream.ReadByte();
            } while (character >= 0 && character != '#' && !char.IsWhiteSpace((char)character));
            if (character == '#') stream.Position--;
            if (character == '\r' && stream.ReadByte() != '\n' && stream.Position < stream.Length) stream.Position--;
            return token.ToString();
        }
    }
}
