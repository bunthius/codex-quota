using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexQuota.Core;

namespace CodexQuota;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon notify;
    private Icon? current;
    private string? lastKey;
    public event Action? Clicked;
    public TrayIcon(ContextMenuStrip menu)
    {
        notify = new NotifyIcon { ContextMenuStrip = menu, Text = "Codex Quota · connecting" };
        notify.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Clicked?.Invoke(); };
        Update(null, null); notify.Visible = true;
    }
    public void Update(QuotaSnapshot? snapshot, string? error)
    {
        var now = DateTimeOffset.UtcNow;
        var window = snapshot?.MainWindow;
        var remaining = window?.AwaitingReset(now) == true ? null : window?.Remaining;
        var stale = error is not null || snapshot?.IsStale(now) == true;
        var number = remaining is { } p ? Math.Floor(p).ToString("0") : "?";
        var color = stale || remaining is null ? Color.FromArgb(144, 156, 150)
            : remaining <= 10 ? Color.FromArgb(242, 104, 110) : remaining <= 30 ? Color.FromArgb(238, 183, 85) : Color.FromArgb(91, 210, 169);
        var key = $"{number}-{color.ToArgb()}";
        if (key != lastKey)
        {
            var next = Draw(number, remaining, color);
            notify.Icon = next; current?.Dispose(); current = next; lastKey = key;
        }
        var tooltip = snapshot is null ? "Codex Quota · " + (error is null ? "connecting" : "unavailable")
            : $"Codex · {number}% left · {window?.Label}" + (stale ? " (last known)" : "") + $"\n{window?.ResetText(now)}";
        notify.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }
    public void Notify(string message) => notify.ShowBalloonTip(4000, "Codex Quota", message, ToolTipIcon.Info);
    internal static void SaveAppIcon(string path)
    {
        using var icon = Draw("C", 71, Color.FromArgb(91, 210, 169));
        using var file = File.Create(path); icon.Save(file);
    }
    private static Icon Draw(string number, double? remaining, Color color)
    {
        using var bitmap = new Bitmap(64, 64);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(245, 27, 38, 33)); g.FillEllipse(background, 1, 1, 62, 62);
        using var track = new Pen(Color.FromArgb(75, 100, 89), 4); g.DrawEllipse(track, 4, 4, 56, 56);
        if (remaining is > 0)
        {
            using var arc = new Pen(color, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(arc, 4, 4, 56, 56, -90, (float)Math.Clamp(remaining.Value / 100 * 360, 0, 360));
        }
        using var font = new Font("Segoe UI", number.Length >= 3 ? 21 : 26, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(number, font, brush, new RectangleF(0, 1, 64, 62), format);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    public void Dispose() { notify.Visible = false; notify.Dispose(); current?.Dispose(); }
}
