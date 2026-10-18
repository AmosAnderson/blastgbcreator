using System.Buffers;
using System.Text;
using System.Text.Json;
using BlastGb.Serialization;

namespace BlastGb.Cli.Tests;

public sealed class JsonIntegerTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("1.0", 1)]
    [InlineData("1e0", 1)]
    [InlineData("1E+2", 100)]
    [InlineData("0.000001e6", 1)]
    [InlineData("-12.3000e1", -123)]
    [InlineData("2147483647.0", int.MaxValue)]
    [InlineData("-2147483648e0", int.MinValue)]
    [InlineData("214748364700000000000000000000e-20", int.MaxValue)]
    [InlineData("0e999999999999999999999999999999", 0)]
    [InlineData("-0.000e-999999999999999999999999999999", 0)]
    public void Int32AcceptsExactlyIntegralFormsThroughEveryReader(string json, int expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(JsonInteger.TryGetInt32(document.RootElement, out var elementValue));
        Assert.Equal(expected, elementValue);
        Assert.True(JsonInteger.TryGetInt64(document.RootElement, out var wideValue));
        Assert.Equal(expected, wideValue);
        CheckReaders(json, true, expected);
    }

    [Theory]
    [InlineData("1.00000000000000000000000000000000001")]
    [InlineData("0.000001e5")]
    [InlineData("1e-999999999999999999999999999999")]
    [InlineData("1e999999999999999999999999999999")]
    [InlineData("2147483648")]
    [InlineData("-2147483649.0")]
    [InlineData("2147483647.000000000000000000001")]
    [InlineData("-2147483648.000000000000000000001")]
    [InlineData("100000000000000000000000000000001e-32")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void Int32RejectsFractionsOverflowAndNonNumbers(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(JsonInteger.TryGetInt32(document.RootElement, out _));
        CheckReaders(json, false);
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775807.0", long.MaxValue)]
    [InlineData("9223372036854775807000e-3", long.MaxValue)]
    [InlineData("-9223372036854775808e0", long.MinValue)]
    [InlineData("-9223372036854775808000e-3", long.MinValue)]
    [InlineData("9e18", 9000000000000000000L)]
    [InlineData("-9e18", -9000000000000000000L)]
    public void Int64PreservesExactBoundaryValues(string json, long expected)
    {
        using var document = JsonDocument.Parse(json);
        Assert.True(JsonInteger.TryGetInt64(document.RootElement, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809.0")]
    [InlineData("9223372036854775808e0")]
    [InlineData("18446744073709551615")]
    [InlineData("9007199254740991.1")]
    [InlineData("9223372036854775807.000000000000000000001")]
    [InlineData("1e-999999999999999999999999999999")]
    [InlineData("1e999999999999999999999999999999")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    [InlineData("false")]
    public void Int64RejectsFractionsOverflowAndNonNumbers(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.False(JsonInteger.TryGetInt64(document.RootElement, out _));
    }

    [Fact]
    public void LongCoefficientsCanCancelExactlyWithoutRoundingSmallFractions()
    {
        var integral = "123" + new string('0', 10000) + "e-10000";
        using var document = JsonDocument.Parse(integral);
        Assert.True(JsonInteger.TryGetInt64(document.RootElement, out var value));
        Assert.Equal(123, value);
        CheckReaders(integral, true, 123);

        var fractional = "123." + new string('0', 10000) + "1";
        using var fractionDocument = JsonDocument.Parse(fractional);
        Assert.False(JsonInteger.TryGetInt64(fractionDocument.RootElement, out _));
        CheckReaders(fractional, false);
    }

    private static void CheckReaders(string json, bool accepted, int expected = 0)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        Assert.True(reader.Read());
        Assert.Equal(accepted, JsonInteger.TryGetInt32(ref reader, out var value));
        if (accepted) Assert.Equal(expected, value);

        if (bytes.Length < 2) return;
        var split = bytes.Length / 2;
        var first = new Segment(bytes.AsMemory(0, split));
        var last = first.Append(bytes.AsMemory(split));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var segmentedReader = new Utf8JsonReader(sequence);
        Assert.True(segmentedReader.Read());
        Assert.Equal(accepted, JsonInteger.TryGetInt32(ref segmentedReader, out var segmentedValue));
        if (accepted) Assert.Equal(expected, segmentedValue);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
