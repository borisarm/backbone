using Backbone.Service.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;

namespace Backbone.Service.Tests.Hosting;

/// <summary>
/// HT-03 I-5: el host fija <c>ContentRootPath = AppContext.BaseDirectory</c> y <c>Production</c>, y no carga configuración
/// (appsettings, variables de entorno, línea de comandos, secretos de usuario) de ubicaciones que un usuario pueda escribir.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class HostConfigurationTests
{
    private static HostApplicationBuilder NewBuilder() => BackboneHost.CreateBuilder(new BackboneHostOptions { DataRoot = Path.Combine(Path.GetTempPath(), "bb-unused") });

    [Fact]
    public void The_content_root_is_the_installation_directory_and_the_environment_is_Production()
    {
        var builder = NewBuilder();

        Assert.Equal(
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(builder.Environment.ContentRootPath).TrimEnd(Path.DirectorySeparatorChar));
        Assert.Equal(Environments.Production, builder.Environment.EnvironmentName);
    }

    [Fact]
    public void No_default_configuration_source_is_loaded()
    {
        var providers = ((IConfigurationRoot)NewBuilder().Configuration).Providers.ToList();

        Assert.DoesNotContain(providers, p => p is JsonConfigurationProvider);
        Assert.DoesNotContain(providers, p => p is EnvironmentVariablesConfigurationProvider);
        Assert.DoesNotContain(providers, p => p is CommandLineConfigurationProvider);
        Assert.DoesNotContain(providers, p => p.GetType().Name.Contains("UserSecrets", StringComparison.Ordinal));
    }

    [Fact]
    public void Environment_variables_cannot_change_the_environment_or_inject_configuration()
    {
        const string key = "BACKBONE_QA_INJECTED";
        var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        try
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable(key, "1");

            var builder = NewBuilder();

            Assert.Equal(Environments.Production, builder.Environment.EnvironmentName);
            Assert.Null(builder.Configuration[key]);
            Assert.Null(builder.Configuration["Logging:LogLevel:Default"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous);
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Fact]
    public void An_appsettings_file_in_the_working_directory_is_ignored()
    {
        var cwd = Directory.GetCurrentDirectory();
        var file = Path.Combine(cwd, "appsettings.json");
        var existed = File.Exists(file);
        Assert.False(existed, "La prueba no sobrescribe un appsettings.json existente.");
        try
        {
            File.WriteAllText(file, """{ "QaInjected": "1" }""");

            Assert.Null(NewBuilder().Configuration["QaInjected"]);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
