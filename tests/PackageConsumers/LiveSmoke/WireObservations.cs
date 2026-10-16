using System.Text;
using System.Text.Json;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace PackageConsumers.LiveSmoke;

// Observations of selected predicates, never an alternative admission result or retained wire tree.
internal sealed record WireObservations(bool? JsonValid = null, bool? UsageValid = null, int? Shape = null,
    int? Finish = null, int? Content = null, bool? IdentityValid = null, int? CallError = null, int? ArgumentsError = null)
{
    internal WireObservations Sanitize() => this with { Shape = Bounded(Shape, 6), Finish = Bounded(Finish, 6), Content = Bounded(Content, 3), CallError = Bounded(CallError, 15), ArgumentsError = Bounded(ArgumentsError, 15) };
    private static int? Bounded(int? value, int maximum) => value >= 0 && value <= maximum ? value : null;
    private static readonly UTF8Encoding Strict = new(false, true);
    internal static WireObservations Read(string body, ProviderRequest request)
    {
        if (Strict.GetByteCount(body) > 65536) return new();
        JsonDocument document;
        try { document = JsonDocument.Parse(body, new() { MaxDepth = 16 }); }
        catch (JsonException) { return new(JsonValid: false); }
        using (document)
        {
            var root = document.RootElement;
            try { if (!Tree(root)) return new(JsonValid: false); }
            catch (Exception e) when (e is InvalidOperationException or EncoderFallbackException) { return new(JsonValid: false); }
            var result = new WireObservations(JsonValid: true);
            if (root.ValueKind != JsonValueKind.Object) return result with { Shape = 1 };
            result = result with { UsageValid = Usage(root), Shape = 0 };
            if (!IsShape(root, ["id", "object", "model", "choices", "usage", "created", "system_fingerprint"])) result = result with { Shape = 1 };
            if (!Required(root, ["id", "object", "model", "choices"])) result = Mark(result, 6);
            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                return Mark(result, 2);
            var choice = choices[0];
            if (!IsShape(choice, ["index", "message", "finish_reason", "logprobs"])) result = Mark(result, 2);
            if (!Required(choice, ["index", "message", "finish_reason"])) result = Mark(result, 6);
            if (choice.ValueKind != JsonValueKind.Object) return Mark(result, 2);
            if (choice.TryGetProperty("logprobs", out var logs) && logs.ValueKind != JsonValueKind.Null) result = Mark(result, 2);
            if (choice.TryGetProperty("finish_reason", out var finish)) result = result with { Finish = Text(finish) switch
                { "stop" => 0, "tool_calls" => 1, "length" => 2, "content_filter" => 3, "insufficient_system_resource" => 4, "aborted" => 5, _ => 6 } };
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return Mark(result, 3);
            if (!IsShape(message, ["role", "content", "reasoning_content", "tool_calls"])) result = Mark(result, 3);
            if (!Required(message, ["role", "content"])) result = Mark(result, 6);
            result = result with { Content = !message.TryGetProperty("content", out var content) ? 0 : content.ValueKind switch
                { JsonValueKind.Null => 1, JsonValueKind.String => 2, _ => 3 } };
            var id = root.TryGetProperty("id", out var identity) ? Text(identity) : null;
            var createdValid = !root.TryGetProperty("created", out var created) || created.ValueKind == JsonValueKind.Number && created.TryGetInt64(out var time) && time >= 0;
            var fingerprintValid = !root.TryGetProperty("system_fingerprint", out var fingerprint) || fingerprint.ValueKind == JsonValueKind.Null
                || Text(fingerprint) is { } fp && Strict.GetByteCount(fp) <= 128;
            result = result with { IdentityValid = id is { Length: > 0 } && Strict.GetByteCount(id) <= 128 && !id.Any(char.IsControl)
                && root.TryGetProperty("object", out var obj) && Text(obj) == "chat.completion"
                && root.TryGetProperty("model", out var model) && Text(model) == request.Scope.Model
                && choice.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var number) && number == 0
                && message.TryGetProperty("role", out var role) && Text(role) == "assistant" && createdValid && fingerprintValid };
            if (!message.TryGetProperty("tool_calls", out var calls) || calls.ValueKind == JsonValueKind.Null) return result;
            if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0 || calls.GetArrayLength() > request.Bounds.MaximumToolCalls) return Mark(result, 4);
            int? metadataError = 0, argumentsError = 0; var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls.EnumerateArray())
            {
                if (!IsShape(call, ["id", "type", "function"])) result = Mark(result, 4);
                if (!Required(call, ["id", "type", "function"])) result = Mark(result, 6);
                if (call.ValueKind != JsonValueKind.Object || !call.TryGetProperty("type", out var type) || Text(type) != "function"
                    || !call.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                { result = Mark(result, 4); metadataError = Unknown(metadataError); argumentsError = Unknown(argumentsError); continue; }
                if (!IsShape(function, ["name", "arguments"])) result = Mark(result, 5);
                if (!Required(function, ["name", "arguments"])) result = Mark(result, 6);
                if (!call.TryGetProperty("id", out var callId) || Text(callId) is not { } key
                    || !function.TryGetProperty("name", out var name) || Text(name) is not { } toolName
                    || !function.TryGetProperty("arguments", out var arguments) || Text(arguments) is not { } raw)
                { metadataError = Unknown(metadataError); argumentsError = Unknown(argumentsError); continue; }
                try
                {
                    var actual = new ToolCall(key, toolName, raw);
                    if (!ids.Add(actual.CallId)) result = Mark(result, 4);
                    var descriptor = request.Tools.SingleOrDefault(tool => tool.Name == actual.ToolName);
                    if (descriptor is null) argumentsError = Unknown(argumentsError);
                    else argumentsError = Merge(argumentsError, (int)descriptor.InputSchema.Validate(actual.ArgumentsJson, descriptor.MaximumArgumentBytes));
                }
                catch (ToolContractException e) { metadataError = Merge(metadataError, (int)e.Error); argumentsError = Unknown(argumentsError); }
            }
            return result with { CallError = metadataError, ArgumentsError = argumentsError };
        }
    }
    private static int? Unknown(int? value) => value is > 0 ? value : null;
    private static int? Merge(int? value, int next) => value is > 0 ? value : next > 0 ? next : value;
    private static WireObservations Mark(WireObservations value, int shape) => value.Shape is null or 0 ? value with { Shape = shape } : value;
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Required(JsonElement value, string[] names) => value.ValueKind == JsonValueKind.Object && names.All(name => value.TryGetProperty(name, out _));
    private static bool IsShape(JsonElement value, string[] names) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(p => names.Contains(p.Name, StringComparer.Ordinal));
    private static bool Tree(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) { if (!names.Add(p.Name)) return false; _ = Strict.GetByteCount(p.Name); if (!Tree(p.Value)) return false; }
        }
        else if (value.ValueKind == JsonValueKind.Array) { foreach (var item in value.EnumerateArray()) if (!Tree(item)) return false; }
        else if (value.ValueKind == JsonValueKind.String) _ = Strict.GetByteCount(value.GetString()!);
        return true;
    }
    private static bool? Usage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) return false;
        var valid = true;
        long? Counter(JsonElement owner, string name)
        {
            if (!owner.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
            if (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var count) && count >= 0) return count;
            valid = false; return null;
        }
        long? Detail(string name, string key)
        {
            if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
            if (field.ValueKind != JsonValueKind.Object) { valid = false; return null; }
            return Counter(field, key);
        }
        var input = Counter(value, "prompt_tokens"); var output = Counter(value, "completion_tokens");
        var hit = Counter(value, "prompt_cache_hit_tokens"); var miss = Counter(value, "prompt_cache_miss_tokens");
        var cached = Detail("prompt_tokens_details", "cached_tokens"); var reasoning = Detail("completion_tokens_details", "reasoning_tokens");
        var total = Counter(value, "total_tokens");
        if (hit is not null && cached is not null && hit != cached) { valid = false; hit = null; cached = null; }
        hit ??= cached;
        if (input is long i && hit is long h && h > i) { valid = false; hit = null; }
        if (input is long j && miss is long m && m > j) { valid = false; miss = null; }
        if (input is long parent && hit is long a && miss is long b && (a > long.MaxValue - b || a + b != parent)) valid = false;
        if (output is long o && reasoning is long r && r > o) valid = false;
        if (input is long first && output is long second && total is long sum && (first > long.MaxValue - second || first + second != sum)) valid = false;
        return valid;
    }
}

internal sealed class WireObserver(bool fail = false)
{
    private ProviderRequest? request;
    internal WireObservations Value { get; private set; } = new();
    internal void Begin(ProviderRequest value) { request = value; Value = new(); }
    internal void End() => request = null;
    internal void Observe(string body)
    {
        try { if (fail) throw new InvalidOperationException(); Value = request is null ? new() : WireObservations.Read(body, request); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException)) { Value = new(); }
    }
}
