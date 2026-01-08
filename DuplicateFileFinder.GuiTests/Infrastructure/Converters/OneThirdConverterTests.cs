using System;
using System.Globalization;

using DuplicateFileFinder.Gui.Infrastructure.Converters;

using Xunit;

namespace DuplicateFileFinder.GuiTests.Infrastructure.Converters;

public sealed class OneThirdConverterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(3.0, 1.0)]
    [InlineData(300.0, 100.0)]
    public void Convert_PositiveDouble_DividesByThree(double input, double expected)
    {
        var c = new OneThirdConverter();
        var got = c.Convert(input, typeof(double), null, Invariant);
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Convert_NonPositive_ReturnsNaN(double input)
    {
        var c = new OneThirdConverter();
        var got = (double)c.Convert(input, typeof(double), null, Invariant);
        Assert.True(double.IsNaN(got));
    }

    [Fact]
    public void Convert_NonDouble_ReturnsNaN()
    {
        var c = new OneThirdConverter();
        var got = (double)c.Convert("123", typeof(double), null, Invariant);
        Assert.True(double.IsNaN(got));
    }

    [Fact]
    public void ConvertBack_NotSupported()
    {
        var c = new OneThirdConverter();
        Assert.Throws<NotSupportedException>(() => c.ConvertBack(1.0, typeof(double), null, Invariant));
    }
}
