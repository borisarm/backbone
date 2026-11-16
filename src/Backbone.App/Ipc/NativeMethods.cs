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

    // ---- Bandeja (HU-18): shellapi.h, winuser.h, wingdi.h ----

    internal const uint NimAdd = 0;
    internal const uint NimModify = 1;
    internal const uint NimDelete = 2;
    internal const uint NimSetVersion = 4;
    internal const uint NifMessage = 0x1;
    internal const uint NifIcon = 0x2;
    internal const uint NifTip = 0x4;
    internal const uint NifShowTip = 0x80;
    internal const uint NotifyIconVersion4 = 4;
    internal const uint NinSelect = 0x400;
    internal const uint NinKeySelect = 0x401;
    internal const uint WmContextMenu = 0x007B;
    internal const uint WmApp = 0x8000;
    internal const uint WmNull = 0;
    internal const uint MfString = 0x0;
    internal const uint MfGrayed = 0x1;
    internal const uint MfDisabled = 0x2;
    internal const uint MfSeparator = 0x800;
    internal const uint TpmRightButton = 0x2;
    internal const uint TpmBottomAlign = 0x20;
    internal const uint TpmReturnCmd = 0x100;
    internal const uint WsPopup = 0x80000000;
    internal const uint WsExToolWindow = 0x80;
    internal const int SmCxSmIcon = 49;
    internal const int SmCySmIcon = 50;

    /// <summary><c>NOTIFYICONDATAW</c> (shellapi.h).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NotifyIconData
    {
        public uint CbSize;
        public nint HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public nint HIcon;
        public fixed char SzTip[128];
        public uint DwState;
        public uint DwStateMask;
        public fixed char SzInfo[256];
        public uint UVersion;
        public fixed char SzInfoTitle[64];
        public uint DwInfoFlags;
        public Guid GuidItem;
        public nint HBalloonIcon;
    }

    /// <summary><c>WNDCLASSEXW</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WndClassEx
    {
        public uint CbSize;
        public uint Style;
        public nint LpfnWndProc;
        public int CbClsExtra;
        public int CbWndExtra;
        public nint HInstance;
        public nint HIcon;
        public nint HCursor;
        public nint HbrBackground;
        public nint LpszMenuName;
        public nint LpszClassName;
        public nint HIconSm;
    }

    /// <summary><c>ICONINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        public int FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public nint HbmMask;
        public nint HbmColor;
    }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(ref WndClassEx windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string message);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? moduleName);

    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenu(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetricsForDpi(int index, uint dpi);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, nint bits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CreateIconIndirect(ref IconInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);


    // ---- Instancia única (Program): synchapi.h, combaseapi.h ----

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateEvent(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    internal static partial uint CoWaitForMultipleObjects(uint flags, uint milliseconds, uint handleCount, nint[] handles, out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(uint processId);
}
