using System.Text.Json;

namespace CodexQuota.Core;

public sealed record QuotaWindow(double? Remaining, int? Minutes, DateTimeOffset? ResetsAt)
{
    public string Label => Minutes switch
    {
        10080 => "Weekly", 1440 => "Daily", 300 => "5-hour",
        null => "Usage", > 0 when Minutes % 60 == 0 => $"{Minutes / 60}-hour",
        > 0 => $"{Minutes}-minute", _ => "Usage"
    };
    public bool AwaitingReset(DateTimeOffset now) => ResetsAt is { } reset && now >= reset;
    public string ResetText(DateTimeOffset now)
    {
        if (ResetsAt is not { } reset) return "Reset time unavailable";
        var delta = reset - now;
        if (delta <= TimeSpan.Zero) return "Reset due · refreshing";
        var duration = delta.TotalDays >= 1 ? $"{(int)delta.TotalDays}d {delta.Hours}h"
            : delta.TotalHours >= 1 ? $"{(int)delta.TotalHours}h {delta.Minutes}m"
            : $"{Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes))}m";
        return $"Resets in {duration}";
    }
}

public sealed record QuotaBucket(string Id, string Name, IReadOnlyList<QuotaWindow> Windows);
public sealed record QuotaSnapshot(IReadOnlyList<QuotaBucket> Buckets, DateTimeOffset FetchedAt)
{
    public QuotaBucket? Codex => Buckets.FirstOrDefault(b => b.Id == "codex");
    // Display the most constrained reported Codex window; Spark stays separate.
    public QuotaWindow? MainWindow => Codex?.Windows.OrderBy(w => w.Remaining ?? double.MaxValue).FirstOrDefault();
    public bool IsStale(DateTimeOffset now) => now - FetchedAt > TimeSpan.FromMinutes(3);

    public static QuotaSnapshot Parse(JsonElement root, DateTimeOffset fetchedAt)
    {
        var buckets = new Dictionary<string, QuotaBucket>();
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
            foreach (var item in map.EnumerateObject())
                if (item.Value.ValueKind == JsonValueKind.Object) buckets[item.Name] = ReadBucket(item.Name, item.Value);
        if (root.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            var id = String(legacy, "limitId") ?? "codex";
            if (!buckets.ContainsKey(id)) buckets[id] = ReadBucket(id, legacy);
        }
        return new(buckets.Values.OrderBy(b => b.Id == "codex" ? 0 : 1).ThenBy(b => b.Name).ToArray(), fetchedAt);
    }

    private static QuotaBucket ReadBucket(string id, JsonElement value)
    {
        var windows = new List<QuotaWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!value.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            double? remaining = w.TryGetProperty("usedPercent", out var used) && used.TryGetDoubleSafe(out var n)
                && double.IsFinite(n) ? Math.Clamp(100 - n, 0, 100) : null;
            int? mins = w.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind == JsonValueKind.Number
                && duration.TryGetInt32(out var m) && m > 0 ? m : null;
            DateTimeOffset? reset = null;
            if (w.TryGetProperty("resetsAt", out var timestamp) && timestamp.ValueKind == JsonValueKind.Number
                && timestamp.TryGetInt64(out var t))
                try { reset = DateTimeOffset.FromUnixTimeSeconds(t); } catch (ArgumentOutOfRangeException) { }
            windows.Add(new(remaining, mins, reset));
        }
        return new(id, String(value, "limitName") ?? (id == "codex" ? "Codex" : id), windows);
    }
    private static string? String(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

internal static class JsonExtensions
{
    internal static bool TryGetDoubleSafe(this JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number);
    }
}

public static class VisibilityPolicy
{
    public static bool ShouldShow(string mode, bool appOpen, bool dismissed) =>
        !dismissed && (mode == "always" || (mode == "automatic" && appOpen));
}
