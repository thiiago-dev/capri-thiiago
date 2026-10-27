// Capri Signal — wires receiver → router → manager → cards / sound / Capri's animation, plus the menus.
using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace CapriDesktop.Signal
{
    internal sealed class SignalHub : IDisposable
    {
        internal readonly SignalSettings Settings;
        internal readonly NotificationManager Manager;
        internal readonly SignalLog Log;
        private readonly SignalReceiver receiver;
        private readonly SignalSound sound;
        private readonly Action<SignalKind> react;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private SignalCards cards;
        private NativeMenuItem status;
        private MenuItem statusItem;

        /// <param name="react">Called on the UI thread so Capri can play her (existing) reaction animation.</param>
        internal SignalHub(string folder, Action<SignalKind> react)
        {
            this.react = react;
            Settings = SignalSettings.Load(folder);
            Log = new SignalLog(Settings.LogEvents ? Settings.LogPath : null);
            Manager = new NotificationManager(Settings);
            sound = new SignalSound(Settings, Log);
            receiver = new SignalReceiver(Settings, Deliver, Log);
            Manager.Changed += delegate { Dispatcher.UIThread.Post(Redraw); };
            timer.Tick += delegate
            {
                Manager.Expire(DateTimeOffset.Now);
                if (cards != null && cards.IsVisible) cards.Pulse();
            };
        }

        internal void Start()
        {
            cards = new SignalCards(Manager);
            timer.Start();
            if (Settings.Enabled) receiver.Start();
            UpdateStatus();
        }

        public void Dispose()
        {
            timer.Stop();
            receiver.Stop();
            sound.Stop();
            if (cards != null) cards.Close();
        }

        /// <summary>Runs on the receiver's background thread.</summary>
        private string Deliver(SignalEvent e)
        {
            SignalDecision decision = Manager.Accept(e, DateTimeOffset.Now);
            if (decision.Outcome == SignalOutcome.Shown || decision.Outcome == SignalOutcome.Updated)
            {
                bool ring = decision.Ring;
                Dispatcher.UIThread.Post(delegate
                {
                    if (ring) sound.Ring(e.Kind);
                    if (!Settings.Silent && react != null) react(e.Kind);
                });
            }
            return decision.Outcome.ToString().ToLowerInvariant();
        }

        private void Redraw()
        {
            if (cards != null) cards.Refresh();
        }

        internal void Test(SignalKind kind)
        {
            Deliver(SignalRouter.Sanitize(new SignalEvent
            {
                Kind = kind,
                Source = "test",
                EventId = Guid.NewGuid().ToString("N"),
                TaskId = "menu-test-" + kind,
                Project = "Capri",
                Message = "Notificación de prueba enviada desde el menú de Capri.",
            }));
        }

        private void SetEnabled(bool on)
        {
            Settings.Enabled = on;
            Settings.Save();
            if (on) receiver.Start(); else { receiver.Stop(); Manager.DismissAll(); }
            UpdateStatus();
        }

        private string StatusText()
        {
            if (!Settings.Enabled) return "Signal: desactivado";
            if (receiver.Running) return "Signal: escuchando en 127.0.0.1:" + receiver.Port;
            return "Signal: sin receptor (" + (receiver.LastError ?? "error") + ")";
        }

        private void UpdateStatus()
        {
            string text = StatusText();
            if (status != null) status.Header = text;
            if (statusItem != null) statusItem.Header = text;
        }

        // ---- menus ------------------------------------------------------------------------

        /// <summary>Submenu for Capri's right-click menu.</summary>
        internal MenuItem ContextMenuItem()
        {
            MenuItem root = new MenuItem { Header = "Capri Signal" };
            statusItem = new MenuItem { Header = StatusText(), IsEnabled = false };
            root.Items.Add(statusItem);
            root.Items.Add(new Separator());
            root.Items.Add(Check("Notificaciones", () => Settings.Enabled, SetEnabled));
            root.Items.Add(Check("Sonido", () => Settings.Sound, v => { Settings.Sound = v; Settings.Save(); }));
            root.Items.Add(Check("Modo silencioso", () => Settings.Silent, v => { Settings.Silent = v; Settings.Save(); if (v) sound.Stop(); }));
            root.Items.Add(Check("Avisar también al empezar", () => Settings.ShowStarted, v => { Settings.ShowStarted = v; Settings.Save(); }));
            MenuItem volume = new MenuItem { Header = "Volumen" };
            foreach (int level in new[] { 25, 50, 70, 100 })
            {
                int pick = level;
                volume.Items.Add(Check(level + " %", () => Settings.Volume == pick, v => { Settings.Volume = pick; Settings.Save(); sound.Ring(SignalKind.TurnEnded); }));
            }
            root.Items.Add(volume);
            root.Items.Add(new Separator());
            MenuItem test = new MenuItem { Header = "Probar" };
            foreach (SignalKind kind in new[] { SignalKind.TurnEnded, SignalKind.NeedsInput, SignalKind.PermissionRequired, SignalKind.Failed })
            {
                SignalKind k = kind;
                MenuItem item = new MenuItem { Header = SignalCards.Label(k).ToLowerInvariant() };
                item.Click += delegate { Test(k); };
                test.Items.Add(item);
            }
            root.Items.Add(test);
            MenuItem clear = new MenuItem { Header = "Descartar todas" };
            clear.Click += delegate { Manager.DismissAll(); };
            root.Items.Add(clear);
            MenuItem folder = new MenuItem { Header = "Abrir carpeta de configuración" };
            folder.Click += delegate { SignalCards.Open(Settings.Folder); };
            root.Items.Add(folder);
            root.SubmenuOpened += delegate { UpdateStatus(); };
            return root;
        }

        /// <summary>Submenu for the tray icon.</summary>
        internal NativeMenuItem TrayItem()
        {
            NativeMenu menu = new NativeMenu();
            status = new NativeMenuItem(StatusText()) { IsEnabled = false };
            menu.Items.Add(status);
            menu.Items.Add(TrayCheck("Notificaciones", () => Settings.Enabled, SetEnabled));
            menu.Items.Add(TrayCheck("Sonido", () => Settings.Sound, v => { Settings.Sound = v; Settings.Save(); }));
            menu.Items.Add(TrayCheck("Modo silencioso", () => Settings.Silent, v => { Settings.Silent = v; Settings.Save(); if (v) sound.Stop(); }));
            NativeMenuItem test = new NativeMenuItem("Enviar notificación de prueba");
            test.Click += delegate { Test(SignalKind.TurnEnded); };
            menu.Items.Add(test);
            NativeMenuItem clear = new NativeMenuItem("Descartar todas");
            clear.Click += delegate { Manager.DismissAll(); };
            menu.Items.Add(clear);
            menu.Opening += delegate { UpdateStatus(); };
            return new NativeMenuItem("Capri Signal") { Menu = menu };
        }

        private static MenuItem Check(string header, Func<bool> get, Action<bool> set)
        {
            MenuItem item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.CheckBox, IsChecked = get() };
            item.Click += delegate { bool next = !get(); set(next); item.IsChecked = get(); };
            item.AttachedToVisualTree += delegate { item.IsChecked = get(); };
            return item;
        }

        private static NativeMenuItem TrayCheck(string header, Func<bool> get, Action<bool> set)
        {
            NativeMenuItem item = new NativeMenuItem(header) { ToggleType = NativeMenuItemToggleType.CheckBox, IsChecked = get() };
            item.Click += delegate { set(!get()); item.IsChecked = get(); };
            return item;
        }
    }
}
