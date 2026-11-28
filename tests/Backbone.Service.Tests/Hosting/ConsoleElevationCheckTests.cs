using Backbone.Service.Hosting;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Hosting;

/// <summary>El modo <c>--console</c> sin elevar se rechaza antes de construir el host (RNF-07); el modo servicio no cambia.</summary>
[Trait("Story", "HT-03")]
public sealed class ConsoleElevationCheckTests
{
    [Fact]
    public void Console_mode_without_elevation_writes_the_message_and_does_not_start()
    {
        var error = new StringWriter();

        var canStart = ConsoleElevationCheck.CanStart(consoleMode: true, () => false, error);

        Assert.False(canStart);
        Assert.Equal(ConsoleElevationCheck.NotElevatedMessage + Environment.NewLine, error.ToString());
        Assert.Contains("consola elevada", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, ConsoleElevationCheck.ExitCode);
    }

    [Fact]
    public void Console_mode_with_elevation_starts_silently()
    {
        var error = new StringWriter();

        Assert.True(ConsoleElevationCheck.CanStart(consoleMode: true, () => true, error));
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Service_mode_never_checks_the_elevation(bool elevated)
    {
        var error = new StringWriter();
        var asked = false;

        var canStart = ConsoleElevationCheck.CanStart(consoleMode: false, () => { asked = true; return elevated; }, error);

        Assert.True(canStart);
        Assert.False(asked);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public void The_real_probe_matches_the_administrators_membership_of_the_process_token()
    {
        Assert.Equal(WindowsTestEnvironment.IsElevated, ConsoleElevationCheck.IsProcessElevated());
    }
}
