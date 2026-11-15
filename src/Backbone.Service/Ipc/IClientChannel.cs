using System.IO.Pipes;
using Backbone.Service.Os;

namespace Backbone.Service.Ipc;

/// <summary>
/// Transporte de una conexión de cliente: el flujo dúplex y la identidad del cliente. Implementación real sobre
/// <see cref="NamedPipeServerStream"/> (<see cref="PipeClientChannel"/>); en pruebas, un flujo en memoria con identidad fija.
/// </summary>
internal interface IClientChannel : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>
    /// Identidad del cliente según su token. Solo se puede obtener después de leer al menos un mensaje del cliente
    /// (requisito de <c>ImpersonateNamedPipeClient</c>).
    /// </summary>
    ClientIdentity GetClientIdentity();

    /// <summary>
    /// Ejecuta <paramref name="action"/> de forma síncrona suplantando al cliente (§8.6, §8.7). La suplantación se revierte
    /// al salir, también si lanza.
    /// </summary>
    void RunAsClient(Action action);
}

/// <summary><see cref="IClientChannel"/> sobre una instancia conectada del pipe.</summary>
internal sealed class PipeClientChannel(NamedPipeServerStream pipe) : IClientChannel
{
    public Stream Stream => pipe;

    public ClientIdentity GetClientIdentity()
    {
        ClientIdentity? identity = null;
        // RunAsClient lanza si ImpersonateNamedPipeClient falla: nunca se ejecuta nada como SYSTEM por error.
        pipe.RunAsClient(() => identity = TokenInspector.InspectImpersonationToken());
        return identity ?? throw new InvalidOperationException("The client identity could not be read.");
    }

    public void RunAsClient(Action action) => pipe.RunAsClient(() => action());

    public ValueTask DisposeAsync()
    {
        try
        {
            if (pipe.IsConnected)
            {
                pipe.Disconnect();
            }
        }
        catch (IOException)
        {
            // El cliente ya se ha ido.
        }
        catch (InvalidOperationException)
        {
            // La instancia ya no está conectada.
        }

        return pipe.DisposeAsync();
    }
}
