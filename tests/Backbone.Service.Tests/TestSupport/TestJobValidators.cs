using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Construcción de <see cref="JobValidator"/> para las pruebas, con dobles de lo que no es la regla probada.</summary>
internal static class TestJobValidators
{
    /// <summary>Opciones de plazo de las pruebas que no prueban plazos (las de §8.2, con el reloj real).</summary>
    public static SourceIoOptions DefaultIoOptions { get; } = new();

    public static JobValidator Create(
        ILocalFileSystemInspector inspector,
        string dataRoot,
        IHostResolver? hostResolver = null,
        SourceValidationRunner? runner = null,
        TimeProvider? time = null) =>
        Create(inspector, new ProtectedFolderResolver(new ServicePaths(dataRoot), inspector, NullLogger<ProtectedFolderResolver>.Instance), hostResolver, runner, time);

    public static JobValidator Create(
        ILocalFileSystemInspector inspector,
        ProtectedFolderResolver resolver,
        IHostResolver? hostResolver = null,
        SourceValidationRunner? runner = null,
        TimeProvider? time = null) =>
        new(
            inspector,
            resolver,
            runner ?? new SourceValidationRunner(time ?? TimeProvider.System, DefaultIoOptions),
            hostResolver ?? new FakeHostResolver(),
            time ?? TimeProvider.System,
            NullLogger<JobValidator>.Instance);

    /// <summary>Resultado de la validación (sin el trabajo normalizado).</summary>
    public static async Task<ValidationResult> CheckAsync(this JobValidator validator, JobDefinition job, IReadOnlyList<JobDefinition> saved) =>
        (await validator.ValidateAsync(job, saved, TestContext.Current.CancellationToken)).Issues;
}
