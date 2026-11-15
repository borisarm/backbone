using Backbone.Contracts;

namespace Backbone.App.Ipc;

/// <summary>El proceso al otro lado del pipe no es el servicio Backbone (§7.1 A-1). Se cierra la conexión sin enviar nada.</summary>
public sealed class ServerVerificationException : Exception
{
    public ServerVerificationException()
    {
    }

    public ServerVerificationException(string message)
        : base(message)
    {
    }

    public ServerVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>El servicio implementa una versión mayor del contrato distinta de la de la app (§7.1).</summary>
public sealed class ContractIncompatibleException : Exception
{
    public ContractIncompatibleException()
        : this(string.Empty)
    {
    }

    public ContractIncompatibleException(string serviceContractVersion)
        : base($"The service implements contract '{serviceContractVersion}'; this app requires major version {ContractVersion.Major}.")
    {
        ServiceContractVersion = serviceContractVersion;
    }

    public ContractIncompatibleException(string message, Exception innerException)
        : base(message, innerException)
    {
        ServiceContractVersion = string.Empty;
    }

    /// <summary>Versión de contrato que anuncia el servicio.</summary>
    public string ServiceContractVersion { get; }
}

/// <summary>Se intentó una llamada sin conexión con el servicio (o la conexión se perdió durante la llamada).</summary>
public sealed class ServiceUnavailableException : Exception
{
    public ServiceUnavailableException()
        : this(ConnectionState.ServiceStopped)
    {
    }

    public ServiceUnavailableException(ConnectionState state)
        : base($"The service is not available ({state}).")
    {
        State = state;
    }

    public ServiceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        State = ConnectionState.Reconnecting;
    }

    /// <summary>Estado de la conexión cuando falló la llamada.</summary>
    public ConnectionState State { get; }
}
