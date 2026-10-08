using System.Text;
using System.Text.Json;
using SolusAgent.Api.Context;

namespace SolusAgent.ContractTests.ConsumerProbes.Apr;

/// <summary>
/// Implementation-local synthetic prior-context grammar for the APR scenario. This minimized
/// in-memory format is not a runtime saved format, restoration codec or compatibility commitment.
/// Saved bytes stay untrusted data: saved instruction, limit or capability claims never replace the
/// current request, configuration or newly supplied capability objects.
/// </summary>
internal sealed record AprContextState(Guid OriginExecutionId, int Units, int Goal, long Total, IReadOnlyList<string> Notes)
{
    public const int MaximumPayloadBytes = 8192;
    public const int MaximumNotes = 16;
    public const int MaximumNoteCharacters = 256;

    /// <summary>Serializes the synthetic state for one restricted call-scoped capture.</summary>
    public byte[] ToRestrictedPayload()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            origin = OriginExecutionId,
            units = Units,
            goal = Goal,
            finished = Units == Goal,
            total = Total,
            notes = Notes,
        });
        if (payload.Length > MaximumPayloadBytes)
        {
            throw new InvalidOperationException("Synthetic context state exceeds its bounded grammar.");
        }

        return payload;
    }

    /// <summary>Builds the restricted envelope the Host supplies for one synthetic run.</summary>
    public AgentContextEnvelope ToEnvelope(Guid implementationId) =>
        new(implementationId, AprScenarioAgent.FormatVersion, AprScenarioAgent.CompatibilityVersion, ToRestrictedPayload());

    /// <summary>Strictly admits the synthetic grammar; malformed or contradictory bytes yield null.</summary>
    public static AprContextState? TryParse(byte[] payload)
    {
        if (payload.Length is 0 or > MaximumPayloadBytes)
        {
            return null;
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(payload);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!HasFields(root, "origin", "units", "goal", "finished", "total", "notes"))
            {
                return null;
            }

            if (root.GetProperty("origin").ValueKind != JsonValueKind.String
                || !root.GetProperty("origin").TryGetGuid(out var origin) || origin == Guid.Empty
                || !root.GetProperty("units").TryGetInt32(out var units) || units < 0
                || !root.GetProperty("goal").TryGetInt32(out var goal) || goal <= 0 || units > goal
                || !root.GetProperty("total").TryGetInt64(out var total) || total < 0
                || root.GetProperty("finished").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || root.GetProperty("finished").GetBoolean() != (units == goal)
                || root.GetProperty("notes").ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var notes = new List<string>();
            foreach (var entry in root.GetProperty("notes").EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || notes.Count >= MaximumNotes)
                {
                    return null;
                }

                var note = entry.GetString();
                if (note is null || note.Length > MaximumNoteCharacters)
                {
                    return null;
                }

                notes.Add(note);
            }

            return new AprContextState(origin, units, goal, total, notes.ToArray());
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException
            or FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static bool HasFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }
        }

        return names.SetEquals(fields);
    }
}
