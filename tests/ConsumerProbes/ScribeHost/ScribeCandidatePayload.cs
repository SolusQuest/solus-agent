using System.Text.Json;

namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>Closed Host payload validation outcomes for the synthetic candidate grammar.</summary>
public enum ScribePayloadValidation
{
    /// <summary>The payload parsed and satisfied every synthetic domain check.</summary>
    Valid,

    /// <summary>The payload is not the exact synthetic shape.</summary>
    InvalidShape,

    /// <summary>The payload names a member outside the manifest selection.</summary>
    UnselectedMember,

    /// <summary>The payload repeats an already accepted member.</summary>
    DuplicateMember,

    /// <summary>The payload carries invalid fact text.</summary>
    InvalidFact,
}

/// <summary>Minimal synthetic candidate payload grammar shared by producer fixtures and Host validation.</summary>
/// <remarks>The grammar is test-only and demonstrates synthetic validation only; it is not a product document, patch or evidence format.</remarks>
public static class ScribeCandidatePayload
{
    /// <summary>Formats one synthetic payload as a JSON object with exactly a member and a fact string.</summary>
    /// <exception cref="ArgumentNullException">The member or fact is null.</exception>
    /// <exception cref="ArgumentException">The member token or fact text is invalid.</exception>
    public static string Format(string member, string fact) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["member"] = ScribeText.Member(member),
            ["fact"] = ScribeText.Fact(fact),
        });

    /// <summary>Attempts to parse one synthetic payload; only structural shape is checked here.</summary>
    /// <param name="payload">Untrusted candidate text.</param>
    /// <param name="member">The parsed member token, or empty when shape validation failed.</param>
    /// <param name="fact">The parsed fact text, or empty when shape validation failed.</param>
    /// <returns><see cref="ScribePayloadValidation.Valid"/> when the exact shape parsed; otherwise <see cref="ScribePayloadValidation.InvalidShape"/>.</returns>
    /// <exception cref="ArgumentNullException">The payload is null.</exception>
    public static ScribePayloadValidation TryParse(string payload, out string member, out string fact)
    {
        ArgumentNullException.ThrowIfNull(payload);
        member = string.Empty;
        fact = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ScribePayloadValidation.InvalidShape;
            }

            string? parsedMember = null;
            string? parsedFact = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return ScribePayloadValidation.InvalidShape;
                }

                if (property.Name == "member" && parsedMember is null)
                {
                    parsedMember = property.Value.GetString();
                }
                else if (property.Name == "fact" && parsedFact is null)
                {
                    parsedFact = property.Value.GetString();
                }
                else
                {
                    return ScribePayloadValidation.InvalidShape;
                }
            }

            if (parsedMember is null || parsedFact is null)
            {
                return ScribePayloadValidation.InvalidShape;
            }

            member = parsedMember;
            fact = parsedFact;
            return ScribePayloadValidation.Valid;
        }
        catch (JsonException)
        {
            return ScribePayloadValidation.InvalidShape;
        }
    }
}
