using System.Collections.Concurrent;
using Backbone.Contracts;
using Backbone.Core.Storage;
using Backbone.Service.Ipc;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Outcome = Backbone.Service.Jobs.RepositoryPreparationOutcome;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// Segunda revisión de seguridad de HU-06, BAJA-N1: la parada cancela las preparaciones pendientes (también las abandonadas)
/// antes de cerrar a la fuerza las conexiones, así que ninguna toca el almacenamiento con el registro de uso ya borrado
/// (la E/S saldría con la cuenta del equipo, §7.5 A-3).
/// </summary>
[Trait("Story", "HU-06")]
public sealed class RepositoryPreparationShutdownTests : IAsyncLifetime
{
    private const string Share = @"\\nas1\b";
    private const string Destination = @"\\nas1\b\x";

    private readonly IpcTestHarness _server = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        await _server.Credentials.SaveAsync(Share, new CredentialInput { UserName = "ana", Password = "Contraseña-HU06-parada" }, Ct);

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private ShareConnectionShutdown NewShutdown() => new(
        _server.ConnectionTester,
        _server.Repositories,
        _server.Connections,
        _server.Time,
        _server.LoggerFactory.CreateLogger<ShareConnectionShutdown>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_cancels_a_preparation_stuck_in_storage_io_before_forcing_the_connections(bool abandoned)
    {
        var storage = new SlowStorage(new InMemoryRepositoryStorage(createRoot: false), _server.ShareConnector);
        _server.RepositoryStorages.Set(Destination, storage);
        var call = _server.Repositories.PrepareAsync(Destination, Ct);
        await storage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        if (abandoned)
        {
            await Task.Delay(100, Ct); // el plazo ya está armado
            await Task.Run(() => _server.Time.Advance(_server.PreparationOptions.Timeout + TimeSpan.FromSeconds(1)), Ct);
            var timeout = await Assert.ThrowsAsync<BackboneException>(() => call);
            Assert.Equal(BackboneErrorCode.NasUnreachable, timeout.Code);

            // Al vencer el plazo, PrepareAsync cancela el token de la preparación abandonada (finally), de forma asíncrona respecto
            // a la excepción: se espera a que la E/S lo note antes de parar, en vez de suponer que aún no lo ha hecho. La parada
            // tiene que terminar igualmente, sin operaciones nuevas.
            await storage.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        // Sin avanzar el reloj: si la parada solo esperase, agotaría los 5 s del plazo (que aquí no pasan) y no terminaría.
        await NewShutdown().StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        await storage.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        if (!abandoned)
        {
            // Aquí la única cancelación posible es la de CancelAll (el cliente sigue esperando y el plazo no ha vencido).
            var failure = await Assert.ThrowsAsync<BackboneException>(() => call);
            Assert.Equal(BackboneErrorCode.Interrupted, failure.Code);
        }

        Assert.Equal(1, storage.Calls); // nada más tras cancelarla
        Assert.Empty(storage.CallsAfterForcedDisconnect);
        Assert.Empty(_server.ShareConnector.ForcedDisconnects); // la conexión ya se había soltado limpiamente
        Assert.Equal(["connect:" + Share, "disconnect:" + Share], _server.ShareConnector.Events);
        Assert.DoesNotContain(
            _server.Logs.Entries,
            e => e.Message.Contains("preparaciones de repositorio", StringComparison.Ordinal) && e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CancelAll_with_nothing_pending_does_nothing_and_a_finished_preparation_is_not_touched()
    {
        _server.Repositories.CancelAll();

        Assert.Equal(Outcome.Initialized, await _server.Repositories.PrepareAsync(Destination, Ct));
        _server.Repositories.CancelAll();

        await _server.Repositories.WhenPreparationsCompleteAsync().WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    /// <summary>
    /// Almacenamiento cuya primera operación se queda "en la red" hasta que se cancela su token; anota toda llamada hecha
    /// después de que el conector haya cerrado a la fuerza alguna conexión.
    /// </summary>
    private sealed class SlowStorage(InMemoryRepositoryStorage inner, FakeShareConnector connector) : IRepositoryStorage
    {
        private int _calls;
        private readonly ConcurrentQueue<string> _afterForced = new();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyCollection<string> CallsAfterForcedDisconnect => [.. _afterForced];

        private async Task EnterAsync(string operation, CancellationToken ct)
        {
            if (!connector.ForcedDisconnects.IsEmpty)
            {
                _afterForced.Enqueue(operation);
            }

            if (Interlocked.Increment(ref _calls) != 1)
            {
                return;
            }

            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public async Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync("OpenRead " + path, cancellationToken);
            return await inner.OpenReadAsync(path, cancellationToken);
        }

        public async Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync("CreateNew " + path, cancellationToken);
            return await inner.CreateNewAsync(path, cancellationToken);
        }

        public async Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            await EnterAsync("Move " + sourcePath, cancellationToken);
            return await inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);
        }

        public async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync("Delete " + path, cancellationToken);
            await inner.DeleteFileAsync(path, cancellationToken);
        }

        public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync("CreateDirectory " + path, cancellationToken);
            await inner.CreateDirectoryAsync(path, cancellationToken);
        }

        public async Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync("DirectoryExists " + path, cancellationToken);
            return await inner.DirectoryExistsAsync(path, cancellationToken);
        }

        public async Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken)
        {
            await EnterAsync("GetFileInfo " + path, cancellationToken);
            return await inner.GetFileInfoAsync(path, cancellationToken);
        }

        public async IAsyncEnumerable<StorageEntry> EnumerateAsync(
            string directoryPath,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await EnterAsync("Enumerate " + directoryPath, cancellationToken);
            await foreach (var entry in inner.EnumerateAsync(directoryPath, cancellationToken).WithCancellation(cancellationToken))
            {
                yield return entry;
            }
        }
    }
}
