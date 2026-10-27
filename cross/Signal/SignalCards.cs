// Capri Signal — provisional notification UI (native Avalonia controls, no images).
// One transparent, always-on-top, non-activating overlay window stacked in the bottom-right corner.
// Designed to be swapped later for Capri's own "incoming call" sprite animation: everything visual lives here.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;

namespace CapriDesktop.Signal
{
    internal sealed class SignalCards : Window
    {
        internal const double CardWidth = 344;
        private static readonly Color Magenta = Color.FromRgb(255, 46, 196);
        private readonly NotificationManager manager;
        private readonly StackPanel stack = new StackPanel { Spacing = 10 };
        private bool glow;

        internal SignalCards(NotificationManager manager)
        {
            this.manager = manager;
            Look.Overlay(this, "Capri Signal");
            SizeToContent = SizeToContent.WidthAndHeight;
            Content = new Border { Padding = new Thickness(14), Child = stack };
        }

        internal static Color Accent(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.NeedsInput:
                case SignalKind.PermissionRequired: return Magenta;
                case SignalKind.Failed: return Look.Red;
                case SignalKind.Started: return Look.Dim;
                default: return Look.Cyan;
            }
        }

        internal static string Label(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.Started: return "EN CURSO";
                case SignalKind.TurnEnded: return "TURNO TERMINADO";
                case SignalKind.Completed: return "TAREA COMPLETADA";
                case SignalKind.NeedsInput: return "ESPERA TU RESPUESTA";
                case SignalKind.PermissionRequired: return "PIDE AUTORIZACIÓN";
                default: return "ERROR";
            }
        }

        private static string Glyph(SignalKind kind)
        {
            switch (kind)
            {
                case SignalKind.NeedsInput: return "?";
                case SignalKind.PermissionRequired: return "!";
                case SignalKind.Failed: return "×";
                case SignalKind.Started: return "›";
                default: return "✓";
            }
        }

        /// <summary>Rebuilds the stack from the manager. Call on the UI thread.</summary>
        internal void Refresh()
        {
            List<SignalNotice> visible = manager.Visible();
            stack.Children.Clear();
            foreach (SignalNotice notice in visible) stack.Children.Add(Card(notice));
            int waiting = manager.Waiting;
            if (waiting > 0)
                stack.Children.Add(new TextBlock { Text = "+" + waiting + " en cola", FontFamily = Look.Mono, FontSize = 12, Foreground = new SolidColorBrush(Look.Dim), HorizontalAlignment = HorizontalAlignment.Right });
            if (visible.Count == 0) { if (IsVisible) Hide(); return; }
            if (!IsVisible) Show();
            Place();
        }

        /// <summary>Soft pulse on cards that are waiting for you.</summary>
        internal void Pulse()
        {
            glow = !glow;
            foreach (Control child in stack.Children)
            {
                Border card = child as Border;
                if (card == null || !(card.Tag is SignalNotice)) continue;
                SignalNotice notice = (SignalNotice)card.Tag;
                if (!notice.Sticky) continue;
                Color c = Accent(notice.Event.Kind);
                card.BoxShadow = new BoxShadows(new BoxShadow { Blur = glow ? 22 : 10, Spread = glow ? 1 : 0, Color = Color.FromArgb(glow ? (byte)150 : (byte)80, c.R, c.G, c.B) });
            }
        }

        internal void Place()
        {
            Screen screen = Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
            if (screen == null) return;
            PixelRect area = screen.WorkingArea;
            double scale = screen.Scaling;
            Size size = DesiredSize.Width > 0 ? DesiredSize : new Size(CardWidth + 28, 160);
            int wide = (int)Math.Ceiling(Math.Max(Bounds.Width, size.Width) * scale);
            int tall = (int)Math.Ceiling(Math.Max(Bounds.Height, size.Height) * scale);
            if (Bounds.Height > 0) tall = (int)Math.Ceiling(Bounds.Height * scale);
            Position = new PixelPoint(area.Right - wide - (int)(8 * scale), area.Bottom - tall - (int)(8 * scale));
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            Place();
        }

        private Border Card(SignalNotice notice)
        {
            SignalEvent e = notice.Event;
            Color accent = Accent(e.Kind);
            SolidColorBrush accentBrush = new SolidColorBrush(accent);

            TextBlock kind = new TextBlock { Text = Glyph(e.Kind) + "  " + Label(e.Kind), FontFamily = Look.Mono, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = accentBrush };
            TextBlock time = new TextBlock { Text = e.Time.ToString("HH:mm") + (notice.Count > 1 ? "  ×" + notice.Count : ""), FontFamily = Look.Mono, FontSize = 11, Foreground = new SolidColorBrush(Look.Dim), HorizontalAlignment = HorizontalAlignment.Right };
            Grid top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            top.Children.Add(kind);
            Grid.SetColumn(time, 1);
            top.Children.Add(time);

            StackPanel body = new StackPanel { Spacing = 4 };
            body.Children.Add(top);
            body.Children.Add(new TextBlock
            {
                Text = SignalRouter.Display(e.Source) + (e.Project != null ? "  ·  " + e.Project : ""),
                FontFamily = Look.Mono, FontSize = 15, Foreground = new SolidColorBrush(Look.Ink), TextTrimming = TextTrimming.CharacterEllipsis
            });
            string waiting = e.Kind == SignalKind.NeedsInput ? "El agente está esperando tu respuesta." : e.Kind == SignalKind.PermissionRequired ? "El agente necesita que autorices una acción." : null;
            if (waiting != null) body.Children.Add(new TextBlock { Text = waiting, FontFamily = Look.Mono, FontSize = 12, Foreground = accentBrush, TextWrapping = TextWrapping.Wrap });
            if (e.Kind == SignalKind.TurnEnded) body.Children.Add(new TextBlock { Text = "Revisa el resultado: fin de turno no garantiza que todo funcione.", FontFamily = Look.Mono, FontSize = 11, Foreground = new SolidColorBrush(Look.Dim), TextWrapping = TextWrapping.Wrap });
            if (e.Message != null)
                body.Children.Add(new TextBlock
                {
                    Text = e.Message, FontFamily = Look.Mono, FontSize = 13, Foreground = new SolidColorBrush(Look.Neon),
                    TextWrapping = TextWrapping.Wrap, MaxLines = notice.Expanded ? 12 : 2, TextTrimming = TextTrimming.CharacterEllipsis
                });
            if (notice.Expanded)
            {
                List<string> facts = new List<string>();
                if (e.Detail != null) facts.Add("tipo: " + e.Detail);
                if (notice.Count > 1) facts.Add("antes: " + Label(notice.PreviousKind).ToLowerInvariant());
                if (e.TaskId != null) facts.Add("sesión: " + (e.TaskId.Length > 14 ? e.TaskId.Substring(0, 14) + "…" : e.TaskId));
                if (e.ProjectPath != null) facts.Add("ruta: " + e.ProjectPath);
                facts.Add("recibido: " + notice.Created.ToString("HH:mm:ss"));
                body.Children.Add(new TextBlock { Text = string.Join("\n", facts), FontFamily = Look.Mono, FontSize = 11, Foreground = new SolidColorBrush(Look.Dim), TextWrapping = TextWrapping.Wrap });
            }

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
            actions.Children.Add(Action(notice.Expanded ? "OCULTAR" : "VER", Look.Cyan, delegate { manager.Toggle(notice.Id); }));
            if (CanOpen(e.ProjectPath)) actions.Children.Add(Action("ABRIR CARPETA", Look.Cyan, delegate { Open(e.ProjectPath); }));
            actions.Children.Add(Action("DESCARTAR", accent, delegate { manager.Dismiss(notice.Id); }));
            body.Children.Add(actions);

            Grid row = new Grid { ColumnDefinitions = new ColumnDefinitions("3,*") };
            row.Children.Add(new Border { Background = accentBrush, Margin = new Thickness(0, 2, 0, 2) });
            Border pad = new Border { Padding = new Thickness(12, 10, 12, 10), Child = body };
            Grid.SetColumn(pad, 1);
            row.Children.Add(pad);

            return new Border
            {
                Tag = notice,
                Width = CardWidth,
                Background = new SolidColorBrush(Color.FromArgb(242, Look.Panel.R, Look.Panel.G, Look.Panel.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, accent.R, accent.G, accent.B)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(3),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 14, Color = Color.FromArgb(90, accent.R, accent.G, accent.B) }),
                Child = row,
            };
        }

        private static Button Action(string text, Color colour, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
        {
            Button button = new Button
            {
                Content = text, FontFamily = Look.Mono, FontSize = 11, Padding = new Thickness(10, 4, 10, 4),
                Foreground = new SolidColorBrush(colour), Background = new SolidColorBrush(Color.FromArgb(30, colour.R, colour.G, colour.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, colour.R, colour.G, colour.B)), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2), Cursor = new Cursor(StandardCursorType.Hand), Focusable = false,
            };
            button.Click += click;
            return button;
        }

        internal static bool CanOpen(string path)
        {
            try { return !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && Directory.Exists(path); }
            catch (Exception) { return false; }
        }

        /// <summary>Opens the project folder in the file manager. Only an existing local directory is ever passed — never a command.</summary>
        internal static void Open(string path)
        {
            if (!CanOpen(path)) return;
            try
            {
                ProcessStartInfo start;
                if (OperatingSystem.IsWindows()) start = new ProcessStartInfo("explorer.exe");
                else if (OperatingSystem.IsMacOS()) start = new ProcessStartInfo("open");
                else start = new ProcessStartInfo("xdg-open");
                start.ArgumentList.Add(Path.GetFullPath(path));
                start.UseShellExecute = false;
                Process.Start(start);
            }
            catch (Exception) { }
        }
    }
}
