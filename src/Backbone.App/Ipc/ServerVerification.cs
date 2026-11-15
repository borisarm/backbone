using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Backbone.App.Ipc;

/// <summary>
/// Comprueba que el proceso al otro lado del pipe es el servicio auténtico (§7.1 A-1). Se ejecuta nada más conectar y
/// <b>antes de enviar ningún mensaje</b>: el servidor solo puede suplantar al cliente después de leer un mensaje suyo.
/// </summary>
internal interface IServerVerifier
{
    /// <exception cref="ServerVerificationException">No es el servicio Backbone.</exception>
    void Verify(SafePipeHandle pipe);
}

/// <summary>Proceso de un servicio de Windows según el SCM.</summary>
/// <param name="IsRunning">El servicio está en ejecución.</param>
/// <param name="ProcessId">PID del proceso del servicio (0 si no corre).</param>
/// <param name="StartName">Cuenta con la que corre (<c>LocalSystem</c>).</param>
/// <param name="ServiceType">Tipo de servicio; debe ser <c>SERVICE_WIN32_OWN_PROCESS</c> (0x10).</param>
internal sealed record ServiceProcessInfo(
    bool IsRunning,
    int ProcessId,
    string StartName,
    uint ServiceType = NativeMethods.ServiceWin32OwnProcess);

/// <summary>Consulta de un servicio al SCM (se sustituye en las pruebas).</summary>
internal interface IServiceQuery
{
    /// <summary>Devuelve la información del servicio o <see langword="null"/> si no está instalado.</summary>
    ServiceProcessInfo? Query(string serviceName);
}

/// <summary>Lectura de las propiedades del extremo servidor de un pipe (se sustituye en las pruebas).</summary>
internal interface IPipeInspector
{
    /// <summary>PID del proceso servidor (<c>GetNamedPipeServerProcessId</c>).</summary>
    int GetServerProcessId(SafePipeHandle pipe);

    /// <summary>Sesión del proceso servidor (<c>GetNamedPipeServerSessionId</c>); los servicios corren en la sesión 0.</summary>
    uint GetServerSessionId(SafePipeHandle pipe);

    /// <summary>Propietario del descriptor de seguridad del pipe (<c>GetSecurityInfo</c> sobre el handle del cliente).</summary>
    SecurityIdentifier GetOwner(SafePipeHandle pipe);
}

/// <summary>Implementación real de <see cref="IPipeInspector"/> con las API del sistema.</summary>
internal sealed class NativePipeInspector : IPipeInspector
{
    public int GetServerProcessId(SafePipeHandle pipe)
    {
        if (!NativeMethods.GetNamedPipeServerProcessId(pipe, out var pid))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return checked((int)pid);
    }

    public uint GetServerSessionId(SafePipeHandle pipe)
    {
        if (!NativeMethods.GetNamedPipeServerSessionId(pipe, out var session))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return session;
    }

    public SecurityIdentifier GetOwner(SafePipeHandle pipe)
    {
        var error = NativeMethods.GetSecurityInfo(
            pipe, NativeMethods.SeKernelObject, NativeMethods.OwnerSecurityInformation, out var owner, 0, 0, 0, out var descriptor);
        if (error != 0)
        {
            throw new Win32Exception((int)error);
        }

        try
        {
            return owner == 0
                ? throw new ServerVerificationException("The pipe has no owner.")
                : new SecurityIdentifier(owner);
        }
        finally
        {
            _ = NativeMethods.LocalFree(descriptor);
        }
    }
}

/// <summary>
/// Verificación de §7.1 A-1, todo antes de enviar ningún mensaje: (1) el propietario del descriptor de seguridad del pipe es
/// <c>S-1-5-18</c> o <c>S-1-5-32-544</c> (un proceso sin privilegios no puede crear un pipe con ese propietario); (2) el servidor
/// está en la sesión 0; (3) el PID del servidor (<c>GetNamedPipeServerProcessId</c>) es el de <c>BackboneService</c> según el SCM
/// (<c>QueryServiceStatusEx</c>, tipo <c>SERVICE_WIN32_OWN_PROCESS</c>) y este corre como LocalSystem. El PID del pipe se lee antes
/// y después de consultar al SCM y debe coincidir, para cerrar la ventana en que el proceso pudiera cambiar.
/// </summary>
/// <remarks>
/// Ver §7.1 A-1. Se consulta al SCM y no el token del proceso porque un token de SYSTEM no se puede abrir desde una app sin
/// elevar (su DACL solo concede acceso a SYSTEM y administradores), mientras que la configuración del servicio
/// (<c>QueryServiceConfig</c>, <c>lpServiceStartName</c> = <c>LocalSystem</c>) la pueden leer los usuarios autenticados y solo un
/// administrador puede cambiarla.
/// </remarks>
internal sealed class ServiceProcessVerifier : IServerVerifier
{
    /// <summary>Nombre del servicio de Windows (§10).</summary>
    public const string DefaultServiceName = "BackboneService";

    /// <summary>Propietarios aceptados del pipe en producción: SYSTEM y Administradores.</summary>
    internal static readonly IReadOnlyList<SecurityIdentifier> ProductionOwners =
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
    ];

    private readonly IServiceQuery _query;
    private readonly IPipeInspector _pipe;
    private readonly string _serviceName;
    private readonly IReadOnlyList<SecurityIdentifier> _allowedOwners;

    public ServiceProcessVerifier()
        : this(new ScmServiceQuery(), new NativePipeInspector(), DefaultServiceName)
    {
    }

    /// <param name="allowedOwners">
    /// Solo para pruebas: propietarios aceptados. En producción es siempre <see cref="ProductionOwners"/> (valor por defecto).
    /// </param>
    internal ServiceProcessVerifier(
        IServiceQuery query,
        IPipeInspector pipe,
        string serviceName,
        IReadOnlyList<SecurityIdentifier>? allowedOwners = null)
    {
        _query = query;
        _pipe = pipe;
        _serviceName = serviceName;
        _allowedOwners = allowedOwners ?? ProductionOwners;
    }

    public void Verify(SafePipeHandle pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        try
        {
            var owner = _pipe.GetOwner(pipe);
            var session = _pipe.GetServerSessionId(pipe);
            var pidBefore = _pipe.GetServerProcessId(pipe);
            var info = _query.Query(_serviceName);
            var pidAfter = _pipe.GetServerProcessId(pipe);

            Check(owner, session, pidBefore, pidAfter, info, _allowedOwners);
        }
        catch (Win32Exception ex)
        {
            throw new ServerVerificationException("The server process could not be verified.", ex);
        }
    }

    /// <summary>Lógica de decisión, separada de las llamadas al sistema.</summary>
    /// <exception cref="ServerVerificationException">El servidor no es el servicio auténtico.</exception>
    internal static void Check(
        SecurityIdentifier pipeOwner,
        uint serverSessionId,
        int pipePidBefore,
        int pipePidAfter,
        ServiceProcessInfo? info,
        IReadOnlyList<SecurityIdentifier>? allowedOwners = null)
    {
        if (!(allowedOwners ?? ProductionOwners).Contains(pipeOwner))
        {
            throw new ServerVerificationException("The pipe is not owned by SYSTEM or Administrators.");
        }

        if (serverSessionId != 0)
        {
            throw new ServerVerificationException("The pipe server is not running in session 0.");
        }

        Check(pipePidBefore, info);

        if (pipePidAfter != pipePidBefore)
        {
            throw new ServerVerificationException("The pipe server process changed during verification.");
        }
    }

    /// <summary>Compara el PID del pipe con la respuesta del SCM.</summary>
    internal static void Check(int pipeServerPid, ServiceProcessInfo? info)
    {
        if (info is null || !info.IsRunning || info.ProcessId <= 0)
        {
            throw new ServerVerificationException("The Backbone service is not running.");
        }

        if (info.ServiceType != NativeMethods.ServiceWin32OwnProcess)
        {
            throw new ServerVerificationException("The Backbone service is not a standalone Win32 service process.");
        }

        if (info.ProcessId != pipeServerPid)
        {
            throw new ServerVerificationException("The pipe server is not the Backbone service process.");
        }

        if (!IsLocalSystem(info.StartName))
        {
            throw new ServerVerificationException("The Backbone service does not run as LocalSystem.");
        }
    }

    private static bool IsLocalSystem(string startName) =>
        startName.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)
        || startName.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);

    internal static int GetServerProcessId(SafePipeHandle pipe) => new NativePipeInspector().GetServerProcessId(pipe);
}

/// <summary>Consulta real al Administrador de control de servicios con los derechos mínimos (conectar, consultar estado y configuración).</summary>
internal sealed class ScmServiceQuery : IServiceQuery
{
    public ServiceProcessInfo? Query(string serviceName)
    {
        var scm = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (scm == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var service = NativeMethods.OpenService(scm, serviceName, NativeMethods.ServiceQueryStatus | NativeMethods.ServiceQueryConfig);
            if (service == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == NativeMethods.ErrorServiceDoesNotExist)
                {
                    return null;
                }

                throw new Win32Exception(error);
            }

            try
            {
                if (!NativeMethods.QueryServiceStatusEx(
                        service,
                        NativeMethods.ScStatusProcessInfo,
                        out var status,
                        (uint)Marshal.SizeOf<NativeMethods.ServiceStatusProcess>(),
                        out _))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                return new ServiceProcessInfo(
                    status.CurrentState == NativeMethods.ServiceRunning,
                    checked((int)status.ProcessId),
                    ReadStartName(service),
                    status.ServiceType);
            }
            finally
            {
                _ = NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            _ = NativeMethods.CloseServiceHandle(scm);
        }
    }

    private static string ReadStartName(nint service)
    {
        // Primera llamada con búfer vacío: devuelve el tamaño necesario (ERROR_INSUFFICIENT_BUFFER).
        _ = NativeMethods.QueryServiceConfig(service, 0, 0, out var needed);
        var error = Marshal.GetLastPInvokeError();
        if (error != NativeMethods.ErrorInsufficientBuffer || needed == 0)
        {
            throw new Win32Exception(error);
        }

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.QueryServiceConfig(service, buffer, needed, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var config = Marshal.PtrToStructure<NativeMethods.ServiceConfig>(buffer);
            return Marshal.PtrToStringUni(config.ServiceStartName) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
