// Capri Lockdown — "guard my computer": blocks the keyboard (low-level hook) behind a full-screen ICE screen
// until the 4-digit code or the secret word is typed. Emergency exits: the emergency code, left click on Capri ->
// EMERGENCY UNLOCK, and Ctrl+Alt+Del / Win+L (Windows never lets apps block those, so you can't get locked out).
// Codes are NOT in the source: only salted SHA-256 hashes in %APPDATA%\Capri\lockdown.json.
// C# 5 / .NET Framework 4.x.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CapriDesktop
{
    [DataContract]
    internal sealed class LockdownCodes
    {
        [DataMember(Name = "salt")] public string Salt;
        [DataMember(Name = "code")] public string CodeHash;
        [DataMember(Name = "emergency")] public string EmergencyHash;
        [DataMember(Name = "word")] public string WordHash;      // optional secret word typed anywhere to unlock
        [DataMember(Name = "wordLength")] public int WordLength;

        internal static string FilePath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capri", "lockdown.json"); } }

        /// <summary>One-time setup file the user (or Claude) drops in Desktop\Capri; Capri moves it into AppData on first use.</summary>
        internal static string SetupPath { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "Capri", "lockdown.json"); } }

        internal static LockdownCodes Load()
        {
            try
            {
                if (File.Exists(SetupPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.Copy(SetupPath, FilePath, true);
                    File.Delete(SetupPath);
                }
                if (!File.Exists(FilePath)) return null;
                using (FileStream s = File.OpenRead(FilePath))
                {
                    LockdownCodes c = (LockdownCodes)new DataContractJsonSerializer(typeof(LockdownCodes)).ReadObject(s);
                    if (c == null || string.IsNullOrEmpty(c.Salt) || string.IsNullOrEmpty(c.CodeHash) || string.IsNullOrEmpty(c.EmergencyHash)) return null;
                    return c;
                }
            }
            catch (Exception) { return null; }
        }

        internal static string Hash(string salt, string code)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(salt + ":" + code))).Replace("-", "").ToLowerInvariant();
        }

        private static bool Same(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>True if the typed letters end with the secret word.</summary>
        internal bool WordTyped(string recent)
        {
            if (string.IsNullOrEmpty(WordHash) || WordLength <= 0 || recent.Length < WordLength) return false;
            return Same(Hash(Salt, recent.Substring(recent.Length - WordLength)), WordHash);
        }

        /// <summary>0 = wrong, 1 = main code, 2 = emergency code.</summary>
        internal int Check(string code)
        {
            string h = Hash(Salt, code);
            if (Same(h, CodeHash)) return 1;
            if (Same(h, EmergencyHash)) return 2;
            return 0;
        }
    }

    internal static class LockdownWords
    {
        private const string Device = @"(ordenador|ordena|pc|equipo|compu|computador[a]?|portatil|laptop|pantalla|teclado|escritorio|rig|cosas|computer|screen|keyboard|desktop|stuff|machine)";
        private static readonly Regex[] Asks =
        {
            new Regex(@"\b(cuida|cuidame|cuidamelo|vigila|vigilame|vigilamelo|protege|protegeme|protegemelo|bloquea|bloqueame|bloquealo|guarda|guardame|blinda|asegura|aseguramelo)\b.{0,30}\b" + Device + @"\b"),
            new Regex(@"\b" + Device + @"\b.{0,20}\b(cuidalo|cuidamelo|vigilalo|vigilamelo|protegelo|protegemelo|bloquealo|bloqueala|guardalo)\b"),
            new Regex(@"\b(me voy|me piro|ahora vuelvo|vuelvo (en|ahora)|voy a salir|salgo un momento|voy al|me ausento)\b.{0,40}\b(cuida|vigila|protege|bloquea|guarda)"),
            new Regex(@"\bnadie (toque|use|mire|entre en)\b"),
            new Regex(@"\bmodo (bloqueo|lockdown|proteccion|guardian|vigilancia|ice)\b|\b(activa|pon|ponte en)\s+(el\s+|la\s+)?(modo\s+)?(bloqueo|lockdown|proteccion|vigilancia|ice)\b"),
            new Regex(@"\b(guard|watch|protect|lock|secure|keep an eye on|look after)\b.{0,25}\b(my|the|this)?\s*" + Device + @"\b"),
            new Regex(@"\b(i'?m (leaving|stepping out|going|heading out)|brb|be right back|back in a (bit|sec|minute))\b.{0,40}\b(guard|watch|protect|lock)\b"),
            new Regex(@"\b(lock\s*down|ice up|don'?t let anyone (touch|use))\b"),
        };

        internal static string Normalize(string text)
        {
            string d = (text ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
            StringBuilder b = new StringBuilder(d.Length);
            foreach (char c in d) if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) b.Append(c);
            return b.ToString();
        }

        internal static bool Asked(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = Normalize(text);
            if (Regex.IsMatch(t, @"\b(no|don'?t|desbloquea|unlock|quita)\b.{0,12}(bloque|lock)")) return false;
            foreach (Regex r in Asks) if (r.IsMatch(t)) return true;
            return false;
        }
    }

    internal sealed class Lockdown : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private readonly LockdownCodes codes;
        private readonly Action<bool> unlocked;      // true = emergency exit
        private readonly Func<IntPtr[]> keepAbove;   // Capri's windows stay clickable above the ICE screen
        private readonly HookProc proc;
        private IntPtr hook = IntPtr.Zero;
        private LockdownScreen screen;
        private Timer timer;
        private DateTime coolUntil = DateTime.MinValue;

        internal bool Active { get { return screen != null; } }
        internal bool KeyboardBlocked { get { return hook != IntPtr.Zero; } }

        internal Lockdown(LockdownCodes codes, Action<bool> unlocked, Func<IntPtr[]> keepAbove)
        {
            this.codes = codes;
            this.unlocked = unlocked;
            this.keepAbove = keepAbove;
            proc = Hook;
        }

        internal void Start()
        {
            if (Active) return;
            screen = new LockdownScreen();
            screen.Show();
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            screen.KeyboardOk = hook != IntPtr.Zero;
            timer = new Timer();
            timer.Interval = 90;
            timer.Tick += delegate { Tick(); };
            timer.Start();
            Raise();
        }

        private void Tick()
        {
            if (screen == null) return;
            screen.Cooldown = coolUntil > DateTime.UtcNow ? (int)Math.Ceiling((coolUntil - DateTime.UtcNow).TotalSeconds) : 0;
            screen.Animate();
            Raise();
        }

        private int raiseTicks;
        private void Raise()
        {
            if (screen == null || (++raiseTicks % 6) != 1) return;
            SetWindowPos(screen.Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            foreach (IntPtr above in keepAbove()) if (above != IntPtr.Zero) SetWindowPos(above, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        // Runs on the UI thread (the hook was installed there). Must stay fast.
        private IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code < 0 || screen == null) return CallNextHookEx(hook, code, wParam, lParam);
            int message = wParam.ToInt32();
            if (message == 0x0100 || message == 0x0104) // WM_KEYDOWN / WM_SYSKEYDOWN
            {
                int vk = Marshal.ReadInt32(lParam);
                char digit = vk >= 0x30 && vk <= 0x39 ? (char)('0' + vk - 0x30) : vk >= 0x60 && vk <= 0x69 ? (char)('0' + vk - 0x60) : '\0';
                if (digit != '\0') Type(digit);
                else if (vk >= 0x41 && vk <= 0x5A) Letter((char)('a' + vk - 0x41));
                else if (vk == 0x08) screen.Back();
                else if (vk == 0x1B) screen.Clear();
            }
            return new IntPtr(1); // swallow every key, down and up
        }

        private string letters = "";

        // Letters are invisible: typing the secret word (anywhere, any time) unlocks.
        private void Letter(char c)
        {
            letters += c;
            if (letters.Length > 32) letters = letters.Substring(letters.Length - 32);
            if (codes.WordTyped(letters)) Unlock(false);
        }

        private void Type(char digit)
        {
            if (DateTime.UtcNow < coolUntil) { screen.Flash(); return; }
            string entered = screen.Push(digit);
            if (entered.Length < 4) return;
            int result = codes.Check(entered);
            if (result == 0)
            {
                screen.Wrong();
                if (screen.Attempts % 5 == 0) coolUntil = DateTime.UtcNow.AddSeconds(30);
                return;
            }
            Unlock(result == 2);
        }

        /// <summary>Lifts the lockdown. emergency = emergency code or click on Capri.</summary>
        internal void Unlock(bool emergency)
        {
            if (!Active) return;
            Stop();
            unlocked(emergency);
        }

        private void Stop()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            if (timer != null) { timer.Stop(); timer.Dispose(); timer = null; }
            if (screen != null) { screen.Close(); screen.Dispose(); screen = null; }
        }

        public void Dispose() { Stop(); }
    }

    /// <summary>Full-screen ICE screen, Edgerunners style: black, acid yellow, hot pink, cyan glitch.</summary>
    internal sealed class LockdownScreen : Form
    {
        private static readonly Color Yellow = Color.FromArgb(243, 230, 0);
        private static readonly Color Pink = Color.FromArgb(255, 0, 110);
        private static readonly Color Cyan = Color.FromArgb(0, 240, 255);
        private readonly Random random = new Random();
        private readonly List<char> digits = new List<char>();
        private int shake;
        private int flash;
        private int frame;
        internal int Attempts;
        internal int Cooldown;
        internal bool KeyboardOk = true;

        internal LockdownScreen()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = SystemInformation.VirtualScreen;
            BackColor = Color.Black;
            Opacity = 0.94;
            DoubleBuffered = true;
            Cursor = Cursors.No;
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams p = base.CreateParams; p.ExStyle |= 0x00000080; return p; } // tool window: not in Alt+Tab
        }

        internal string Push(char d) { if (digits.Count < 4) digits.Add(d); Invalidate(); return new string(digits.ToArray()); }
        internal void Back() { if (digits.Count > 0) digits.RemoveAt(digits.Count - 1); Invalidate(); }
        internal void Clear() { digits.Clear(); Invalidate(); }
        internal void Wrong() { Attempts++; shake = 10; flash = 8; digits.Clear(); Invalidate(); }
        internal void Flash() { flash = 4; Invalidate(); }
        internal void Animate() { frame++; if (shake > 0) shake--; if (flash > 0) flash--; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            Rectangle primary = Screen.PrimaryScreen.Bounds;
            Point origin = new Point(primary.X - Bounds.X, primary.Y - Bounds.Y);
            int w = primary.Width, h = primary.Height;
            float k = Math.Max(0.75f, h / 1080f);
            int dx = shake > 0 ? random.Next(-18, 19) : 0;

            // scanlines + random glitch bars everywhere
            using (Pen scan = new Pen(Color.FromArgb(22, Yellow))) for (int y = 0; y < Height; y += 4) g.DrawLine(scan, 0, y, Width, y);
            for (int i = 0; i < 6; i++)
            {
                if (random.NextDouble() > 0.55) continue;
                int y = random.Next(Height), bh = random.Next(2, 9);
                Color c = i % 3 == 0 ? Pink : i % 3 == 1 ? Cyan : Yellow;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(random.Next(25, 90), c))) g.FillRectangle(b, random.Next(-200, Width / 2), y, random.Next(200, Width), bh);
            }

            int cx = origin.X + w / 2 + dx;
            int top = origin.Y + (int)(h * 0.22f);
            using (Font huge = Look.Mono(64 * k, FontStyle.Bold))
            using (Font big = Look.Mono(26 * k, FontStyle.Bold))
            using (Font mid = Look.Mono(18 * k, FontStyle.Regular))
            using (Font small = Look.Mono(14 * k, FontStyle.Regular))
            using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center })
            {
                // chromatic-aberration title
                string title = "ICE // LOCKDOWN";
                int jitter = frame % 23 == 0 ? random.Next(-12, 13) : 0;
                using (SolidBrush p = new SolidBrush(Color.FromArgb(200, Pink))) g.DrawString(title, huge, p, cx - 4 + jitter, top, center);
                using (SolidBrush c = new SolidBrush(Color.FromArgb(200, Cyan))) g.DrawString(title, huge, c, cx + 4 - jitter, top + 2, center);
                using (SolidBrush y = new SolidBrush(Yellow)) g.DrawString(title, huge, y, cx, top, center);
                using (SolidBrush pk = new SolidBrush(Pink))
                    g.DrawString("CAPRI IS GUARDING THIS RIG  ·  NO CODE, NO ENTRY", mid, pk, cx, top + 100 * k, center);

                // code slots
                int slot = (int)(86 * k), gap = (int)(22 * k);
                int total = slot * 4 + gap * 3;
                int sx = cx - total / 2, sy = top + (int)(190 * k);
                for (int i = 0; i < 4; i++)
                {
                    Rectangle r = new Rectangle(sx + i * (slot + gap), sy, slot, (int)(slot * 1.2f));
                    Color edge = flash > 0 ? Pink : i == digits.Count ? Yellow : Color.FromArgb(160, Yellow);
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(flash > 0 ? 70 : 28, flash > 0 ? Pink : Yellow))) g.FillPolygon(fill, Cut(r, (int)(14 * k)));
                    using (Pen pen = new Pen(edge, 3 * k)) g.DrawPolygon(pen, Cut(r, (int)(14 * k)));
                    if (i < digits.Count) using (SolidBrush y = new SolidBrush(Yellow)) g.DrawString("■", big, y, r.X + r.Width / 2, r.Y + r.Height / 2 - 20 * k, center);
                    else if (i == digits.Count && (frame / 6) % 2 == 0) using (SolidBrush y = new SolidBrush(Yellow)) g.FillRectangle(y, r.X + r.Width / 2 - 12 * k, r.Bottom - 22 * k, 24 * k, 5 * k);
                }

                int line = sy + (int)(slot * 1.2f) + (int)(40 * k);
                string status = Cooldown > 0 ? "TOO MANY ATTEMPTS  ·  ICE COOLDOWN " + Cooldown + "s"
                              : flash > 0 && Attempts > 0 ? "ACCESS DENIED  ·  ATTEMPT " + Attempts
                              : "ENTER 4-DIGIT CODE";
                using (SolidBrush b = new SolidBrush(Cooldown > 0 || flash > 0 ? Pink : Cyan)) g.DrawString(status, big, b, cx, line, center);
                if (!KeyboardOk)
                    using (SolidBrush b = new SolidBrush(Pink)) g.DrawString("(keyboard lock unavailable on this system — screen lock only)", small, b, cx, line + 50 * k, center);
            }
        }

        private static Point[] Cut(Rectangle r, int c)
        {
            return new[] { new Point(r.Left + c, r.Top), new Point(r.Right, r.Top), new Point(r.Right, r.Bottom - c), new Point(r.Right - c, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, r.Top + c) };
        }
    }
}
