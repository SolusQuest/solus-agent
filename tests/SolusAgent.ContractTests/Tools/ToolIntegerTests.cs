using System.Globalization;
using CustomTools;
using SolusAgent.Tools.Api;
using Xunit;

namespace SolusAgent.ContractTests.Tools;

public sealed class ToolIntegerTests
{
    [Theory]
    [InlineData("1.00", 1)] [InlineData("1e0", 1)] [InlineData("1.5e1", 15)]
    [InlineData("-1.50E+2", -150)] [InlineData("1000e-3", 1)]
    [InlineData("0.001e3", 1)] [InlineData("-0.00e999999999999", 0)]
    [InlineData("9223372036854775807.000", long.MaxValue)]
    [InlineData("-9.223372036854775808e18", long.MinValue)]
    public void ExactSpellingsRetainSignedInt64Value(string token, long expected)
    {
        Assert.True(ToolInteger.TryParse(token, out var value)); Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("")] [InlineData(" 1")] [InlineData("1 ")] [InlineData("+1")]
    [InlineData("01")] [InlineData("-01")] [InlineData("1.")] [InlineData(".1")]
    [InlineData("1e")] [InlineData("1e+")] [InlineData("true")] [InlineData("null")]
    [InlineData("\"1\"")] [InlineData("1 2")] [InlineData("NaN")]
    [InlineData("1.00000000000000000001")] [InlineData("1e-1")]
    [InlineData("9223372036854775808")] [InlineData("-9223372036854775809")]
    [InlineData("1e999999999999")] [InlineData("1e-999999999999")]
    [InlineData("\ud800")]
    public void SyntaxEncodingFractionAndOverflowNeverRoundIntoAdmission(string token)
    {
        Assert.False(ToolInteger.TryParse(token, out var value)); Assert.Equal(0, value);
    }

    [Fact]
    public void WorkAndSourceBytesRemainBoundedForLongMantissasAndExponents()
    {
        Assert.True(ToolInteger.TryParse("1" + new string('0', 12000) + "e-12000", out var one)); Assert.Equal(1, one);
        var exact = "0e" + new string('9', ToolLimits.PayloadBytes - 2);
        Assert.True(ToolInteger.TryParse(exact, out var zero)); Assert.Equal(0, zero);
        Assert.False(ToolInteger.TryParse(exact + "9", out _));
        Assert.False(ToolInteger.TryParse("1e" + new string('9', 12000), out _));
        Assert.Throws<ArgumentNullException>(() => ToolInteger.TryParse(null!, out _));
    }

    [Fact]
    public void DecimalAndExponentEquivalenceHoldsWithoutFloatingPoint()
    {
        var numbers = new[] { long.MinValue, long.MaxValue, -100000000000000001, 100000000000000001, 0, -1, 1, 151 };
        foreach (var number in numbers)
        {
            var integer = number.ToString(CultureInfo.InvariantCulture);
            foreach (var token in new[] { integer, integer + ".0000", integer + "e0", number == 0 ? "0e-3" : integer + "000e-3" })
            { Assert.True(ToolInteger.TryParse(token, out var actual)); Assert.Equal(number, actual); }
        }
    }

    [Theory]
    [InlineData("1.00", 1)] [InlineData("1e0", 1)] [InlineData("1.5e1", 15)]
    public async Task RealProducerPreparationInvocationAndHistoryUseTheSameValueAndExactRawCall(string token, long expected)
    {
        var tool = new CounterTool(); var capability = new CounterCapability();
        var call = new ToolCall("exact", "counter", "{ \"amount\": " + token + " }");
        var prepared = tool.Prepare(call); Assert.True(prepared.Accepted); Assert.Equal(0, capability.Effects);
        var equivalent = new ToolCall(call.CallId, call.ToolName, "{\"amount\":" + expected + "}");
        Assert.Equal(ToolError.CallMismatch, tool.ValidateInvocation(prepared.Prepared!, equivalent, capability));
        var actual = await tool.InvokeAsync(prepared.Prepared!, call, capability);
        Assert.Equal(ToolOutcome.Succeeded, actual.Outcome); Assert.Equal(expected, capability.Total); Assert.Equal(1, capability.Effects);
        var historical = ToolResult.RestoreHistorical(tool.Descriptor, call, actual.Call, actual.Outcome, actual.Error, actual.InvocationStarted, actual.Json);
        Assert.True(historical.IsHistorical); Assert.Equal(call.ArgumentsJson, historical.Call.ArgumentsJson); Assert.Equal(1, capability.Effects);
    }
}
