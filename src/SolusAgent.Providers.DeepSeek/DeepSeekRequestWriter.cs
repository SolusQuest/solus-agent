using System.Text;
using System.Text.Json;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace SolusAgent.Providers.DeepSeek;

internal static class DeepSeekRequestWriter
{
    internal static byte[] Write(ProviderRequest request, DeepSeekOptions options)
    {
        if (request.Inputs.Count == 0) throw new ProviderContractException(ProviderError.InvalidInput);
        var replayRequired = request.Tools.Count != 0;
        if (replayRequired && !request.RequiredCapabilities.HasFlag(ProviderCapabilities.Continuation))
            throw new ProviderContractException(ProviderError.UnsupportedCapability);
        using var stream = new CappedStream(options.MaximumRequestBodyBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("model", DeepSeekOptions.Model);
            writer.WriteBoolean("stream", false); writer.WriteString("reasoning_effort", "high");
            writer.WriteStartObject("thinking"); writer.WriteString("type", "enabled"); writer.WriteEndObject();
            writer.WriteNumber("max_tokens", options.MaximumTokens); writer.WriteStartArray("messages");
            foreach (var input in request.Inputs)
            {
                writer.WriteStartObject();
                switch (input.Kind)
                {
                    case ProviderInputKind.HostInstruction: WriteText("system", input.Text!); break;
                    case ProviderInputKind.InputData: WriteText("user", input.Text!); break;
                    case ProviderInputKind.ModelData:
                        var model = input.Model!;
                        writer.WriteString("role", "assistant"); writer.WriteString("content", model.Text);
                        if (model.Continuation is { } continuation)
                            writer.WriteString("reasoning_content", DeepSeekReplay.Decode(continuation));
                        else if (replayRequired) throw new ProviderContractException(ProviderError.ContinuationMismatch);
                        if (model.Calls.Count != 0)
                        {
                            writer.WriteStartArray("tool_calls");
                            foreach (var call in model.Calls)
                            {
                                writer.WriteStartObject(); writer.WriteString("id", call.CallId); writer.WriteString("type", "function");
                                writer.WriteStartObject("function"); writer.WriteString("name", call.ToolName);
                                writer.WriteString("arguments", call.ArgumentsJson); writer.WriteEndObject(); writer.WriteEndObject();
                            }
                            writer.WriteEndArray();
                        }
                        break;
                    case ProviderInputKind.ToolResultData:
                        var result = input.ToolResult!;
                        writer.WriteString("role", "tool"); writer.WriteString("tool_call_id", result.Call.CallId);
                        // Failed results remain fixed data, never fabricated successful empty output.
                        writer.WriteString("content", result.Json ?? JsonSerializer.Serialize(new { outcome = result.Outcome.ToString(), error = result.Error.ToString() }));
                        break;
                    default: throw new ProviderContractException(ProviderError.InvalidInput);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (request.Tools.Count != 0)
            {
                writer.WriteStartArray("tools");
                foreach (var tool in request.Tools)
                {
                    writer.WriteStartObject(); writer.WriteString("type", "function"); writer.WriteStartObject("function");
                    writer.WriteString("name", tool.Name); writer.WriteString("description", tool.Description);
                    writer.WritePropertyName("parameters"); writer.WriteRawValue(tool.InputSchema.NormalizedJson);
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject(); writer.Flush();
            void WriteText(string role, string text) { writer.WriteString("role", role); writer.WriteString("content", text); }
        }
        return stream.ToArray();
    }
    private sealed class CappedStream(int cap) : MemoryStream(Math.Min(cap, 4096))
    {
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        private void Check(int count) { if (count > cap - Length) throw new ProviderContractException(ProviderError.LimitExceeded); }
    }
}

internal static class DeepSeekReplay
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static ProviderContinuation Encode(ProviderRequest request, string reasoning)
    {
        var count = StrictUtf8.GetByteCount(reasoning);
        if (count + 1 > request.Bounds.MaximumContinuationBytes) throw new ProviderContractException(ProviderError.LimitExceeded);
        var bytes = new byte[count + 1]; bytes[0] = 1; StrictUtf8.GetBytes(reasoning, bytes.AsSpan(1));
        return new(request.Scope, request.Attempt, bytes);
    }
    internal static string Decode(ProviderContinuation continuation)
    {
        var bytes = continuation.CopyReplayBytes();
        if (bytes[0] != 1) throw new ProviderContractException(ProviderError.ContinuationMismatch);
        try { return StrictUtf8.GetString(bytes.AsSpan(1)); }
        catch (DecoderFallbackException) { throw new ProviderContractException(ProviderError.ContinuationMismatch); }
    }
}
