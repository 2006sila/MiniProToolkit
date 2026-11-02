using System.Globalization;
using MiniProToolkit.Core;
using Xunit;

namespace MiniProToolkit.Core.Tests;

/// <summary>
/// Unit formatting shared by the taskbar overlay and the main window. Missing values must
/// render as an explicit placeholder so "unknown" is never confused with 0 MHz.
/// </summary>
public sealed class MetricDisplayFormatTests
{
    [Theory]
    [InlineData(null, "-- MHz")]
    [InlineData(0d, "-- MHz")]
    [InlineData(4200d, "4200 MHz")]
    [InlineData(799.6d, "800 MHz")]
    public void MegahertzSpellsOutTheUnit(double? mhz, string expected) =>
        Assert.Equal(expected, MetricDisplayFormat.Megahertz(mhz));

    [Fact]
    public void FrequencyShowsTheNominalClockSoTurboIsNotMistakenForBase()
    {
        Assert.Equal("4200 MHz（基准 2800 MHz，150%）",
            MetricDisplayFormat.Frequency(4200, 2800, 150));
        // No usable base clock: the clock is still shown, without a fabricated percentage.
        Assert.Equal("4200 MHz", MetricDisplayFormat.Frequency(4200, null, null));
        Assert.Equal("-- MHz", MetricDisplayFormat.Frequency(null, 2800, 120));
    }

    [Fact]
    public void PercentOfBaseIsRoundedAndGuarded() =>
        Assert.Equal("150%", MetricDisplayFormat.PercentOfBase(150.4));

    [Fact]
    public void FrequencyFormattingIsCultureInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("4200 MHz（基准 2800 MHz，150%）", MetricDisplayFormat.Frequency(4200, 2800, 150));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
