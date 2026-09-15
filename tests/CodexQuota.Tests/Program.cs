using System.Text.Json;
using CodexQuota.Core;
using System.Diagnostics;

// An isolated fake CLI lets the transport tests exercise real redirected processes.
if (args.Contains("app-server"))
{
    var scenario = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_SCENARIO");
    var pidPath = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_PID_FILE");
    if (pidPath is not null) File.WriteAllText(pidPath, Environment.ProcessId.ToString());
    var initialized = false;
    while (Console.ReadLine() is { } line)
    {
        using var doc = JsonDocument.Parse(line);
        var method = doc.RootElement.GetProperty("method").GetString();
        if (method == "initialize") Console.WriteLine("""{"id":1,"result":{"userAgent":"test"}}""");
        else if (method == "initialized") initialized = true;
        else if (method == "account/rateLimits/read" && initialized)
        {
            if (scenario == "hang") { await Task.Delay(30000); return 0; }
            if (scenario == "eof") return 0;
            if (scenario == "malformed") { Console.WriteLine("not-json"); continue; }
            if (scenario == "error") { Console.WriteLine("""{"id":2,"error":{"message":"secret_do_not_log"}}"""); continue; }
            Console.WriteLine("""{"method":"account/updated","params":{}}""");
            Console.WriteLine("""{"id":2,"result":{"rateLimits":{"limitId":"codex","primary":{"usedPercent":78,"windowDurationMins":10080}}}}""");
        }
        else return 2;
    }
    return 0;
}

var failures = new List<string>();
var checks = 0;
void Check(string name, bool passed) { checks++; if (!passed) failures.Add(name); }
var now = DateTimeOffset.UtcNow;
QuotaSnapshot Parse(string json) { using var d = JsonDocument.Parse(json); return QuotaSnapshot.Parse(d.RootElement, now); }
var multiple = Parse("""
{"rateLimits":{"limitId":"codex","primary":{"usedPercent":1,"windowDurationMins":300}},
"rateLimitsByLimitId":{
"codex":{"primary":{"usedPercent":25,"windowDurationMins":300},"secondary":{"usedPercent":78,"windowDurationMins":10080,"resetsAt":1900000000}},
"spark":{"limitName":"Spark","primary":{"usedPercent":99,"windowDurationMins":300}}}}
""");
Check("Prefer the authoritative multi-bucket view", multiple.Codex?.Windows[0].Remaining == 75);
Check("Show the most constrained Codex window, keeping Spark separate", multiple.MainWindow?.Remaining == 22);
Check("Use durations rather than assuming primary is five-hour", multiple.MainWindow?.Label == "Weekly");
Check("Preserve model buckets", multiple.Buckets.Count == 2);
Check("Preserve reset timestamp", multiple.MainWindow?.ResetsAt?.ToUnixTimeSeconds() == 1900000000);
var weekly = Parse("""{"rateLimits":{"primary":{"usedPercent":77,"windowDurationMins":10080},"secondary":null}}""");
Check("Legacy weekly-only response", weekly.MainWindow?.Remaining == 23 && weekly.Codex?.Windows.Count == 1);
Check("Missing quota is not zero usage", Parse("""{"rateLimits":{"primary":{"usedPercent":null}}}""").MainWindow?.Remaining is null);
Check("Malformed percentage is unavailable", Parse("""{"rateLimits":{"primary":{"usedPercent":"oops"}}}""").MainWindow?.Remaining is null);
Check("Clamp used percent above 100", Parse("""{"rateLimits":{"primary":{"usedPercent":150}}}""").MainWindow?.Remaining == 0);
Check("Clamp negative usage", Parse("""{"rateLimits":{"primary":{"usedPercent":-4}}}""").MainWindow?.Remaining == 100);
Check("Missing all data is empty", Parse("{}").Buckets.Count == 0);
Check("Missing Codex doesn't relabel Spark as Codex", Parse("""{"rateLimitsByLimitId":{"spark":{"primary":{"usedPercent":4}}}}""").MainWindow is null);
Check("Invalid timestamp is unavailable", Parse("""{"rateLimits":{"primary":{"usedPercent":4,"resetsAt":9223372036854775807}}}""").MainWindow?.ResetsAt is null);
Check("Fresh snapshot", !weekly.IsStale(now.AddMinutes(2)));
Check("Stale snapshot", weekly.IsStale(now.AddMinutes(4)));
Check("Past reset isn't assumed replenished", new QuotaWindow(0, 300, now.AddSeconds(-1)).AwaitingReset(now));
Check("Reset countdown rounds small positive duration up", new QuotaWindow(23, 300, now.AddSeconds(1)).ResetText(now) == "Resets in 1m");
Check("Auto appears with desktop app", VisibilityPolicy.ShouldShow("automatic", true, false));
Check("Auto hides without desktop app", !VisibilityPolicy.ShouldShow("automatic", false, false));
Check("Manual dismissal persists in current session", !VisibilityPolicy.ShouldShow("automatic", true, true));
Check("Always mode doesn't need desktop app", VisibilityPolicy.ShouldShow("always", false, false));
Check("Tray-only mode hides widget", !VisibilityPolicy.ShouldShow("tray", true, false));
var pidFile = Path.GetTempFileName();
var priorScenario = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_SCENARIO");
var priorPid = Environment.GetEnvironmentVariable("CODEX_QUOTA_TEST_PID_FILE");
try
{
    Environment.SetEnvironmentVariable("CODEX_QUOTA_TEST_PID_FILE", pidFile);
    foreach (var scenario in new[] { "normal", "error", "eof", "malformed", "hang" })
    {
        Environment.SetEnvironmentVariable("CODEX_QUOTA_TEST_SCENARIO", scenario);
        using var ct = new CancellationTokenSource(scenario == "hang" ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(10));
        try
        {
            var result = await CodexClient.ReadAsync(Environment.ProcessPath, ct.Token);
            Check("Real transport handshake, notification filtering, and quota read", scenario == "normal" && result.MainWindow?.Remaining == 22);
        }
        catch (QuotaConnectionException ex)
        {
            Check($"Transport handles {scenario} safely", scenario is "error" or "eof" or "malformed" && !ex.Message.Contains("secret_do_not_log"));
        }
        catch (OperationCanceledException) { Check("Caller cancellation stops a hung connection", scenario == "hang"); }
        var pid = int.Parse(File.ReadAllText(pidFile));
        var exited = false;
        try { using var child = Process.GetProcessById(pid); exited = child.WaitForExit(2000); }
        catch (ArgumentException) { exited = true; }
        Check($"Owned subprocess is cleaned up after {scenario}", exited);
    }
}
finally
{
    Environment.SetEnvironmentVariable("CODEX_QUOTA_TEST_SCENARIO", priorScenario);
    Environment.SetEnvironmentVariable("CODEX_QUOTA_TEST_PID_FILE", priorPid);
    File.Delete(pidFile);
}
foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
Console.WriteLine($"{checks - failures.Count}/{checks} checks passed");
return failures.Count == 0 ? 0 : 1;
