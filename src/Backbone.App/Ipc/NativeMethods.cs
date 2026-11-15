using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Las DLL del sistema solo se resuelven en System32 (evita secuestro de DLL desde el directorio de la app o el PATH).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace Backbone.App.Ipc;

/// <summary>
/// Llamadas a la API de Windows de la app (P/Invoke generado con <see cref="LibraryImportAttribute"/>).
/// Firmas de pipe (namedpipeapi.h) y del Administrador de control de servicios (winsvc.h) comprobadas en Microsoft Learn.
/// </summary>
internal static partial class NativeMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const int ScStatusProcessInfo = 0;
    internal const uint ServiceRunning = 4;
    internal const uint ServiceWin32OwnProcess = 0x10;
    internal const int SeKernelObject = 6;
    internal const uint OwnerSecurityInformation = 0x1;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorServiceDoesNotExist = 1060;

    /// <summary><c>SERVICE_STATUS_PROCESS</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    /// <summary>Cabecera de <c>QUERY_SERVICE_CONFIGW</c> (los punteros apuntan al mismo búfer).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceConfig
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public nint BinaryPathName;
        public nint LoadOrderGroup;
        public uint TagId;
        public nint Dependencies;
        public nint ServiceStartName;
        public nint DisplayName;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);

    /// <summary><c>GetSecurityInfo</c> devuelve el código de error directamente (no usa GetLastError).</summary>
    [LibraryImport("advapi32.dll")]
    internal static partial uint GetSecurityInfo(
        SafePipeHandle handle,
        int objectType,
        uint securityInfo,
        out nint owner,
        nint group,
        nint dacl,
        nint sacl,
        out nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    internal static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint OpenService(nint scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceStatusEx(
        nint service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded);

    [LibraryImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryServiceConfig(nint service, nint buffer, uint bufferSize, out uint bytesNeeded);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseServiceHandle(nint handle);
}
