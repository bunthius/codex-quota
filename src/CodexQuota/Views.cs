using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CodexQuota.Core;

namespace CodexQuota;

public static class Theme
{
    public static readonly Brush Ink = Brush("#20332E");
    public static readonly Brush Muted = Brush("#6F7C77");
    public static readonly Brush Line = Brush("#E3E9E3");
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    public static Brush Accent(double? remaining, bool stale) => stale || remaining is null ? Brush("#86928E")
        : remaining <= 10 ? Brush("#C33C42") : remaining <= 30 ? Brush("#B07821") : Brush("#258D71");
    public static TextBlock Text(string text, double size = 13, Brush? color = null, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Ink, FontWeight = weight ?? FontWeights.Normal,
        FontFamily = new FontFamily("Segoe UI"), TextWrapping = TextWrapping.Wrap
    };
    public static Button Button(string text, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(13, 8, 13, 8), FontSize = 12, Cursor = Cursors.Hand,
            Background = primary ? Brush("#258D71") : Brush("#F2F5F0"),
            Foreground = primary ? Brushes.White : Ink, BorderBrush = Line,
            BorderThickness = new Thickness(primary ? 0 : 1)
        };
        button.Click += (_, _) => action();
        AutomationProperties.SetName(button, text);
        return button;
    }
    public static Border Surface(UIElement child, double radius = 22) => new()
    {
        Background = new LinearGradientBrush(Color.FromArgb(252, 253, 254, 249), Color.FromArgb(248, 237, 245, 237), 65),
        CornerRadius = new CornerRadius(radius), BorderBrush = Brush("#D7E3D9"), BorderThickness = new Thickness(1),
        Child = child, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Opacity = .17, Color = Colors.Black }
    };
}

public sealed class QuotaRing : FrameworkElement
{
    public double? Remaining { get; private set; }
    public bool Stale { get; private set; }
    public QuotaRing() { Width = 100; Height = 100; }
    public void Update(double? remaining, bool stale) { Remaining = remaining; Stale = stale; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var center = new Point(50, 50);
        dc.DrawEllipse(null, new Pen(Theme.Brush("#DFE8E0"), 6), center, 43, 43);
        if (Remaining is > 0)
        {
            var pen = new Pen(Theme.Accent(Remaining, Stale), 6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            if (Remaining >= 100) dc.DrawEllipse(null, pen, center, 43, 43);
            else
            {
                var radians = Remaining.Value / 100 * Math.PI * 2;
                var end = new Point(50 + 43 * Math.Sin(radians), 50 - 43 * Math.Cos(radians));
                var geometry = new StreamGeometry();
                using (var c = geometry.Open())
                {
                    c.BeginFigure(new Point(50, 7), false, false);
                    c.ArcTo(end, new Size(43, 43), 0, Remaining > 50, SweepDirection.Clockwise, true, false);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }
        var number = Remaining is { } p ? Math.Floor(p).ToString(CultureInfo.InvariantCulture) : "—";
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Display"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 32, Theme.Ink, dpi);
        dc.DrawText(text, new Point(50 - text.Width / 2, 24));
        var caption = new FormattedText(Remaining is null ? "" : "% LEFT", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, Theme.Muted, dpi);
        dc.DrawText(caption, new Point(50 - caption.Width / 2, 62));
        AutomationProperties.SetName(this, Remaining is { } percent ? $"{Math.Floor(percent)} percent remaining" : "Quota unavailable");
    }
}

public sealed class Widget : Window
{
    private readonly TextBlock label = Theme.Text("Connecting", 12, Theme.Muted);
    private readonly TextBlock reset = Theme.Text("Reading your allowance", 11, Theme.Muted);
    private readonly QuotaRing ring = new();
    private readonly TextBlock live = Theme.Text("●", 9, Theme.Muted);
    private readonly Settings settings;
    private Point? mouseDown;
    public event Action? DetailsRequested;
    public event Action? Dismissed;
    public Widget(Settings settings)
    {
        this.settings = settings;
        Title = "Codex Quota"; Width = 192; Height = 214;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        var stack = new StackPanel { Margin = new Thickness(18, 11, 18, 13) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var hide = new Button { Content = "×", BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Foreground = Theme.Muted, Width = 20, Height = 20, FontSize = 17, ToolTip = "Hide until the next app session" };
        AutomationProperties.SetName(hide, "Hide quota widget");
        hide.Click += (_, _) => Dismissed?.Invoke();
        DockPanel.SetDock(hide, Dock.Right); header.Children.Add(hide);
        DockPanel.SetDock(live, Dock.Right); live.VerticalAlignment = VerticalAlignment.Center;
        live.Margin = new Thickness(0, 0, 6, 0); header.Children.Add(live);
        var title = Theme.Text("CODEX", 10, Theme.Muted, FontWeights.SemiBold);
        title.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(title);
        stack.Children.Add(header);
        var detailButton = new Button { Content = ring, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(0), Cursor = Cursors.Hand, ToolTip = "Show all quota windows" };
        AutomationProperties.SetName(detailButton, "Show quota details");
        detailButton.Click += (_, _) => DetailsRequested?.Invoke();
        stack.Children.Add(detailButton);
        label.HorizontalAlignment = HorizontalAlignment.Center; label.Margin = new Thickness(0, 3, 0, 0); stack.Children.Add(label);
        reset.HorizontalAlignment = HorizontalAlignment.Center; reset.Margin = new Thickness(0, 4, 0, 0); stack.Children.Add(reset);
        Content = new Border { Padding = new Thickness(9), Child = Theme.Surface(stack) };
        header.MouseLeftButtonDown += (_, e) => { mouseDown = e.GetPosition(this); };
        header.MouseMove += (_, e) =>
        {
            if (mouseDown is { } origin && e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(this) - origin).Length > 3)
            {
                mouseDown = null; DragMove(); SavePosition();
            }
        };
        header.MouseLeftButtonUp += (_, _) => mouseDown = null;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Dismissed?.Invoke(); };
        Loaded += (_, _) => EnsureOnScreen();
    }
    private void SavePosition()
    {
        settings.Left = Left; settings.Top = Top;
        try { settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void EnsureOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(settings.Left is { } left && double.IsFinite(left) ? left : area.Right - Width - 18,
            area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(settings.Top is { } top && double.IsFinite(top) ? top : area.Bottom - Height - 12,
            area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
    public void Update(QuotaSnapshot? snapshot, string? error, bool refreshing)
    {
        var now = DateTimeOffset.UtcNow;
        var window = snapshot?.MainWindow;
        var stale = error is not null || snapshot?.IsStale(now) == true;
        var due = window?.AwaitingReset(now) == true;
        var remaining = due ? null : window?.Remaining;
        ring.Update(remaining, stale);
        live.Foreground = Theme.Accent(remaining, stale);
        live.ToolTip = stale ? "Last known value · reconnecting" : "Connected to your Codex account";
        label.Text = snapshot is null ? refreshing ? "Connecting" : "Quota unavailable"
            : stale ? $"{window?.Label ?? "Usage"} · last known" : $"{window?.Label ?? "Usage"} allowance";
        reset.Text = error is not null ? "Click for connection details" : window?.ResetText(now) ?? "Click for details";
        ToolTip = error ?? "Drag the CODEX header to move. Click the ring for details.";
    }
}

public sealed class DetailsWindow : Window
{
    private readonly StackPanel body = new();
    public event Action? RefreshRequested;
    public event Action? UsageRequested;
    public DetailsWindow()
    {
        Title = "Codex Quota — usage details"; Width = 370; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 30);
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        body.Margin = new Thickness(23, 20, 23, 20);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height - 60) };
        Content = new Border { Padding = new Thickness(10), Child = Theme.Surface(scroll) };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Deactivated += (_, _) => Hide();
    }
    public void Update(QuotaSnapshot? snapshot, string? error, bool refreshing)
    {
        body.Children.Clear();
        var header = new DockPanel();
        var close = Theme.Button("×", Hide); close.Padding = new Thickness(8, 0, 8, 2);
        AutomationProperties.SetName(close, "Close quota details");
        DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(Theme.Text("Your allowance", 22, weight: FontWeights.SemiBold));
        body.Children.Add(header);
        var subtitle = Theme.Text("Codex · linked to your ChatGPT account", 12, Theme.Muted);
        subtitle.Margin = new Thickness(0, 5, 0, 18); body.Children.Add(subtitle);
        var now = DateTimeOffset.UtcNow;
        var stale = error is not null || snapshot?.IsStale(now) == true;
        if (snapshot is not null)
        {
            foreach (var bucket in snapshot.Buckets)
            {
                var title = Theme.Text(bucket.Name, 14, weight: FontWeights.SemiBold);
                title.Margin = new Thickness(0, 0, 0, 10); body.Children.Add(title);
                foreach (var window in bucket.Windows)
                {
                    var due = window.AwaitingReset(now);
                    var remaining = due ? null : window.Remaining;
                    var row = new DockPanel();
                    var value = Theme.Text(remaining is { } p ? $"{Math.Floor(p)}% left" : "Unavailable", 12, Theme.Accent(remaining, stale), FontWeights.SemiBold);
                    DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
                    row.Children.Add(Theme.Text(window.Label, 12)); body.Children.Add(row);
                    var progress = new ProgressBar { Minimum = 0, Maximum = 100, Value = remaining ?? 0, Height = 5,
                        Foreground = Theme.Accent(remaining, stale), Background = Theme.Line, BorderThickness = new Thickness(0),
                        Margin = new Thickness(0, 7, 0, 5) };
                    AutomationProperties.SetName(progress, $"{bucket.Name} {window.Label} remaining"); body.Children.Add(progress);
                    var reset = Theme.Text(window.ResetText(now), 11, Theme.Muted); reset.Margin = new Thickness(0, 0, 0, 15);
                    if (window.ResetsAt is { } timestamp) reset.ToolTip = timestamp.ToLocalTime().ToString("f");
                    body.Children.Add(reset);
                }
                if (bucket.Windows.Count == 0) body.Children.Add(Theme.Text("No quota windows reported", 12, Theme.Muted));
            }
        }
        else
        {
            var empty = Theme.Text(refreshing ? "Connecting to Codex…" : "Waiting for quota data", 14);
            empty.Margin = new Thickness(0, 10, 0, 18); body.Children.Add(empty);
        }
        if (error is not null)
        {
            var note = Theme.Text(error + (snapshot is not null ? " Values shown are last known." : ""), 12, Theme.Brush("#9A541D"));
            note.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(note);
        }
        var status = Theme.Text(refreshing ? "Refreshing…" : snapshot is { } s
            ? $"{(stale ? "Last successful update" : "Updated")} {s.FetchedAt.ToLocalTime():h:mm tt}" : "Refreshes automatically", 11, Theme.Muted);
        status.Margin = new Thickness(0, 0, 0, 12); body.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = Theme.Button("Refresh", () => RefreshRequested?.Invoke(), true); refresh.IsEnabled = !refreshing;
        buttons.Children.Add(refresh);
        var usage = Theme.Button("Open usage page ↗", () => UsageRequested?.Invoke()); usage.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(usage); body.Children.Add(buttons);
        var footer = Theme.Text("Widget and startup options are in the tray menu.", 10, Theme.Muted);
        footer.Margin = new Thickness(0, 14, 0, 0); body.Children.Add(footer);
    }
    public void Present()
    {
        Show(); UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - ActualWidth - 16);
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 10);
        Activate();
    }
}
