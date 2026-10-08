using System.Text.Json;

namespace SolusAgent.ConsumerProbes.ScribeHost;

/// <summary>Minimal synthetic correction grammar shared by Host rejection feedback and repair production.</summary>
/// <remarks>Correction content is Host-channel untrusted data. A repair must derive its fact from this data; absent or malformed data never authorizes fabricating a repair. This grammar is test-only and carries no product review semantics.</remarks>
public static class ScribeCandidateCorrection
{
    /// <summary>Formats one rejection correction carrying the Host marker and an optional requested fact as data.</summary>
    /// <param name="requestedFact">The corrected fact the Host requests, or null when the correction supplies no repair data.</param>
    /// <returns>The bounded synthetic correction text.</returns>
    /// <exception cref="ArgumentException">A supplied requested fact is invalid.</exception>
    public static string Format(string? requestedFact) =>
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["marker"] = ScribeBusinessHost.CorrectionCanary,
            ["requestedFact"] = requestedFact is null ? string.Empty : ScribeText.Fact(requestedFact),
        });

    /// <summary>Attempts to parse requested-fact correction data under the exact synthetic grammar.</summary>
    /// <param name="correctionText">Host correction text, or null when no correction was delivered.</param>
    /// <param name="requestedFact">The parsed requested fact, or empty when no usable repair data exists.</param>
    /// <returns><see langword="true"/> only when the exact grammar parsed with a genuine marker and nonempty requested fact.</returns>
    public static bool TryParseRequestedFact(string? correctionText, out string requestedFact)
    {
        requestedFact = string.Empty;
        if (correctionText is null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(correctionText, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? marker = null;
            string? parsed = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                if (property.Name == "marker" && marker is null)
                {
                    marker = property.Value.GetString();
                }
                else if (property.Name == "requestedFact" && parsed is null)
                {
                    parsed = property.Value.GetString();
                }
                else
                {
                    return false;
                }
            }

            if (marker != ScribeBusinessHost.CorrectionCanary || string.IsNullOrEmpty(parsed))
            {
                return false;
            }

            requestedFact = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
