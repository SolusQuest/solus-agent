using System.Text;
using System.Text.Json;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.Providers.DeepSeek;

internal static class DeepSeekResponseParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static ProviderResponse Parse(byte[] bytes, HttpResponseMessage http, ProviderRequest request, ProviderObservation observation)
    {
        try
        {
            _ = StrictUtf8.GetCharCount(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            var measured = DeepSeekUsageReader.Read(root);
            observation.CaptureUsage(measured.Usage);
            // Even an error status can carry validated measurements, but never a successful payload.
            if (http.StatusCode != System.Net.HttpStatusCode.OK)
            {
                try { ValidateContent(http); Require(measured.Valid); ValidateTree(root); }
                catch (ProviderContractException) { throw new ProviderFailureException(); }
                throw new ProviderFailureException(DeepSeekFailure.Status(http));
            }
            ValidateContent(http);
            Require(measured.Valid);
            ValidateTree(root);
            Shape(root, "id", "object", "model", "choices", "usage", "created", "system_fingerprint");
            var id = Text(root.GetProperty("id"), 128);
            Require(id.Length != 0 && !id.Any(char.IsControl));
            Require(Text(root.GetProperty("object")) == "chat.completion");
            Require(Text(root.GetProperty("model")) == DeepSeekOptions.Model);
            if (root.TryGetProperty("created", out var created)) Require(created.TryGetInt64(out var time) && time >= 0);
            if (root.TryGetProperty("system_fingerprint", out var fingerprint) && fingerprint.ValueKind != JsonValueKind.Null) _ = Text(fingerprint, 128);
            var choices = root.GetProperty("choices");
            Require(choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() == 1);
            var choice = choices[0]; Shape(choice, "index", "message", "finish_reason", "logprobs");
            Require(choice.GetProperty("index").TryGetInt32(out var index) && index == 0);
            if (choice.TryGetProperty("logprobs", out var logs)) Require(logs.ValueKind == JsonValueKind.Null);
            var finish = Text(choice.GetProperty("finish_reason"));
            // Normalize only the two successful wire finishes. Partial valid-looking data cannot bypass this cut.
            Require(finish is "stop" or "tool_calls");
            var message = choice.GetProperty("message"); Shape(message, "role", "content", "reasoning_content", "tool_calls");
            Require(Text(message.GetProperty("role")) == "assistant");
            var content = message.GetProperty("content");
            string? text = content.ValueKind == JsonValueKind.Null ? null : Text(content);
            List<ToolCall> calls = [];
            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind != JsonValueKind.Null)
            {
                Require(toolCalls.ValueKind == JsonValueKind.Array);
                if (toolCalls.GetArrayLength() > request.Bounds.MaximumToolCalls) throw new ProviderContractException(ProviderError.LimitExceeded);
                foreach (var call in toolCalls.EnumerateArray())
                {
                    Shape(call, "id", "type", "function"); Require(Text(call.GetProperty("type")) == "function");
                    var function = call.GetProperty("function"); Shape(function, "name", "arguments");
                    calls.Add(new(Text(call.GetProperty("id"), ToolLimits.CallIdBytes), Text(function.GetProperty("name"), ToolLimits.IdentifierCharacters),
                        Text(function.GetProperty("arguments"), ToolLimits.PayloadBytes)));
                }
            }
            Require(finish == "stop" ? text is { Length: > 0 } && calls.Count == 0 : calls.Count != 0);
            ProviderContinuation? continuation = null;
            var hasReasoning = message.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind != JsonValueKind.Null;
            if (request.Tools.Count != 0) Require(hasReasoning, ProviderError.ContinuationMismatch);
            if (hasReasoning)
            {
                var reasoningText = Text(reasoning, ProviderLimits.ContinuationBytes);
                if (request.RequiredCapabilities.HasFlag(ProviderCapabilities.Continuation)) continuation = DeepSeekReplay.Encode(request, reasoningText);
            }
            return new(request.Scope, request.Attempt, finish == "stop" ? ProviderFinish.Final : ProviderFinish.ToolCalls, text, calls, continuation);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or DecoderFallbackException or EncoderFallbackException)
        {
            if (http.StatusCode != System.Net.HttpStatusCode.OK) throw new ProviderFailureException();
            throw new ProviderContractException(ProviderError.InvalidResponse);
        }
    }
    private static void ValidateContent(HttpResponseMessage http)
    {
        Require(http.Content.Headers.ContentEncoding.Count == 0);
        var media = http.Content.Headers.ContentType;
        Require(media?.MediaType == "application/json" && (media.CharSet is null || media.CharSet.Equals("utf-8", StringComparison.OrdinalIgnoreCase)));
    }
    private static void ValidateTree(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { Require(names.Add(property.Name)); _ = StrictUtf8.GetByteCount(property.Name); ValidateTree(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var element in node.EnumerateArray()) ValidateTree(element);
        else if (node.ValueKind == JsonValueKind.String) _ = StrictUtf8.GetByteCount(node.GetString()!);
    }
    private static void Shape(JsonElement node, params string[] allowed)
    { Require(node.ValueKind == JsonValueKind.Object); foreach (var property in node.EnumerateObject()) Require(allowed.Contains(property.Name, StringComparer.Ordinal)); }
    private static string Text(JsonElement value, int cap = ProviderLimits.TextBytes)
    {
        Require(value.ValueKind == JsonValueKind.String);
        var result = value.GetString()!;
        if (StrictUtf8.GetByteCount(result) > cap) throw new ProviderContractException(ProviderError.LimitExceeded);
        return result;
    }
    private static void Require(bool condition, ProviderError error = ProviderError.InvalidResponse)
    { if (!condition) throw new ProviderContractException(error); }
}
