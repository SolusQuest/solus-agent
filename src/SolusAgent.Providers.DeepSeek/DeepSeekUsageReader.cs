using System.Text.Json;
using SolusAgent.Api.Usage;

namespace SolusAgent.Providers.DeepSeek;

internal static class DeepSeekUsageReader
{
    internal static (UsageObservation Usage, bool Valid) Read(JsonElement root)
    {
        var valid = true;
        var matches = root.ValueKind == JsonValueKind.Object
            ? root.EnumerateObject().Where(p => p.Name == "usage").ToArray() : [];
        if (matches.Length == 0) return (new(), true);
        if (matches.Length != 1) return (new(), false);
        var usage = matches[0].Value;
        if (usage.ValueKind == JsonValueKind.Null) return (new(), true);
        if (usage.ValueKind != JsonValueKind.Object) return (new(), false);
        var input = Counter(usage, "prompt_tokens");
        var output = Counter(usage, "completion_tokens");
        var hit = Counter(usage, "prompt_cache_hit_tokens");
        var miss = Counter(usage, "prompt_cache_miss_tokens");
        var cached = Detail("prompt_tokens_details", "cached_tokens");
        var reasoning = Detail("completion_tokens_details", "reasoning_tokens");
        var total = Counter(usage, "total_tokens");
        if (hit is not null && cached is not null && hit != cached) { valid = false; hit = null; cached = null; }
        hit ??= cached;
        if (input is long i && hit is long h && h > i) { valid = false; hit = null; }
        if (input is long i2 && miss is long m && m > i2) { valid = false; miss = null; }
        if (input is long parent && hit is long a && miss is long b && (a > long.MaxValue - b || a + b != parent))
        { valid = false; hit = null; miss = null; }
        if (output is long o && reasoning is long r && r > o) { valid = false; reasoning = null; }
        if (input is long knownInput && output is long knownOutput && total is long knownTotal
            && (knownInput > long.MaxValue - knownOutput || knownInput + knownOutput != knownTotal)) valid = false;
        List<ProviderTokenCounter> counters = [];
        if (hit is long cacheHit) counters.Add(new(ProviderTokenCounterKind.CacheRead, cacheHit, TokenCounterRelationship.IncludedInInput));
        if (miss is long cacheMiss) counters.Add(new(ProviderTokenCounterKind.UncachedInput, cacheMiss, TokenCounterRelationship.IncludedInInput));
        if (reasoning is long thought) counters.Add(new(ProviderTokenCounterKind.Reasoning, thought, TokenCounterRelationship.IncludedInOutput));
        return (new(input, output, counters), valid);

        long? Detail(string name, string counter)
        {
            var entries = usage.EnumerateObject().Where(p => p.Name == name).ToArray();
            if (entries.Length == 0 || entries.Length == 1 && entries[0].Value.ValueKind == JsonValueKind.Null) return null;
            if (entries.Length != 1 || entries[0].Value.ValueKind != JsonValueKind.Object) { valid = false; return null; }
            return Counter(entries[0].Value, counter);
        }
        long? Counter(JsonElement owner, string name)
        {
            var entries = owner.EnumerateObject().Where(p => p.Name == name).ToArray();
            if (entries.Length == 0 || entries.Length == 1 && entries[0].Value.ValueKind == JsonValueKind.Null) return null;
            if (entries.Length != 1 || entries[0].Value.ValueKind != JsonValueKind.Number
                || !entries[0].Value.TryGetInt64(out var count) || count < 0) { valid = false; return null; }
            return count;
        }
    }
}
