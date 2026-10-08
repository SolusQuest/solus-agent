using System.Text.Json;
using SolusAgent.Tools.Api;

namespace CustomTools;

/// <summary>A separate narrow string-transform capability with test-memory-only effect observation.</summary>
public sealed class TransformCapability : IToolCapability
{
    private int effects;
    /// <inheritdoc />
    public string CapabilityId => "text_transform";
    /// <summary>Gets actual transform effects.</summary>
    public int Effects => Volatile.Read(ref effects);
    /// <summary>Transforms text after observing the supplied cancellation.</summary>
    public string Upper(string text, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Interlocked.Increment(ref effects); return text.ToUpperInvariant(); }
}

/// <summary>An interface-only producer; the private guarded implementation has no runtime or provider reference.</summary>
public sealed class TransformTool : IFunctionTool
{
    private readonly Guard inner;
    /// <summary>Constructs the unrelated tool with optional effect-free rejection or controlled effect behavior.</summary>
    public TransformTool(bool rejectDomain = false, int maximumResultBytes = 128,
        Func<ToolCall, TransformCapability, CancellationToken, ValueTask<ToolOutput>>? effect = null)
        => inner = new(rejectDomain, maximumResultBytes, effect);
    /// <summary>Gets the closed scalar string profile.</summary>
    public const string Schema = "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}},\"required\":[\"text\"],\"additionalProperties\":false}";
    /// <inheritdoc />
    public ToolDescriptor Descriptor => inner.Descriptor;
    /// <inheritdoc />
    public ToolPreparation Prepare(ToolCall call) => inner.Prepare(call);
    /// <inheritdoc />
    public ToolError ValidateInvocation(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability) =>
        inner.ValidateInvocation(prepared, expectedCall, capability);
    /// <inheritdoc />
    public ValueTask<ToolResult> InvokeAsync(PreparedToolInvocation prepared, ToolCall expectedCall, IToolCapability? capability,
        CancellationToken cancellationToken = default) => inner.InvokeAsync(prepared, expectedCall, capability, cancellationToken);

    private sealed class Guard(bool rejectDomain, int maximumResultBytes,
        Func<ToolCall, TransformCapability, CancellationToken, ValueTask<ToolOutput>>? effect)
        : FunctionTool<TransformCapability>(new("transform", "Synthetic uppercase transform", ToolSchema.Parse(Schema),
            ToolSchema.Parse(Schema), "text_transform", ToolEffect.ReadOnly, maximumResultBytes: maximumResultBytes))
    {
        protected override bool ValidateArguments(JsonElement arguments) => !rejectDomain;
        protected override ValueTask<ToolOutput> InvokeCoreAsync(ToolCall call, TransformCapability capability, CancellationToken token)
        {
            if (effect is not null) return effect(call, capability, token);
            using var json = JsonDocument.Parse(call.ArgumentsJson);
            var transformed = capability.Upper(json.RootElement.GetProperty("text").GetString()!, token);
            return ValueTask.FromResult(ToolOutput.Success(call, JsonSerializer.Serialize(new { text = transformed })));
        }
    }
}
