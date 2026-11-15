namespace Backbone.Contracts;

/// <summary>
/// Constantes del transporte IPC (§7.1).
/// </summary>
public static class BackboneIpc
{
    /// <summary>
    /// Nombre del named pipe (sin el prefijo <c>\\.\pipe\</c>). El sufijo es la versión mayor del contrato
    /// (<see cref="ContractVersion.Major"/>): un cambio incompatible usa un pipe distinto.
    /// </summary>
    public const string PipeName = "Backbone.Service.v1";

    /// <summary>Ruta completa del pipe, <c>\\.\pipe\Backbone.Service.v1</c>.</summary>
    public const string PipePath = @"\\.\pipe\" + PipeName;

    /// <summary>Número máximo de conexiones simultáneas que admite el servidor.</summary>
    public const int MaxConnections = 8;

    /// <summary>
    /// Tamaño máximo de un mensaje JSON-RPC en bytes (4 MiB). Un extremo corta la conexión que lo supera sin deserializarlo;
    /// por eso las listas grandes se paginan (§7.5).
    /// </summary>
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    /// <summary>Frecuencia máxima del evento <see cref="IBackboneService.RunProgress"/> (mensajes por segundo y ejecución).</summary>
    public const int MaxRunProgressEventsPerSecond = 4;
}
