using System.Collections.Concurrent;
using System.Threading.Channels;
using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>
/// El ciclo de una ejecución (HU-14) con dobles de todo lo que toca el sistema: conexión SMB, repositorio en memoria, carpetas
/// locales, lectura de los orígenes, reloj y contador de hilos abandonados. <see cref="RunQueue"/> y <see cref="JobRunner"/>
/// son los reales.
/// </summary>
internal sealed class RunRig : IAsyncDisposable
{
    public const string Share = @"\\nas\backups";
    public const string Destination = @"\\nas\backups\pc";
    public const string DataFolder = @"C:\Datos";
    public const string User = "ana";
    public const string Password = "clave-HU14-secreta";

    private readonly TempDirectory _temp = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly Channel<RunCompletedEvent> _completed = Channel.CreateUnbounded<RunCompletedEvent>();

    public RunRig(
        JobRunnerOptions? runnerOptions = null,
        SourceIoOptions? ioOptions = null,
        Func<ISynchronousIoCanceller>? canceller = null,
        IEnumerable<IRunCompletionHandler>? handlers = null,
        Func<ICredentialStore, ICredentialStore>? wrapCredentials = null)
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
        Credentials = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), Time, _loggerFactory.CreateLogger<CredentialStore>());
        Directory.CreateDirectory(_temp.Combine("config"));
        Jobs = new JobStore(_temp.Combine("config", "jobs.json"), TestFileTrust.CurrentUser, _loggerFactory.CreateLogger<JobStore>());
        var programData = Path.GetDirectoryName(_temp.Path) ?? _temp.Path;
        Inspector = new FakeLocalFileSystemInspector().MapFinalDirectory(programData, programData).AddFolder(DataFolder);
        Abandoned = new AbandonedSourceThreads(EventLog, _loggerFactory.CreateLogger<AbandonedSourceThreads>());
        Validation = canceller is null
            ? new SourceValidationRunner(Time, ioOptions ?? new SourceIoOptions(), abandoned: Abandoned)
            : new SourceValidationRunner(Time, ioOptions ?? new SourceIoOptions(), canceller, abandoned: Abandoned);
        Sources.Source.PathTranslator = Snapshotter.ToLive;
        Connections = new ShareConnectionManager(Connector, _loggerFactory.CreateLogger<ShareConnectionManager>());
        Runner = new JobRunner(
            new ShareCredentialProvider(wrapCredentials?.Invoke(Credentials) ?? Credentials, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
            Connections,
            Storages,
            Inspector,
            new ProtectedFolderResolver(new ServicePaths(_temp.Path), Inspector, _loggerFactory.CreateLogger<ProtectedFolderResolver>()),
            Validation,
            Sources,
            Snapshotter,
            EventLog,
            States,
            new ServiceRuntimeInfo(Time),
            Time,
            runnerOptions ?? new JobRunnerOptions(),
            _loggerFactory.CreateLogger<JobRunner>());
        Runs = new RunQueue(Jobs, Runner, Time, handlers ?? [], _loggerFactory.CreateLogger<RunQueue>());
        Runs.Completed += (_, e) =>
        {
            CompletedEvents.Enqueue(e);
            _completed.Writer.TryWrite(e);
        };
        Runs.Progress += (_, e) => ProgressEvents.Enqueue(e);
        Runs.Changed += (_, _) => Interlocked.Increment(ref _changes);
        Runs.Start();
    }

    private int _changes;

    /// <summary>Carpeta de datos del servicio de la prueba (equivale a <c>%ProgramData%\Backbone</c>).</summary>
    public string DataRoot => _temp.Path;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero));

    public FakeShareConnector Connector { get; } = new();

    public FakeRepositoryStorageFactory Storages { get; } = new();

    public FakeLocalFileSystemInspector Inspector { get; }

    public FakeEventLogWriter EventLog { get; } = new();

    public AbandonedSourceThreads Abandoned { get; }

    public SourceValidationRunner Validation { get; }

    public FakeSourceFileSystemFactory Sources { get; } = new();

    /// <summary>VSS simulado (HU-15): por defecto crea las instantáneas; su lectura se sirve de los mismos archivos que la copia en vivo.</summary>
    public FakeVolumeSnapshotter Snapshotter { get; } = new();

    public RepositoryStateTracker States { get; } = new();

    public CapturingLoggerProvider Logs { get; } = new();

    public CredentialStore Credentials { get; }

    public JobStore Jobs { get; }

    public ShareConnectionManager Connections { get; }

    public JobRunner Runner { get; }

    public RunQueue Runs { get; }

    public ConcurrentQueue<RunCompletedEvent> CompletedEvents { get; } = new();

    public ConcurrentQueue<RunProgressEvent> ProgressEvents { get; } = new();

    /// <summary>Veces que la cola ha avisado de un cambio (<c>StatusChanged</c>).</summary>
    public int Changes => Volatile.Read(ref _changes);

    /// <summary>Repositorio en memoria del destino (vacío si aún no se ha usado).</summary>
    public Backbone.Core.Storage.InMemoryRepositoryStorage Memory(string destination = Destination) => Storages.Memory(destination);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Guarda la credencial del recurso (la de <see cref="User"/>).</summary>
    public Task SaveCredentialAsync() =>
        Credentials.SaveAsync(Share, new CredentialInput { UserName = User, Password = Password }, Ct);

    /// <summary>Guarda un trabajo con un origen (<c>Datos</c>, <see cref="DataFolder"/>) y el destino de las pruebas.</summary>
    public async Task<JobDefinition> AddJobAsync(string name = "Documentos", string? destination = null, IReadOnlyList<(string Alias, string Path)>? sources = null)
    {
        var job = JobDefaults.NewJob() with
        {
            Id = Guid.NewGuid(),
            Name = name,
            Sources = [.. (sources ?? [("Datos", DataFolder)]).Select(s => new JobSource { Alias = s.Alias, Path = s.Path })],
            Destination = destination ?? Destination,
        };
        await Jobs.UpdateAsync(list => (list.Add(job), true), Ct);
        return job;
    }

    /// <summary>
    /// Hace que la lectura de los orígenes se detenga al abrir el primer archivo (y de todas las ejecuciones siguientes hasta
    /// que se libere), atendiendo la cancelación. <c>Entered</c> se completa cuando una ejecución llega ahí.
    /// </summary>
    public (Task Entered, TaskCompletionSource Release) HoldAtFirstFile()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Sources.Source.OnOpenRead = async (_, ct) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct).ConfigureAwait(false);
        };
        return (entered.Task, release);
    }

    /// <summary>Espera a que se cumpla la condición (hasta 10 s).</summary>
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.True(condition(), "La condición no se cumplió a tiempo.");
    }

    /// <summary>Espera la siguiente ejecución terminada.</summary>
    public async Task<RunRecord> NextCompletedAsync(int seconds = 20)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return (await _completed.Reader.ReadAsync(timeout.Token)).Run;
    }

    public async ValueTask DisposeAsync()
    {
        await Runs.StopAsync(CancellationToken.None);
        Runs.Dispose();
        Validation.Dispose();
        Inspector.Dispose();
        Jobs.Dispose();
        Credentials.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }
}
