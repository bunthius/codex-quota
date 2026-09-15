using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuota.Core;
using Forms = System.Windows.Forms;

namespace CodexQuota;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--quit"))
        {
            try { using var quit = EventWaitHandle.OpenExisting(@"Local\CodexQuota-Quit"); quit.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        if (args.Length == 2 && args[0] == "--probe")
        {
            try
            {
                var snapshot = Task.Run(() => CodexClient.ReadAsync(Settings.Load().CodexPath, CancellationToken.None)).GetAwaiter().GetResult();
                File.WriteAllText(args[1], JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], JsonSerializer.Serialize(new { error = ex is QuotaConnectionException ? ex.Message : "Probe failed" }));
                return 1;
            }
        }
        var smokeDirectory = args.Length == 2 && args[0] == "--smoke" ? args[1] : null;
        var renderDirectory = args.Length == 2 && args[0] == "--render" ? args[1] : null;
        using var mutex = new Mutex(true, @"Local\CodexQuota-v1" + (smokeDirectory is not null || renderDirectory is not null ? "-diagnostic" : ""), out var first);
        if (!first)
        {
            if (!args.Contains("--startup"))
                try { using var show = EventWaitHandle.OpenExisting(@"Local\CodexQuota-Show"); show.Set(); }
                catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
        try
        {
            var app = new QuotaApplication(smokeDirectory, renderDirectory);
            return app.Run();
        }
        finally { mutex.ReleaseMutex(); }
    }
}

public sealed class QuotaApplication(string? smokeDirectory, string? renderDirectory) : Application
{
    private readonly Settings settings = Settings.Load();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Widget widget = null!;
    private DetailsWindow details = null!;
    private TrayIcon tray = null!;
    private Forms.ContextMenuStrip menu = null!;
    private EventWaitHandle? showEvent;
    private RegisteredWaitHandle? showWait;
    private EventWaitHandle? quitEvent;
    private RegisteredWaitHandle? quitWait;
    private QuotaSnapshot? snapshot;
    private string? error;
    private bool refreshing;
    private bool dismissed;
    private bool appOpen;
    private DateTimeOffset nextRefresh = DateTimeOffset.MinValue;
    private Task<QuotaSnapshot>? activeRead;
    private bool diagnosticFinished;
    private readonly bool diagnostic = smokeDirectory is not null || renderDirectory is not null;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        widget = new Widget(settings); details = new DetailsWindow();
        widget.DetailsRequested += ShowDetails;
        widget.Dismissed += () => { dismissed = true; widget.Hide(); };
        details.RefreshRequested += () => _ = RefreshAsync();
        details.UsageRequested += OpenUsage;
        menu = BuildMenu(); tray = new TrayIcon(menu); tray.Clicked += ShowDetails;
        if (!diagnostic)
        {
            showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexQuota-Show");
            showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => Dispatcher.BeginInvoke(ShowDetails), null, Timeout.Infinite, false);
            quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexQuota-Quit");
            quitWait = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, false);
        }
        timer.Tick += (_, _) => Tick();
        timer.Start();
        if (renderDirectory is not null)
        {
            RenderFixtures(renderDirectory); Shutdown(); return;
        }
        Tick();
    }

    private void Tick()
    {
        var open = DesktopAppOpen();
        if (open && !appOpen) { dismissed = false; nextRefresh = DateTimeOffset.MinValue; }
        appOpen = open;
        var shouldShow = VisibilityPolicy.ShouldShow(settings.WidgetMode, appOpen, dismissed) || diagnostic;
        if (shouldShow && !widget.IsVisible) { widget.EnsureOnScreen(); widget.Show(); }
        else if (!shouldShow && widget.IsVisible) widget.Hide();
        UpdateViews();
        if (!refreshing && DateTimeOffset.UtcNow >= nextRefresh) _ = RefreshAsync();
        WriteHealth();
    }

    private async Task RefreshAsync()
    {
        if (refreshing || lifetime.IsCancellationRequested) return;
        refreshing = true; UpdateViews(true);
        try
        {
            activeRead = Task.Run(() => CodexClient.ReadAsync(settings.CodexPath, lifetime.Token));
            snapshot = await activeRead;
            error = null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        catch (Exception ex)
        { error = ex is QuotaConnectionException ? ex.Message : "Unable to refresh. Check your Codex sign-in and connection."; }
        finally
        {
            refreshing = false;
            // Active widgets stay fresh; a hidden idle tray checks less often.
            nextRefresh = DateTimeOffset.UtcNow.AddSeconds(appOpen || settings.WidgetMode == "always" || error is not null ? 60 : 180);
        }
        if (lifetime.IsCancellationRequested) return;
        UpdateViews(true); WriteHealth();
        if (smokeDirectory is not null && !diagnosticFinished)
        {
            diagnosticFinished = true;
            Directory.CreateDirectory(smokeDirectory);
            details.Update(snapshot, error, false);
            widget.Show(); details.Show(); widget.UpdateLayout(); details.UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            Capture(widget, Path.Combine(smokeDirectory, "widget.png"));
            Capture(details, Path.Combine(smokeDirectory, "details.png"));
            File.WriteAllText(Path.Combine(smokeDirectory, "smoke.json"), JsonSerializer.Serialize(new
            {
                success = error is null && snapshot?.MainWindow?.Remaining is not null,
                error, remaining = snapshot?.MainWindow?.Remaining, windows = snapshot?.Buckets.Sum(b => b.Windows.Count),
                appDetected = appOpen, widgetVisible = widget.IsVisible, detailsVisible = details.IsVisible,
                startupEnabled = CodexQuota.Startup.Enabled, fetchedAt = snapshot?.FetchedAt
            }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(error is null ? 0 : 1);
        }
    }

    private void UpdateViews(bool rebuildDetails = false)
    {
        widget.Update(snapshot, error, refreshing);
        // Don't rebuild a focused panel every tick; refresh actions handle it explicitly.
        if (details.IsVisible && rebuildDetails) details.Update(snapshot, error, refreshing);
        tray.Update(snapshot, error);
    }
    private void ShowDetails()
    {
        details.Update(snapshot, error, refreshing); details.Present();
        if (snapshot is null || snapshot.IsStale(DateTimeOffset.UtcNow)) _ = RefreshAsync();
    }
    private static bool DesktopAppOpen()
    {
        foreach (var name in new[] { "Codex", "ChatGPT" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    try { if (process.MainWindowHandle != IntPtr.Zero) return true; }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var context = new Forms.ContextMenuStrip();
        void Add(string name, Action action) => context.Items.Add(name, null, (_, _) => action());
        Add("Show quota details", ShowDetails);
        Add("Refresh now", () => _ = RefreshAsync());
        context.Items.Add(new Forms.ToolStripSeparator());
        var mode = new Forms.ToolStripMenuItem("Floating widget");
        foreach (var (key, title) in new[] { ("automatic", "When Codex / ChatGPT is open"), ("always", "Always visible"), ("tray", "Tray icon only") })
        {
            var item = new Forms.ToolStripMenuItem(title) { Tag = key };
            item.Click += (_, _) =>
            {
                settings.WidgetMode = key; dismissed = false;
                SaveSettings(); Tick();
            };
            mode.DropDownItems.Add(item);
        }
        context.Items.Add(mode);
        Add("Show widget now", () => { dismissed = false; widget.EnsureOnScreen(); widget.Show(); if (settings.WidgetMode == "tray") { settings.WidgetMode = "always"; SaveSettings(); } });
        Add("Reset widget position", () => { settings.Left = null; settings.Top = null; SaveSettings(); widget.EnsureOnScreen(); });
        var startup = new Forms.ToolStripMenuItem("Start with Windows");
        startup.Click += (_, _) =>
        {
            try { CodexQuota.Startup.Set(!CodexQuota.Startup.Enabled); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { tray.Notify("Windows could not save the startup setting."); }
        };
        context.Items.Add(startup);
        context.Opening += (_, _) =>
        {
            startup.Checked = CodexQuota.Startup.Enabled;
            foreach (Forms.ToolStripMenuItem item in mode.DropDownItems) item.Checked = (string?)item.Tag == settings.WidgetMode;
        };
        context.Items.Add(new Forms.ToolStripSeparator());
        Add("Open usage page", OpenUsage);
        Add("Locate Codex…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Locate codex.exe", Filter = "Codex executable (codex.exe)|codex.exe" };
            if (dialog.ShowDialog() == true) { settings.CodexPath = dialog.FileName; SaveSettings(); _ = RefreshAsync(); }
        });
        context.Items.Add(new Forms.ToolStripSeparator());
        Add("Quit Codex Quota", () => Shutdown());
        return context;
    }
    private void SaveSettings()
    {
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { tray.Notify("Could not save preferences. Changes apply to this session."); }
    }
    private static void OpenUsage()
    {
        try { Process.Start(new ProcessStartInfo("https://chatgpt.com/codex/settings/usage") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { }
    }
    private void WriteHealth()
    {
        if (diagnostic) return;
        try
        {
            Directory.CreateDirectory(Settings.DataDirectory);
            var destination = Path.Combine(Settings.DataDirectory, "health.json");
            File.WriteAllText(destination + ".tmp", JsonSerializer.Serialize(new
            {
                processId = Environment.ProcessId, version = "1.0.0", checkedAt = DateTimeOffset.UtcNow,
                lastSuccess = snapshot?.FetchedAt, remaining = snapshot?.MainWindow?.Remaining,
                error, appDetected = appOpen, widgetVisible = widget.IsVisible, mode = settings.WidgetMode,
                startupEnabled = CodexQuota.Startup.Enabled
            }));
            File.Move(destination + ".tmp", destination, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * 2), (int)Math.Ceiling(window.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private void RenderFixtures(string directory)
    {
        Directory.CreateDirectory(directory);
        TrayIcon.SaveAppIcon(Path.Combine(directory, "codex-quota.ico"));
        var now = DateTimeOffset.UtcNow;
        foreach (var (name, percent, problem) in new (string, double?, string?)[]
        {
            ("healthy", 71, null), ("low", 9, null), ("empty", 0, null),
            ("full", 100, null), ("stale", 23, "Connection timed out. Retrying automatically."),
            ("unavailable", null, "Quota is unavailable. Open Codex and check your sign-in and connection.")
        })
        {
            snapshot = percent is null ? null : new QuotaSnapshot(new[]
            {
                new QuotaBucket("codex", "Codex", new[] { new QuotaWindow(percent, 10080, now.AddDays(3).AddHours(8)) }),
                new QuotaBucket("spark", "GPT-5.3-Codex-Spark", new[] { new QuotaWindow(100, 300, now.AddHours(4)), new QuotaWindow(100, 10080, now.AddDays(6)) })
            }, now);
            error = problem;
            widget.Update(snapshot, error, false); details.Update(snapshot, error, false);
            widget.Show(); details.Show(); widget.UpdateLayout(); details.UpdateLayout();
            Capture(widget, Path.Combine(directory, name + "-widget.png"));
            Capture(details, Path.Combine(directory, name + "-details.png"));
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        timer.Stop(); lifetime.Cancel();
        try { activeRead?.Wait(TimeSpan.FromSeconds(4)); } catch (AggregateException) { }
        showWait?.Unregister(null); showEvent?.Dispose();
        quitWait?.Unregister(null); quitEvent?.Dispose();
        tray?.Dispose(); menu?.Dispose(); lifetime.Dispose();
        base.OnExit(e);
    }
}
