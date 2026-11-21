using Backbone.Service.Os;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>Guardianes de plazo (<see cref="SourceIoGuard"/>) para llamar al inspector directamente desde una prueba.</summary>
internal static class TestIo
{
    /// <summary>Guardián con el plazo de §8.2 y el reloj real, sin cancelación de E/S del hilo.</summary>
    public static SourceIoGuard Guard() => GuardWith(TestContext.Current.CancellationToken);

    /// <summary>Guardián con otro reloj, otros plazos o la cancelación indicada.</summary>
    public static SourceIoGuard GuardWith(CancellationToken ct, TimeProvider? time = null, SourceIoOptions? options = null) =>
        new(time ?? TimeProvider.System, options ?? new SourceIoOptions(), () => { }, new RecordingCanceller(), ct);
}

/// <summary><see cref="ISynchronousIoCanceller"/> que solo cuenta las cancelaciones.</summary>
internal sealed class RecordingCanceller : ISynchronousIoCanceller
{
    private int _cancellations;

    public int Cancellations => Volatile.Read(ref _cancellations);

    public void Cancel() => Interlocked.Increment(ref _cancellations);

    public void Dispose()
    {
    }
}
