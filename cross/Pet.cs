using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace CapriDesktop
{
    [DataContract]
    internal sealed class Settings
    {
        [DataMember] internal int X;
        [DataMember] internal int Y;
        [DataMember] internal bool Placed;
        [DataMember] internal double Size = 240;
        [DataMember] internal string Model;
    }

    internal sealed class Clip
    {
        internal readonly Bitmap Strip;
        internal readonly int Count;
        internal readonly double Fps;
        internal readonly double Height;

        internal Clip(string name, int count, double fps, double height)
        {
            Count = count;
            Fps = fps;
            Height = height;
            Stream stream = typeof(Clip).Assembly.GetManifestResourceStream("Capri.Sprite." + name + ".png");
            if (stream == null) throw new FileNotFoundException("Capri's " + name + " animation is missing.");
            using (stream) Strip = new Bitmap(stream);
        }

        internal bool Even { get { return Strip.PixelSize.Width % Count == 0; } }
        internal int Wide { get { return Strip.PixelSize.Width / Count; } }

        internal bool Solid(int cell, int x, int y, int radius)
        {
            int left = cell * Wide + Math.Max(0, x - radius);
            int right = cell * Wide + Math.Min(Wide, x + radius + 1);
            int top = Math.Max(0, y - radius);
            int bottom = Math.Min(Strip.PixelSize.Height, y + radius + 1);
            if (right <= left || bottom <= top) return false;
            int wide = right - left;
            byte[] pixels = new byte[wide * (bottom - top) * 4];
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { Strip.CopyPixels(new PixelRect(left, top, wide, bottom - top), pin.AddrOfPinnedObject(), pixels.Length, wide * 4); }
            finally { pin.Free(); }
            for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] > 24) return true;
            return false;
        }
    }

    internal sealed class Body : Control
    {
        internal Clip Shown;
        internal int Cell;
        internal Clip Mouth;
        internal int Talking = -1;
        internal double Size = 240;
        internal double Breathe;
        internal double Tilt;
        internal Point Centre;

        internal Rect Place(Clip shown)
        {
            double tall = Size * shown.Height;
            double wide = tall * shown.Wide / shown.Strip.PixelSize.Height;
            return new Rect(Centre.X - wide / 2, Centre.Y - tall / 2, wide, tall);
        }

        public override void Render(DrawingContext context)
        {
            Clip shown = Shown;
            if (shown == null) return;
            using (context.PushTransform(Matrix.CreateTranslation(-Centre.X, -Centre.Y) * Matrix.CreateRotation(Tilt * Math.PI / 180) * Matrix.CreateTranslation(Centre.X, Centre.Y + Breathe)))
            {
                Rect place = Place(shown);
                context.DrawImage(shown.Strip, new Rect(Cell * shown.Wide, 0, shown.Wide, shown.Strip.PixelSize.Height), place);
                if (Talking >= 0 && Mouth != null) context.DrawImage(Mouth.Strip, new Rect(Talking * Mouth.Wide, 0, Mouth.Wide, Mouth.Strip.PixelSize.Height), place);
            }
        }
    }

    internal sealed class Pet : Window
    {
        internal const double SitX = 0.30;
        internal const double SitY = 0.79;
        internal const double PerchLeft = 0.21;
        internal const double PerchTop = 0.17;
        internal const double PerchRight = 0.69;
        private const int BlinkFrom = 9;
        internal const string Offline = "My brain's offline. Install Ollama from ollama.com, then run: ollama pull gemma4:e4b";
        internal const string DiscordPage = "https://discord.gg/F4fDHfGtFk";
        internal static readonly double[] Sizes = { 170, 240, 320, 440 };
        private static readonly string[] SizeNames = { "Small", "Medium", "Large", "Huge" };
        internal readonly Brain Brain = new Brain();
        internal readonly Talk Talk;
        internal Desk Desk = Desk.Make();
        internal Func<string, string> Think;
        internal Settings Saved;
        private readonly IClassicDesktopStyleApplicationLifetime desktop;
        private readonly Clip idle = new Clip("idle", 16, 8, 1.11);
        private readonly Clip blink = new Clip("blink", 4, 8, 1.11);
        private readonly Clip blinkGlitch = new Clip("blink_glitch", 4, 8, 1.11);
        private readonly Clip glitch = new Clip("glitch", 10, 15, 1.11);
        private readonly Clip dangle = new Clip("dangle", 24, 10, 1.11);
        private readonly Clip perch = new Clip("perch", 16, 8, 1);
        private readonly Clip perchBlink = new Clip("perch_blink", 4, 8, 1);
        private readonly Clip perchBlinkGlitch = new Clip("perch_blink_glitch", 4, 8, 1);
        private readonly Clip perchGlitch = new Clip("perch_glitch", 10, 15, 1);
        private readonly Clip mouth = new Clip("mouth", 5, 12, 1.11);
        private readonly Body body = new Body();
        private readonly DispatcherTimer frames = new DispatcherTimer();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Random random = new Random();
        private readonly string folder;
        private Clip clip;
        private double clipStart;
        private int idleCycle;
        private Clip blinkWith;
        private double nextGlitch;
        private bool pressed;
        private bool dragging;
        private bool falling;
        private PixelPoint anchor;
        private PixelPoint grab;
        private PixelPoint downAt;
        private Point pointer = new Point(-1, -1);
        private double fallSpeed;
        private double fallStep;
        private Ledge seat;
        private Ledge fallTo;
        private double coveredSince = -1;
        private double seatLost = -1;
        private double nextCheck;
        private int asking;
        private Smoke smoke;

        internal Pet(IClassicDesktopStyleApplicationLifetime desktop)
        {
            this.desktop = desktop;
            folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Capri");
            Saved = Program.Smoke != null ? new Settings() : Load();
            Brain.LogPath = Path.Combine(Program.Smoke ?? folder, "errors.txt");
            Title = "Capri";
            SystemDecorations = SystemDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            body.Size = Array.IndexOf(Sizes, Saved.Size) >= 0 ? Saved.Size : 240;
            body.Mouth = mouth;
            RenderOptions.SetBitmapInterpolationMode(body, BitmapInterpolationMode.HighQuality);
            Content = body;
            Fit();
            Play(idle);
            nextGlitch = 30 + random.NextDouble() * 30;
            ContextMenu = Menu();
            Talk = new Talk(this);
            frames.Interval = TimeSpan.FromMilliseconds(33);
            frames.Tick += delegate { Tick(); };
            Opened += delegate
            {
                try
                {
                    double points = Desk.Points;
                    if (points > 0 && Screens.Primary != null) Desk.Scale = Screens.Primary.Bounds.Width / points;
                }
                catch (Exception error) { Lose(error); }
                Place();
                frames.Start();
                FindBrain();
                if (Program.Smoke != null) smoke = new Smoke(this, Program.Smoke);
            };
        }

        internal double Now { get { return clock.Elapsed.TotalSeconds; } }
        internal bool Falling { get { return falling; } }
        internal bool Dragging { get { return dragging; } }
        internal Ledge Seat { get { return seat; } }
        internal double BodySize { get { return body.Size; } }
        internal Clip Showing { get { return clip; } }
        internal Clip PerchGlitch { get { return perchGlitch; } }
        internal IList<Clip> Clips { get { return new[] { idle, blink, blinkGlitch, glitch, dangle, perch, perchBlink, perchBlinkGlitch, perchGlitch, mouth }; } }
        internal PixelPoint Anchor { get { return anchor; } }
        internal PixelPoint Hip { get { return new PixelPoint(anchor.X + Px(body.Size * SitX), anchor.Y + Px(body.Size * SitY)); } }
        internal int Headroom { get { return (int)Math.Ceiling(body.Size * (SitY - PerchTop) * DesktopScaling); } }
        internal int Feet { get { return anchor.Y + Px(body.Size * 0.99); } }
        internal PixelPoint FeetPoint { get { return new PixelPoint(anchor.X + Px(body.Size * 0.58), Feet); } }
        internal PixelPoint Head { get { return new PixelPoint(anchor.X + Px(body.Size * 0.52), anchor.Y - Px(4)); } }
        internal PixelPoint Middle { get { return new PixelPoint(anchor.X + Px(body.Size / 2), anchor.Y + Px(body.Size / 2)); } }
        internal PixelRect Area { get { Screen screen = ScreenHere(); return screen == null ? new PixelRect(0, 0, 1920, 1080) : screen.WorkingArea; } }
        internal bool Busy { get { return dragging || pressed || falling; } }
        internal bool Hovered { get { return body.IsPointerOver && Opaque(pointer); } }
        private double FrameX { get { return body.Size * 0.08 - 8; } }
        private double FrameY { get { return -body.Size * 0.06 - 8; } }

        private int Px(double dip) { return (int)Math.Round(dip * DesktopScaling); }

        internal PixelPoint Seated(int x, int y) { return new PixelPoint(x - Px(body.Size * SitX), y - Px(body.Size * SitY)); }

        internal void Move(PixelPoint to)
        {
            anchor = to;
            Position = new PixelPoint(to.X + Px(FrameX), to.Y + Px(FrameY));
        }

        private void Fit()
        {
            Width = Math.Ceiling(body.Size * 0.77 + 16);
            Height = Math.Ceiling(body.Size * 1.12 + 16);
            body.Centre = new Point(body.Size / 2 - FrameX, body.Size / 2 - FrameY);
        }

        internal Screen ScreenHere()
        {
            PixelPoint hip = Hip;
            Screen screen = Screens.ScreenFromPoint(new PixelPoint(hip.X, hip.Y - 1));
            if (screen == null) screen = Screens.ScreenFromWindow(this);
            return screen ?? Screens.Primary;
        }

        private PixelRect ScreenAt(PixelPoint point)
        {
            Screen screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
            return screen == null ? new PixelRect(0, 0, 1920, 1080) : screen.Bounds;
        }

        private PixelRect WorkAt(PixelPoint point)
        {
            Screen screen = Screens.ScreenFromPoint(point) ?? ScreenHere();
            return screen == null ? new PixelRect(0, 0, 1920, 1080) : screen.WorkingArea;
        }

        private void Place()
        {
            Screen screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen == null) return;
            if (Saved.Placed && Screens.ScreenFromPoint(new PixelPoint(Saved.X + Px(body.Size / 2), Saved.Y + Px(body.Size / 2))) != null)
            {
                Move(new PixelPoint(Saved.X, Saved.Y));
                Fall();
                return;
            }
            PixelRect area = screen.WorkingArea;
            Move(new PixelPoint(area.Right - Px(body.Size * PerchRight) - Px(40), area.Y));
            Fall();
        }

        internal void Bring()
        {
            Screen screen = Screens.Primary ?? ScreenHere();
            if (screen == null) return;
            PixelRect area = screen.WorkingArea;
            Move(new PixelPoint(area.X + area.Width / 2 - Px(body.Size / 2), area.Y));
            Fall();
        }

        private void Tick()
        {
            double now = Now;
            if (!dragging)
            {
                if (falling) Tumble(now);
                else if (seat != null) HoldSeat(now);
            }
            if (now >= nextCheck)
            {
                nextCheck = now + 0.25;
                CheckSeat(now);
            }
            Advance(now);
            Talk.Update(now);
            if (smoke != null) smoke.Tick(now);
        }

        private void Play(Clip next)
        {
            clip = next;
            clipStart = Now;
            idleCycle = 0;
            blinkWith = null;
        }

        private void Advance(double now)
        {
            bool seated = clip == perch || clip == perchGlitch;
            bool perched = seat != null;
            if (perched && !seated) Play(perch);
            if ((!perched && seated) || (clip == dangle && !dragging && !falling)) Play(idle);
            int index = (int)((now - clipStart) * clip.Fps);
            if (clip == glitch && index >= glitch.Count) { Play(idle); index = 0; }
            if (clip == perchGlitch && index >= perchGlitch.Count) { Play(perch); index = 0; }
            if (clip == idle || clip == perch)
            {
                int cycle = index / clip.Count;
                if (cycle != idleCycle)
                {
                    idleCycle = cycle;
                    double roll = random.NextDouble();
                    blinkWith = roll < 0.1 ? (clip == perch ? perchBlinkGlitch : blinkGlitch) : roll < 0.55 ? (clip == perch ? perchBlink : blink) : null;
                    if (now >= nextGlitch && Talk.Quiet)
                    {
                        nextGlitch = now + 30 + random.NextDouble() * 30;
                        Play(clip == perch ? perchGlitch : glitch);
                        index = 0;
                    }
                }
            }
            int cell = index % clip.Count;
            Clip shown = clip;
            if ((clip == idle || clip == perch) && blinkWith != null && cell >= BlinkFrom && cell < BlinkFrom + blinkWith.Count) { shown = blinkWith; cell -= BlinkFrom; }
            bool settled = !dragging && clip != dangle;
            body.Breathe = settled ? Math.Sin(now * 1.75) * 2.2 : 0;
            body.Tilt = settled ? Math.Sin(now * 0.95) * 0.65 : 0;
            body.Shown = shown;
            body.Cell = cell;
            body.Talking = clip == idle && shown != blinkGlitch ? Talk.MouthCell(now) : -1;
            body.InvalidateVisual();
        }

        private List<long> Ours()
        {
            List<long> skip = new List<long>();
            foreach (TopLevel window in new TopLevel[] { this, Talk.Bubble, Talk.Typer, Talk.Pill })
            {
                IPlatformHandle handle = window.TryGetPlatformHandle();
                if (handle != null) skip.Add(handle.Handle.ToInt64());
            }
            return skip;
        }

        internal string DeskTrouble;

        private void Lose(Exception error)
        {
            DeskTrouble = error.GetType().Name + ": " + error.Message;
            Desk = new BareDesk();
        }

        internal List<Spot> Windows()
        {
            try { return Desk.Windows(Ours()); }
            catch (Exception error) { Lose(error); return new List<Spot>(); }
        }

        private Spot Find(long window)
        {
            try { return Desk.Find(window); }
            catch (Exception error) { Lose(error); return null; }
        }

        private bool Alive(long window)
        {
            try { return Desk.Alive(window); }
            catch (Exception error) { Lose(error); return false; }
        }

        internal List<Ledge> Ledges()
        {
            List<PixelRect> floors = new List<PixelRect>();
            Desk.Screens.Clear();
            foreach (Screen screen in Screens.All)
            {
                floors.Add(screen.WorkingArea);
                Desk.Screens.Add(screen.Bounds);
            }
            return Perches.Find(Windows(), floors);
        }

        internal Ledge Perch(IEnumerable<Ledge> ledges, PixelPoint hip)
        {
            return Perches.Below(ledges, hip, Px(Math.Max(32, body.Size * 0.45)), Px(Math.Max(160, body.Size)), Px(24), Headroom, ScreenAt);
        }

        internal void Fall()
        {
            seat = null;
            fallTo = null;
            falling = true;
            fallSpeed = 0;
            fallStep = Now;
            Play(dangle);
        }

        private void Tumble(double now)
        {
            double step = Math.Max(0, Math.Min(0.05, now - fallStep));
            fallStep = now;
            PixelPoint hip = Hip;
            if (fallTo == null) fallTo = Perches.Below(Ledges(), hip, 0, int.MaxValue, 0, Headroom, ScreenAt);
            if (fallTo == null) { falling = false; Save(); return; }
            fallSpeed += body.Size * 11 * step;
            int drop = (int)Math.Ceiling(fallSpeed * step * DesktopScaling);
            if (hip.Y + drop >= fallTo.Y) Land(fallTo);
            else Move(new PixelPoint(anchor.X, anchor.Y + drop));
        }

        private void Land(Ledge ledge)
        {
            fallTo = null;
            if (ledge.Window != 0)
            {
                Spot spot = Find(ledge.Window);
                if (spot == null || !spot.Seat) return;
                if (spot.Bounds != ledge.Bounds) { ledge.Left = spot.Bounds.X; ledge.Right = spot.Bounds.Right; ledge.Y = spot.Bounds.Y; ledge.Bounds = spot.Bounds; }
            }
            falling = false;
            coveredSince = -1;
            seatLost = -1;
            int x = Math.Max(ledge.Left + Px(8), Math.Min(Hip.X, ledge.Right - Px(8)));
            PixelRect area = WorkAt(new PixelPoint(x, ledge.Y - 1));
            int rightmost = area.Right - (int)Math.Ceiling(body.Size * (PerchRight - SitX) * DesktopScaling);
            x = Math.Max(area.X + (int)Math.Ceiling(body.Size * (SitX - PerchLeft) * DesktopScaling), Math.Min(x, rightmost));
            seat = ledge;
            Move(Seated(x, ledge.Y));
            Play(perchGlitch);
            Save();
        }

        internal void HoldSeat(double now)
        {
            if (seat == null || seat.Window == 0) return;
            Spot spot = Find(seat.Window);
            if (spot == null || !spot.Seat)
            {
                if (seatLost < 0) seatLost = now;
                if (!Alive(seat.Window) || now - seatLost >= 0.3) StandUp();
                return;
            }
            seatLost = -1;
            PixelRect bounds = spot.Bounds;
            if (bounds == seat.Bounds) return;
            int dx = bounds.Width == seat.Bounds.Width ? bounds.X - seat.Bounds.X : 0;
            int x = Math.Max(bounds.X + Px(8), Math.Min(Hip.X + dx, bounds.Right - Px(8)));
            seat.Bounds = bounds;
            seat.Left = bounds.X;
            seat.Right = bounds.Right;
            seat.Y = bounds.Y;
            Move(Seated(x, bounds.Y));
        }

        internal void CheckSeat(double now)
        {
            if (seat == null || falling || dragging || seat.Floor) return;
            if (!Perches.Covered(Windows(), seat, Hip.X)) { coveredSince = -1; return; }
            if (coveredSince < 0) coveredSince = now;
            if (now - coveredSince >= 0.7) StandUp();
        }

        internal void StandUp()
        {
            seat = null;
            coveredSince = -1;
            seatLost = -1;
            falling = false;
            fallTo = null;
            Play(glitch);
        }

        private void SitHere()
        {
            PixelPoint hip = Hip;
            Land(new Ledge { Left = hip.X - Px(8), Right = hip.X + Px(8), Y = hip.Y, Floor = true, Free = true });
        }

        private bool Opaque(Point at)
        {
            Clip shown = body.Shown;
            if (shown == null || at.X < 0) return false;
            Rect place = body.Place(shown);
            double x = at.X - place.X;
            double y = at.Y - body.Breathe - place.Y;
            if (x < 0 || y < 0 || x >= place.Width || y >= place.Height) return false;
            return shown.Solid(body.Cell, (int)(x / place.Width * shown.Wide), (int)(y / place.Height * shown.Strip.PixelSize.Height), 6);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            Point at = e.GetPosition(this);
            if (!Opaque(at)) return;
            pressed = true;
            dragging = false;
            downAt = this.PointToScreen(at);
            grab = new PixelPoint(downAt.X - anchor.X, downAt.Y - anchor.Y);
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            pointer = e.GetPosition(this);
            if (!pressed) return;
            PixelPoint at = this.PointToScreen(pointer);
            int dx = at.X - downAt.X;
            int dy = at.Y - downAt.Y;
            if (!dragging && dx * dx + dy * dy < 25) return;
            if (!dragging) Grab();
            Move(new PixelPoint(at.X - grab.X, at.Y - grab.Y));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            pointer = new Point(-1, -1);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!pressed || e.InitialPressMouseButton != MouseButton.Left) return;
            pressed = false;
            e.Pointer.Capture(null);
            if (dragging) Drop();
            else React();
        }

        internal void React()
        {
            Play(seat != null ? perchGlitch : glitch);
        }

        /// <summary>Capri Signal hook: a short, existing reaction animation. Never interrupts a drag, fall or press.</summary>
        internal void Alert()
        {
            if (Busy || clip == glitch || clip == perchGlitch) return;
            React();
        }

        internal void NoVoice()
        {
            Talk.Answer("(voice)", "Voice chat isn't on Mac and Linux yet. Type to me for now, choom.");
        }

        internal void Grab()
        {
            seat = null;
            fallTo = null;
            falling = false;
            dragging = true;
            Play(dangle);
            Talk.Dismiss();
        }

        internal bool Drop()
        {
            dragging = false;
            Ledge target = Perch(Ledges(), Hip);
            if (target == null) { Save(); return false; }
            falling = true;
            fallTo = target;
            fallSpeed = 0;
            fallStep = Now;
            return true;
        }

        internal void Resize(double size)
        {
            PixelPoint hip = Hip;
            PixelPoint middle = Middle;
            body.Size = size;
            Saved.Size = size;
            Fit();
            if (seat != null) Move(Seated(hip.X, seat.Y));
            else Move(new PixelPoint(middle.X - Px(size / 2), middle.Y - Px(size / 2)));
            body.InvalidateVisual();
            Save();
        }

        private ContextMenu Menu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem talk = new MenuItem { Header = "Talk to her" };
            talk.Click += delegate { Talk.Open(); };
            MenuItem sit = new MenuItem { Header = "Sit here" };
            sit.Click += delegate { if (seat != null && seat.Free) StandUp(); else SitHere(); };
            MenuItem sizes = new MenuItem { Header = "Size" };
            for (int i = 0; i < Sizes.Length; i++)
            {
                double size = Sizes[i];
                MenuItem pick = new MenuItem { Header = SizeNames[i] };
                pick.Click += delegate { Resize(size); };
                sizes.Items.Add(pick);
            }
            MenuItem discord = new MenuItem { Header = "Join the Discord" };
            discord.Click += delegate { Browse(DiscordPage); };
            MenuItem quit = new MenuItem { Header = "Quit Capri" };
            quit.Click += delegate { Quit(); };
            menu.Items.Add(talk);
            menu.Items.Add(sit);
            menu.Items.Add(sizes);
            menu.Items.Add(discord);
            menu.Items.Add(new Separator());
            menu.Items.Add(quit);
            return menu;
        }

        internal static void Browse(string url)
        {
            try
            {
                if (OperatingSystem.IsMacOS()) Process.Start("open", url);
                else if (OperatingSystem.IsLinux()) Process.Start("xdg-open", url);
                else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception) { }
        }

        internal void FindBrain()
        {
            string saved = Saved.Model;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string model = null;
                try { model = Brain.PickModel(Brain.InstalledModels(), saved, null); }
                catch (Exception) { }
                Dispatcher.UIThread.Post(delegate
                {
                    if (Brain.Model != null || model == null) return;
                    Brain.Model = model;
                    Saved.Model = model;
                    Save();
                });
            });
        }

        internal void Hear(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (text.Length > 240) text = text.Substring(0, 240);
            int ticket = ++asking;
            Brain.Cancel();
            string blocked = Safety.Screen(text);
            if (blocked != null) { Talk.Answer(text, blocked); return; }
            Func<string, string> think = Think;
            if (think == null)
            {
                if (Brain.Model == null) { Talk.Answer(text, Offline); FindBrain(); return; }
                think = Brain.Ask;
            }
            Talk.Thinking(text);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string said;
                try { said = Safety.Filter(think(text)); }
                catch (BrainException error) { said = error.Message == Brain.Offline ? Offline : error.Told ? error.Message : "My brain couldn't answer: " + error.Message; }
                catch (Exception error) { said = Brain.Explain(error) ?? "That reply was cancelled. Try again."; }
                if (string.IsNullOrWhiteSpace(said)) said = "My brain sent an empty reply. Try again.";
                Dispatcher.UIThread.Post(delegate { if (ticket == asking) Talk.Answer(text, said); });
            });
        }

        private Settings Load()
        {
            try
            {
                using (FileStream stream = File.OpenRead(Path.Combine(folder, "cross.json"))) return (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
            }
            catch (Exception) { return new Settings(); }
        }

        internal void Save()
        {
            Saved.X = anchor.X;
            Saved.Y = anchor.Y;
            Saved.Placed = true;
            if (Program.Smoke != null) return;
            try
            {
                Directory.CreateDirectory(folder);
                using (FileStream stream = File.Create(Path.Combine(folder, "cross.json"))) new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, Saved);
            }
            catch (Exception) { }
        }

        internal void Quit()
        {
            Save();
            frames.Stop();
            Brain.Cancel();
            Talk.Dismiss();
            desktop.Shutdown(0);
        }

        internal void Finish(int code)
        {
            frames.Stop();
            Talk.Dismiss();
            desktop.Shutdown(code);
        }
    }
}
