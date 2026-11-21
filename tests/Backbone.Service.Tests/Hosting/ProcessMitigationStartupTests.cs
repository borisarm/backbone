using System.ComponentModel;
using Backbone.Service.Hosting;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backbone.Service.Tests.Hosting;

/// <summary>
/// Redirection Guard al arrancar (§8.3, ADR-20): se activa antes de leer ningún origen y, si falla, se registra un aviso y el
/// servicio sigue. La mitigación real no se aplica al proceso de pruebas (rompería los <i>junctions</i> de las pruebas); se
/// comprueba en PM-HU07-05.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class ProcessMitigationStartupTests
{
    private sealed class FakeMitigations(Exception? failure = null) : IProcessMitigations
    {
        public int Calls { get; private set; }

        public void EnableRedirectionGuard()
        {
            Calls++;
            if (failure is not null)
            {
                throw failure;
            }
        }
    }

    [Fact]
    public async Task Redirection_Guard_is_enabled_when_starting()
    {
        var mitigations = new FakeMitigations();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));

        await new ProcessMitigationStartup(mitigations, factory.CreateLogger<ProcessMitigationStartup>()).StartingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, mitigations.Calls);
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Redirection Guard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failure_is_a_warning_and_the_service_keeps_starting()
    {
        var mitigations = new FakeMitigations(new Win32Exception(87));
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));

        await new ProcessMitigationStartup(mitigations, factory.CreateLogger<ProcessMitigationStartup>()).StartingAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Redirection Guard", StringComparison.Ordinal));
    }

    [Fact]
    public void It_is_registered_before_the_startup_service()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBackboneService(new BackboneHostOptions { DataRoot = Path.Combine(Path.GetTempPath(), "backbone-no-usado") });

        var hosted = services.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType).ToList();

        Assert.True(hosted.IndexOf(typeof(ProcessMitigationStartup)) >= 0);
        Assert.True(hosted.IndexOf(typeof(ProcessMitigationStartup)) < hosted.IndexOf(typeof(StartupService)));
        Assert.Contains(services, d => d.ServiceType == typeof(IProcessMitigations) && d.ImplementationType == typeof(WindowsProcessMitigations));
    }
}
