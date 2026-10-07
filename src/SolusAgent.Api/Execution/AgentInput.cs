namespace SolusAgent.Api.Execution;

/// <summary>Identifies the source of untrusted data, without granting control authority.</summary>
public enum AgentInputSource
{
    /// <summary>Repository content supplied as data.</summary>
    Repository,

    /// <summary>Tool output supplied as data.</summary>
    Tool,

    /// <summary>Model output supplied as data.</summary>
    Model,
}

/// <summary>Content that remains untrusted data even when it contains instruction-like text.</summary>
/// <remarks>It cannot select Host policy, required capabilities or execution limits. Do not log or serialize its text as diagnostics.</remarks>
public sealed class AgentInput
{
    /// <summary>Creates data with an explicit source classification.</summary>
    /// <exception cref="ArgumentNullException">Text is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The source is undefined.</exception>
    public AgentInput(AgentInputSource source, string text)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        ArgumentNullException.ThrowIfNull(text);
        Source = source;
        Text = text;
    }

    /// <summary>Gets the data's source; this is descriptive and conveys no policy authority.</summary>
    public AgentInputSource Source { get; }

    /// <summary>Gets the untrusted content, which is excluded from ordinary progress and outcomes.</summary>
    public string Text { get; }

    /// <summary>Returns the source classification without the content.</summary>
    public override string ToString() => $"AgentInput {{ Source = {Source}, Text = <data> }}";
}
