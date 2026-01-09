using System.Globalization;

using Avalonia;

using DuplicateFileFinder.Gui.Infrastructure.Converters;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Infrastructure.Converters;

public sealed class BytesToHumanConverterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(null, null)]
    public void Convert_Null_PassesThrough(object? input, object? expected)
    {
        var c = BytesToHumanConverter.Instance;
        Assert.Equal(expected, c.Convert(input, typeof(string), null, Invariant));
    }

    [Theory]
    [InlineData(0L, "0.0 B")]
    [InlineData(1L, "1.0 B")]
    [InlineData(1024L, "1.0 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(1048576L, "1.0 MB")]
    public void Convert_Long_Formats(long bytes, string expected)
    {
        var c = BytesToHumanConverter.Instance;
        var got = c.Convert(bytes, typeof(string), null, Invariant);
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData(-1L, "-1.0 B")]
    [InlineData(-1024L, "-1.0 KB")]
    [InlineData(-1536L, "-1.5 KB")]
    public void Convert_Negative_UsesMinusPrefix(long bytes, string expected)
    {
        var c = BytesToHumanConverter.Instance;
        var got = c.Convert(bytes, typeof(string), null, Invariant);
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("123", "123.0 B")] // string input is TryToLong -> long
    [InlineData(123, "123.0 B")]
    [InlineData(123.9d, "123.0 B")] // double truncates to long via (long)d
    public void Convert_NumericLikeInputs_AreCoerced(object input, string expected)
    {
        var c = BytesToHumanConverter.Instance;
        var got = c.Convert(input, typeof(string), null, Invariant);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void Convert_UnknownType_PassesThrough()
    {
        var c = BytesToHumanConverter.Instance;
        var obj = new object();
        Assert.Same(obj, c.Convert(obj, typeof(string), null, Invariant));
    }

    [Theory]
    [InlineData("", 0L)]
    [InlineData("   ", 0L)]
    [InlineData("1", 1L)]
    [InlineData("1 B", 1L)]
    [InlineData("1 bytes", 1L)]
    [InlineData("1 KB", 1024L)]
    [InlineData("1 KiB", 1024L)]
    [InlineData("1.5 KB", 1536L)]
    [InlineData("2 MB", 2L * 1024 * 1024)]
    [InlineData("3 GB", 3L * 1024 * 1024 * 1024)]
    public void ConvertBack_ParsesCommonUnits(string s, long expected)
    {
        var c = BytesToHumanConverter.Instance;
        var got = c.ConvertBack(s, typeof(long), null, Invariant);
        Assert.Equal(expected, got);
    }

    [Fact]
    public void ConvertBack_UnknownUnit_TreatedAsBytes()
    {
        var c = BytesToHumanConverter.Instance;
        // unit "FOO" -> default power 0
        var got = c.ConvertBack("10 FOO", typeof(long), null, Invariant);
        Assert.Equal(10L, got);
    }

    [Fact]
    public void ConvertBack_InvalidNumber_ReturnsUnsetValue()
    {
        var c = BytesToHumanConverter.Instance;
        Assert.Equal(AvaloniaProperty.UnsetValue, c.ConvertBack("abc KB", typeof(long), null, Invariant));
    }

    [Fact]
    public void ConvertBack_Null_ReturnsUnsetValue()
    {
        var c = BytesToHumanConverter.Instance;
        Assert.Equal(AvaloniaProperty.UnsetValue, c.ConvertBack(null, typeof(long), null, Invariant));
    }

    [Fact]
    public void ConvertBack_TargetType_Int_ClampsToIntMax_WhenValueFitsInLong()
    {
        var sut = new BytesToHumanConverter();

        // int.MaxValue + 1: fits in Int64, should clamp for Int32 target
        var result = sut.ConvertBack("2147483648", typeof(int), null, CultureInfo.InvariantCulture);

        Assert.IsType<int>(result);
        Assert.Equal(int.MaxValue, (int)result);
    }

    [Fact]
    public void ConvertBack_TargetType_Int_ClampsToIntMin_WhenValueFitsInLong()
    {
        var sut = new BytesToHumanConverter();

        // int.MinValue - 1: fits in Int64, should clamp for Int32 target
        var result = sut.ConvertBack("-2147483649", typeof(int), null, CultureInfo.InvariantCulture);

        Assert.IsType<int>(result);
        Assert.Equal(int.MinValue, (int)result);
    }

    [Fact]
    public void ConvertBack_ReturnsUnset_WhenParsedDoubleExceedsLongMax()
    {
        var sut = new BytesToHumanConverter();

        // definitely > long.MaxValue as a double
        var result = sut.ConvertBack("1e40", typeof(long), null, CultureInfo.InvariantCulture);

        Assert.Same(AvaloniaProperty.UnsetValue, result);
    }
}
