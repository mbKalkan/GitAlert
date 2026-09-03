using GitAlert.Core;
using Xunit;

namespace GitAlert.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "now")]
    [InlineData(30, "now")]
    [InlineData(60, "1m")]
    [InlineData(59 * 60, "59m")]
    [InlineData(60 * 60, "1h")]
    [InlineData(23 * 3600, "23h")]
    [InlineData(24 * 3600, "1d")]
    [InlineData(6 * 24 * 3600, "6d")]
    [InlineData(7 * 24 * 3600, "1w")]
    [InlineData(365 * 24 * 3600, "1y")]
    public void Formats_ages_compactly(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Format(Now.AddSeconds(-secondsAgo), Now));
    }

    [Fact]
    public void A_clock_skew_into_the_future_reads_as_now()
    {
        Assert.Equal("now", RelativeTime.Format(Now.AddMinutes(5), Now));
    }
}
