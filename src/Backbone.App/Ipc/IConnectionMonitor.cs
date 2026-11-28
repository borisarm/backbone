using Backbone.Contracts;

namespace Backbone.App.Ipc;

/// <summary>Estado observable de la conexión con el servicio (§9.1). Lo implementa <see cref="BackboneClient"/>; los view models solo ven esta interfaz.</summary>
internal interface IConnectionMonitor
{
    /// <summary>Estado actual.</summary>
    ConnectionState State { get; }

    /// <summary>Información de la conexión actual; <see langword="null"/> si no hay conexión.</summary>
    ServiceInfo? ServiceInfo { get; }

    /// <summary>Versión de contrato del servicio cuando <see cref="State"/> es <see cref="ConnectionState.IncompatibleContract"/>.</summary>
    string? IncompatibleContractVersion { get; }

    /// <summary>Se produce en cada cambio de <see cref="State"/>, en el hilo de UI.</summary>
    event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Interrumpe la espera hasta el siguiente intento y reintenta ya.</summary>
    void RetryNow();
}

/// <summary>Arranca el servicio de Windows (§9.3, "Iniciar servicio").</summary>
internal interface IServiceStarter
{
    /// <summary>Pide arrancar el servicio. Devuelve <see langword="false"/> si el usuario rechazó la elevación o el arranque no se pudo lanzar.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns><see langword="true"/> si <c>sc start</c> terminó.</returns>
    Task<bool> StartAsync(CancellationToken ct = default);
}
