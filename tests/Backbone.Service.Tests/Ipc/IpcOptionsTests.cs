using Backbone.Service.Ipc;

namespace Backbone.Service.Tests.Ipc;

/// <summary>§7.1 (B-2): plazos de identificación y de cierre ordenado tras un rechazo.</summary>
[Trait("Story", "HT-03")]
public sealed class IpcOptionsTests
{
    [Fact]
    public void Defaults_are_five_seconds_to_identify_and_two_seconds_of_linger()
    {
        var options = new IpcOptions();

        Assert.Equal(TimeSpan.FromSeconds(5), options.IdentificationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(2), options.RejectionLinger);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4999)]
    public void A_linger_from_zero_to_less_than_the_identification_timeout_is_accepted(int milliseconds)
    {
        var options = new IpcOptions { RejectionLinger = TimeSpan.FromMilliseconds(milliseconds) };

        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), options.RejectionLinger);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5000)]
    [InlineData(60000)]
    public void A_negative_linger_or_one_not_shorter_than_the_identification_timeout_is_rejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpcOptions { RejectionLinger = TimeSpan.FromMilliseconds(milliseconds) });
    }

    [Fact]
    public void An_infinite_linger_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpcOptions { RejectionLinger = Timeout.InfiniteTimeSpan });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2000)]
    public void An_identification_timeout_that_is_not_longer_than_the_linger_is_rejected(int milliseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpcOptions { IdentificationTimeout = TimeSpan.FromMilliseconds(milliseconds) });
    }

    [Fact]
    public void Both_can_be_shortened_setting_the_linger_first()
    {
        var options = new IpcOptions { RejectionLinger = TimeSpan.FromMilliseconds(100), IdentificationTimeout = TimeSpan.FromMilliseconds(200) };

        Assert.Equal(TimeSpan.FromMilliseconds(200), options.IdentificationTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new IpcOptions
        {
            RejectionLinger = TimeSpan.FromMilliseconds(100),
            IdentificationTimeout = TimeSpan.FromMilliseconds(100),
        });
    }
}
