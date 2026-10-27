// Capri Desktop Agent — extra hands: screenshots with numbered marks, files & code, running commands.
// C# 5 / .NET Framework 4.x. Everything here is called from Computer.Do on the job thread.
// Safety: files only inside the user's profile (never AppData, .ssh or .git internals), no deleting at all,
// overwriting / moving / running always needs the user's OK (see Computer.Risk).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace CapriDesktop
{
    /// <summary>Screenshot of the window in front with every readable target boxed and numbered (set-of-marks).</summary>
    internal static class AgentShot
    {
        internal const int MaxWidth = 1280;
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        /// <summary>Keeps Capri's own windows out of the screenshots she analyses (Windows 10 2004+). Ignored elsewhere.</summary>
        internal static void HideFromCapture(IEnumerable<IntPtr> windows, bool hide)
        {
            foreach (IntPtr w in windows)
                if (w != IntPtr.Zero)
                    try { SetWindowDisplayAffinity(w, hide ? 0x11u : 0u); } catch (Exception) { }
        }

        internal static byte[] Take(Sight sight)
        {
            if (sight == null || sight.Window == IntPtr.Zero) return null;
            Rectangle area = Rectangle.Intersect(sight.Bounds, System.Windows.Forms.SystemInformation.VirtualScreen);
            if (area.Width < 50 || area.Height < 50) return null;
            try
            {
                using (Bitmap raw = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(raw)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    float scale = Math.Min(1f, (float)MaxWidth / area.Width);
                    int w = Math.Max(1, (int)(area.Width * scale)), h = Math.Max(1, (int)(area.Height * scale));
                    using (Bitmap shot = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(shot))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(raw, 0, 0, w, h);
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            using (Font font = new Font("Arial", 11, FontStyle.Bold, GraphicsUnit.Pixel))
                            using (Pen pen = new Pen(Color.FromArgb(230, 255, 0, 110), 2))
                            using (SolidBrush tag = new SolidBrush(Color.FromArgb(235, 255, 0, 110)))
                            using (SolidBrush ink = new SolidBrush(Color.White))
                            {
                                foreach (Target t in sight.Targets)
                                {
                                    Rectangle r = new Rectangle((int)((t.Box.X - area.X) * scale), (int)((t.Box.Y - area.Y) * scale), Math.Max(4, (int)(t.Box.Width * scale)), Math.Max(4, (int)(t.Box.Height * scale)));
                                    g.DrawRectangle(pen, r);
                                    string n = t.Number.ToString();
                                    SizeF size = g.MeasureString(n, font);
                                    RectangleF label = new RectangleF(r.X, Math.Max(0, r.Y - size.Height), size.Width + 2, size.Height);
                                    g.FillRectangle(tag, label);
                                    g.DrawString(n, font, ink, label.X + 1, label.Y);
                                }
                            }
                        }
                        sight.ShotOrigin = area.Location;
                        sight.ShotScale = scale;
                        sight.ShotSize = new Size(w, h);
                        using (MemoryStream memory = new MemoryStream())
                        {
                            ImageCodecInfo codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
                            using (EncoderParameters quality = new EncoderParameters(1))
                            {
                                quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
                                shot.Save(memory, codec, quality);
                            }
                            return memory.ToArray();
                        }
                    }
                }
            }
            catch (Exception) { return null; }
        }

        /// <summary>Maps a point given in screenshot pixels back to the real screen.</summary>
        internal static bool ToScreen(Sight sight, string x, string y, out Point point)
        {
            point = Point.Empty;
            double px, py;
            if (sight == null || sight.ShotScale <= 0) return false;
            if (!double.TryParse(x ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out px)) return false;
            if (!double.TryParse(y ?? "", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out py)) return false;
            if (px < 0 || py < 0 || px > sight.ShotSize.Width || py > sight.ShotSize.Height) return false;
            point = new Point(sight.ShotOrigin.X + (int)Math.Round(px / sight.ShotScale), sight.ShotOrigin.Y + (int)Math.Round(py / sight.ShotScale));
            return true;
        }
    }

    internal static class AgentFiles
    {
        private const int MaxRead = 200 * 1024;
        private const int MaxShown = 6000;
        private const int MaxWrite = 512 * 1024;
        private static readonly string[] BlockedExtensions = { ".exe", ".dll", ".sys", ".lnk", ".reg", ".scr", ".msi", ".com", ".pif" };
        private static readonly string[] SkipDirs = { "node_modules", ".git", "AppData", "bin", "obj", ".vs", ".gradle", ".cache", "$Recycle.Bin" };

        internal static string Home { get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } }
        internal static string Desktop { get { return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }

        /// <summary>Full path inside the user's profile, or null (with a reason) if it isn't allowed.</summary>
        internal static string Resolve(string path, bool writing, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(path)) { problem = "There's no path."; return null; }
            string p = path.Trim().Trim('"');
            if (p.StartsWith("~", StringComparison.Ordinal)) p = Home + p.Substring(1);
            p = Environment.ExpandEnvironmentVariables(p);
            string full;
            try { full = Path.IsPathRooted(p) ? Path.GetFullPath(p) : (Remap(p) ?? Path.GetFullPath(Path.Combine(Desktop, p))); }
            catch (Exception) { problem = "That path isn't valid."; return null; }
            string home = Path.GetFullPath(Home).TrimEnd('\\') + "\\";
            if (!full.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            {
                string fixedUp = Remap(path);
                if (fixedUp != null) full = fixedUp;
            }
            if (!full.StartsWith(home, StringComparison.OrdinalIgnoreCase)) { problem = "I only touch files inside the user folder " + Home + ". Their Desktop is " + Desktop + ". Use paths like " + Path.Combine(Desktop, "name") + "."; return null; }
            string inside = full.Substring(home.Length);
            if (Regex.IsMatch(inside, @"^(AppData|\.ssh|\.gnupg|\.aws|\.azure|\.docker|\.kube|NTUSER)", RegexOptions.IgnoreCase) || Regex.IsMatch(full, @"\\\.git\\", RegexOptions.IgnoreCase)) { problem = "That folder is off limits."; return null; }
            if (writing && Array.IndexOf(BlockedExtensions, Path.GetExtension(full).ToLowerInvariant()) >= 0) { problem = "I won't write program files like " + Path.GetExtension(full) + "."; return null; }
            return full;
        }

        /// <summary>Models often guess paths (C:\\Users\\User\\Desktop\\x, /home/me/Desktop/x, Escritorio/x). Map them onto the real folders.</summary>
        private static string Remap(string path)
        {
            string p = path.Trim().Trim('"').Replace('/', '\\');
            string[] parts = p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string known = Known(parts[i]);
                if (known == null) continue;
                string rest = string.Join("\\", parts, i + 1, parts.Length - i - 1);
                try { return Path.GetFullPath(rest.Length == 0 ? known : Path.Combine(known, rest)); }
                catch (Exception) { return null; }
            }
            return null;
        }

        private static string Known(string segment)
        {
            switch (segment.ToLowerInvariant())
            {
                case "desktop": case "escritorio": return Desktop;
                case "documents": case "documentos": case "my documents": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                case "downloads": case "descargas": return Path.Combine(Home, "Downloads");
                case "pictures": case "imagenes": case "imágenes": return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                case "music": case "musica": case "música": return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
                case "videos": case "vídeos": return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
                default: return null;
            }
        }

        /// <summary>Told to the model every step, so it never has to guess.</summary>
        internal static string Places
        {
            get
            {
                return "Their PC runs Windows. User folder: " + Home + ". Desktop: " + Desktop + ". Documents: " + Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + ". Downloads: " + Path.Combine(Home, "Downloads") + ".";
            }
        }

        internal static bool Exists(string path)
        {
            string problem;
            string full = Resolve(path, false, out problem);
            return full != null && File.Exists(full);
        }

        internal static string Read(string path)
        {
            string problem;
            string full = Resolve(path, false, out problem);
            if (full == null) return "Couldn't read: " + problem;
            if (!File.Exists(full)) return "There's no file at " + full + ".";
            FileInfo info = new FileInfo(full);
            if (info.Length > MaxRead) return "That file is too big to read (" + info.Length / 1024 + " KB).";
            string text = File.ReadAllText(full);
            if (text.IndexOf('\0') >= 0) return "That isn't a text file.";
            string shown = text.Length > MaxShown ? text.Substring(0, MaxShown) + "\n…(" + (text.Length - MaxShown) + " more characters)" : text;
            return "Contents of " + full + " (" + text.Split('\n').Length + " lines):\n" + shown;
        }

        internal static string Write(string path, string content, bool append)
        {
            string problem;
            string full = Resolve(path, true, out problem);
            if (full == null) return "Couldn't write: " + problem;
            content = content ?? "";
            if (content.Length > MaxWrite) return "That's too much text to write at once.";
            string dir = Path.GetDirectoryName(full);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            bool existed = File.Exists(full);
            if (append && existed)
            {
                string old = File.ReadAllText(full);
                string glue = old.Length > 0 && !old.EndsWith("\n", StringComparison.Ordinal) ? Environment.NewLine : "";
                File.AppendAllText(full, glue + content, new UTF8Encoding(false));
                return "Added " + content.Split('\n').Length + " line(s) to the end of " + full + ".";
            }
            if (existed) File.Copy(full, full + ".capri-backup", true);
            File.WriteAllText(full, content, new UTF8Encoding(false));
            return (existed ? "Overwrote " + full + " (old copy: " + Path.GetFileName(full) + ".capri-backup)." : "Created " + full + ".");
        }

        internal static string MakeFolder(string path)
        {
            string problem;
            string full = Resolve(path, true, out problem);
            if (full == null) return "Couldn't make the folder: " + problem;
            if (Directory.Exists(full)) return "The folder " + full + " already exists.";
            Directory.CreateDirectory(full);
            return "Made the folder " + full + ".";
        }

        internal static string Move(string from, string to)
        {
            string problem;
            string source = Resolve(from, true, out problem);
            if (source == null) return "Couldn't move: " + problem;
            string target = Resolve(to, true, out problem);
            if (target == null) return "Couldn't move: " + problem;
            bool isDir = Directory.Exists(source);
            if (!isDir && !File.Exists(source)) return "There's nothing at " + source + ".";
            if (Directory.Exists(target)) target = Path.Combine(target, Path.GetFileName(source));
            if (File.Exists(target) || Directory.Exists(target)) return "Something called " + Path.GetFileName(target) + " is already there, so I didn't move it.";
            string dir = Path.GetDirectoryName(target);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (isDir) Directory.Move(source, target); else File.Move(source, target);
            return "Moved " + source + " to " + target + ".";
        }

        internal static string List(string path)
        {
            string problem;
            string full = Resolve(string.IsNullOrWhiteSpace(path) ? Desktop : path, false, out problem);
            if (full == null) return "Couldn't list: " + problem;
            if (!Directory.Exists(full)) return "There's no folder at " + full + ".";
            StringBuilder text = new StringBuilder("Folder " + full + ":");
            int shown = 0;
            foreach (string d in Directory.GetDirectories(full).OrderBy(x => x)) { if (shown++ >= 80) break; text.Append("\n[folder] " + Path.GetFileName(d)); }
            foreach (string f in Directory.GetFiles(full).OrderBy(x => x)) { if (shown++ >= 120) break; text.Append("\n" + Path.GetFileName(f) + "  (" + Math.Max(1, new FileInfo(f).Length / 1024) + " KB)"); }
            if (shown == 0) text.Append("\n(empty)");
            return text.ToString();
        }

        /// <summary>Searches the usual places for a file or folder by name. Bounded in depth, count and time.</summary>
        internal static List<string> Find(string name, string near, int max)
        {
            List<string> found = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) return found;
            name = name.Trim();
            List<string> roots = new List<string>();
            string problem;
            string nearFull = string.IsNullOrWhiteSpace(near) ? null : Resolve(near, false, out problem);
            if (nearFull != null && Directory.Exists(nearFull)) roots.Add(nearFull);
            foreach (string sub in new[] { "Desktop", "Documents", "source", "repos", "Projects", "projects", "dev", "code", "OneDrive", "Downloads" })
            {
                string r = Path.Combine(Home, sub);
                if (Directory.Exists(r) && !roots.Contains(r)) roots.Add(r);
            }
            Stopwatch clock = Stopwatch.StartNew();
            Queue<KeyValuePair<string, int>> queue = new Queue<KeyValuePair<string, int>>();
            foreach (string r in roots) queue.Enqueue(new KeyValuePair<string, int>(r, 0));
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (queue.Count > 0 && found.Count < max && clock.ElapsedMilliseconds < 4000 && visited.Count < 8000)
            {
                KeyValuePair<string, int> next = queue.Dequeue();
                if (!visited.Add(next.Key)) continue;
                try
                {
                    foreach (string f in Directory.GetFiles(next.Key, name)) { if (found.Count < max && !found.Contains(f)) found.Add(f); }
                    foreach (string d in Directory.GetDirectories(next.Key))
                    {
                        string leaf = Path.GetFileName(d);
                        if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase) && found.Count < max && !found.Contains(d)) found.Add(d);
                        if (next.Value < 6 && Array.IndexOf(SkipDirs, leaf) < 0 && !leaf.StartsWith(".", StringComparison.Ordinal)) queue.Enqueue(new KeyValuePair<string, int>(d, next.Value + 1));
                    }
                }
                catch (Exception) { }
            }
            return found;
        }

        internal static string FindText(string name, string near)
        {
            List<string> found = Find(name, near, 10);
            if (found.Count == 0) return "Couldn't find anything called " + Voice.Quote(name ?? "") + " in your usual folders.";
            return "Found:\n" + string.Join("\n", found.ToArray());
        }

        /// <summary>Works out which file the editor in front has open, from its title (VS Code, Notepad, Notepad++, Sublime, …).</summary>
        internal static string Active(Sight sight)
        {
            if (sight == null || string.IsNullOrEmpty(sight.Title)) return "There's no editor window in front.";
            string title = sight.Title.Replace("●", "").Replace("*", "").Trim();
            string[] parts = Regex.Split(title, @"\s+[-—–]\s+");
            if (parts.Length == 0) return "Couldn't tell which file is open from the title " + Voice.Quote(sight.Title) + ".";
            string first = parts[0].Trim();
            if (Path.IsPathRooted(first) && File.Exists(first)) return "The open file is " + first + ".";
            string file = Path.GetFileName(first);
            string folder = parts.Length >= 3 ? parts[1].Trim() : null; // VS Code: "file - folder - Visual Studio Code"
            if (string.IsNullOrEmpty(file) || file.IndexOf('.') < 0) return "Couldn't tell which file is open from the title " + Voice.Quote(sight.Title) + ". Ask them for the file's location, or use find_file.";
            List<string> candidates = Find(file, null, 25);
            if (folder != null)
            {
                List<string> inFolder = candidates.Where(c => c.IndexOf("\\" + folder + "\\", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (inFolder.Count > 0) candidates = inFolder;
            }
            candidates = candidates.Where(File.Exists).ToList();
            if (candidates.Count == 0) return "The editor shows " + Voice.Quote(file) + " but I couldn't find it on disk. Ask them where it is.";
            if (candidates.Count == 1) return "The open file is " + candidates[0] + ".";
            candidates.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            return "The open file is probably " + candidates[0] + " (most recently changed). Other matches:\n" + string.Join("\n", candidates.Skip(1).Take(4).ToArray());
        }
    }

    internal static class AgentRun
    {
        internal const int Seconds = 90;

        /// <summary>Runs one command line in a hidden cmd.exe and returns what it printed. Only after the user said yes.</summary>
        internal static string Run(string command, string folder)
        {
            if (string.IsNullOrWhiteSpace(command)) return "There's no command.";
            string problem;
            string dir = Resolve(folder, out problem);
            if (dir == null) return "Couldn't run it: " + problem;
            ProcessStartInfo start = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + command + "\"");
            start.WorkingDirectory = dir;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            StringBuilder output = new StringBuilder();
            object gate = new object();
            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) Keep(output, e.Data); };
                process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (gate) Keep(output, e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                Stopwatch clock = Stopwatch.StartNew();
                while (!process.WaitForExit(250))
                {
                    if (Computer.Interrupted || clock.Elapsed.TotalSeconds > Seconds)
                    {
                        Kill(process);
                        string text;
                        lock (gate) text = output.ToString();
                        return (Computer.Interrupted ? "Stopped it. " : "It was still running after " + Seconds + " s, so I stopped it (servers keep running; that can be normal). ") + "Output so far:\n" + Tail(text);
                    }
                }
                process.WaitForExit();
                string done;
                lock (gate) done = output.ToString();
                return "Ran " + Voice.Quote(command) + " in " + dir + ", exit code " + process.ExitCode + (process.ExitCode == 0 ? " (ok)" : " (failed)") + ". Output:\n" + Tail(done);
            }
        }

        private static string Resolve(string folder, out string problem)
        {
            string dir = AgentFiles.Resolve(string.IsNullOrWhiteSpace(folder) ? AgentFiles.Desktop : folder, false, out problem);
            if (dir != null && !Directory.Exists(dir)) { problem = "There's no folder at " + dir + "."; return null; }
            return dir;
        }

        private static void Keep(StringBuilder output, string line)
        {
            output.AppendLine(line);
            if (output.Length > 20000) output.Remove(0, output.Length - 12000);
        }

        private static string Tail(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return "(nothing)";
            return text.Length > 3000 ? "…" + text.Substring(text.Length - 3000) : text;
        }

        private static void Kill(Process process)
        {
            try { using (Process killer = Process.Start(new ProcessStartInfo("taskkill", "/PID " + process.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false })) if (killer != null) killer.WaitForExit(5000); }
            catch (Exception) { }
            try { if (!process.HasExited) process.Kill(); } catch (Exception) { }
        }
    }
}
