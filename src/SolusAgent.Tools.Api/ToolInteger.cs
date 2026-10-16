namespace SolusAgent.Tools.Api;

/// <summary>Exact, bounded reading of the tool profile's signed Int64 JSON numbers.</summary>
public static class ToolInteger
{
    /// <summary>Reads one JSON number token, including integral decimal/exponent spellings, without rounding or rewriting it.</summary>
    /// <remarks>Whitespace, fractions, overflow, invalid encoding/syntax and tokens above the payload byte ceiling return false.
    /// Work is bounded by token length, including for very large exponents. A null argument is a caller precondition error.</remarks>
    public static bool TryParse(string jsonNumber, out long value)
    {
        ArgumentNullException.ThrowIfNull(jsonNumber);
        value = 0;
        try { ToolBoundary.Text(jsonNumber, ToolLimits.PayloadBytes); }
        catch (ToolContractException) { return false; }
        var index = 0;
        var negative = jsonNumber.Length > 0 && jsonNumber[0] == '-';
        if (negative) index++;
        var start = index;
        if (index == jsonNumber.Length) return false;
        if (jsonNumber[index] == '0') index++;
        else
        {
            if (jsonNumber[index] is < '1' or > '9') return false;
            while (index < jsonNumber.Length && Digit(jsonNumber[index])) index++;
        }
        var fractionalDigits = 0;
        if (index < jsonNumber.Length && jsonNumber[index] == '.')
        {
            var fraction = ++index;
            while (index < jsonNumber.Length && Digit(jsonNumber[index])) index++;
            fractionalDigits = index - fraction;
            if (fractionalDigits == 0) return false;
        }
        var end = index;
        var exponent = 0;
        if (index < jsonNumber.Length && jsonNumber[index] is 'e' or 'E')
        {
            index++;
            var exponentNegative = index < jsonNumber.Length && jsonNumber[index] == '-';
            if (index < jsonNumber.Length && jsonNumber[index] is '+' or '-') index++;
            var digits = index;
            // Beyond this magnitude, a nonzero mantissa cannot fit Int64 or become integral.
            var bound = jsonNumber.Length + 20;
            while (index < jsonNumber.Length && Digit(jsonNumber[index]))
            {
                exponent = Math.Min(bound, exponent * 10 + jsonNumber[index++] - '0');
            }
            if (index == digits) return false;
            if (exponentNegative) exponent = -exponent;
        }
        if (index != jsonNumber.Length) return false;
        var first = -1;
        var last = -1;
        var trailingZeros = 0;
        var significantDigits = 0;
        for (var i = start; i < end; i++)
        {
            var digit = jsonNumber[i];
            if (digit == '.') continue;
            if (digit != '0')
            {
                if (first < 0) first = i;
                last = i;
                trailingZeros = 0;
            }
            else if (first >= 0) trailingZeros++;
            if (first >= 0) significantDigits++;
        }
        if (first < 0) return true;
        significantDigits -= trailingZeros;
        var scale = exponent - fractionalDigits + trailingZeros;
        if (scale < 0 || significantDigits + scale > 19) return false;
        ulong magnitude = 0;
        for (var i = first; i <= last; i++)
        {
            if (jsonNumber[i] != '.') magnitude = magnitude * 10 + (uint)(jsonNumber[i] - '0');
        }
        for (var i = 0; i < scale; i++) magnitude *= 10;
        var maximum = negative ? 9223372036854775808UL : long.MaxValue;
        if (magnitude > maximum) return false;
        value = negative ? magnitude == 9223372036854775808UL ? long.MinValue : -(long)magnitude : (long)magnitude;
        return true;
    }

    private static bool Digit(char value) => value is >= '0' and <= '9';
}
