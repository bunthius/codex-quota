using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuota.Core;

public sealed class QuotaConnectionException(string message) : Exception(message);

public static class CodexClient
{
    public static string? FindExecutable(string? configured = null)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var bundled = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(bundled))
        {
            var installed = new DirectoryInfo(bundled).EnumerateDirectories().OrderByDescending(d => d.LastWriteTimeUtc)
                .Select(d => Path.Combine(d.FullName, "codex.exe")).FirstOrDefault(File.Exists);
            if (installed is not null) return installed;
        }
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim('"'), OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public static async Task<QuotaSnapshot> ReadAsync(string? configured, CancellationToken cancellationToken)
    {
        var executable = FindExecutable(configured) ?? throw new QuotaConnectionException("Codex not found. Choose Locate Codex in the tray menu.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new QuotaConnectionException("Could not start the Codex connection.");
            // Drain diagnostics without retaining logs, conversation content, or credentials.
            var drain = DrainAsync(process.StandardError, timeout.Token);
            await SendAsync(process, new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex_quota", title = "Codex Quota", version = "1.0.0" } } }, timeout.Token);
            await ReplyAsync(process, 1, timeout.Token);
            await SendAsync(process, new { method = "initialized", @params = new { } }, timeout.Token);
            await SendAsync(process, new { id = 2, method = "account/rateLimits/read" }, timeout.Token);
            var result = await ReplyAsync(process, 2, timeout.Token);
            var snapshot = QuotaSnapshot.Parse(result, DateTimeOffset.UtcNow);
            if (snapshot.Buckets.Count == 0) throw new QuotaConnectionException("No quota data returned. Sign in to Codex with your ChatGPT account.");
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new QuotaConnectionException("Connection timed out. Retrying automatically."); }
        catch (QuotaConnectionException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        { throw new QuotaConnectionException("Cannot connect to Codex. Check that Codex is installed and signed in."); }
        finally
        {
            if (started)
            {
                try
                {
                    process.StandardInput.Close();
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await process.WaitForExitAsync(shutdown.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or IOException)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
    }

    private static async Task SendAsync(Process p, object message, CancellationToken ct)
    {
        await p.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct);
        await p.StandardInput.FlushAsync(ct);
    }
    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        try { while (await reader.ReadLineAsync(ct) is not null) { } }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    private static async Task<JsonElement> ReplyAsync(Process process, int id, CancellationToken ct)
    {
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var message = doc.RootElement;
            if (!message.TryGetProperty("id", out var replyId) || replyId.ValueKind != JsonValueKind.Number || !replyId.TryGetInt32(out var n) || n != id) continue;
            if (message.TryGetProperty("error", out _))
                throw new QuotaConnectionException("Quota is unavailable. Open Codex and check your sign-in and connection.");
            if (message.TryGetProperty("result", out var result)) return result.Clone();
            throw new QuotaConnectionException("Codex returned an unexpected response.");
        }
        throw new QuotaConnectionException("Codex disconnected. Retrying automatically.");
    }
}
