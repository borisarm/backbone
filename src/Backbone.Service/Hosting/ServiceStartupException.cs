namespace Backbone.Service;

/// <summary>
/// El servicio no puede arrancar de forma segura (pipe ya existente, carpeta de datos insegura). Ya se ha registrado en el
/// Visor de eventos; <c>Program</c> termina con código distinto de cero para que el SCM aplique la recuperación.
/// </summary>
internal sealed class ServiceStartupException : Exception
{
    public ServiceStartupException()
    {
    }

    public ServiceStartupException(string message)
        : base(message)
    {
    }

    public ServiceStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
