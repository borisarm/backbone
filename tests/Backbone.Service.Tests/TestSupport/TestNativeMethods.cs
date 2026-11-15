using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Backbone.Service.Tests.TestSupport;

/// <summary>P/Invoke de las pruebas: token restringido (cliente sin acceso) y comprobación del proceso servidor (§7.1 A-1).</summary>
internal static partial class TestNativeMethods
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenQuery = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    /// <summary>
    /// Token del proceso con <paramref name="sidsToDisable"/> como "solo denegar": sirve para simular otro usuario sin
    /// acceso a la ACL del pipe sin necesitar una segunda cuenta.
    /// </summary>
    public static SafeAccessTokenHandle CreateTokenWithDenyOnly(params SecurityIdentifier[] sidsToDisable)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAssignPrimary | TokenDuplicate | TokenImpersonate | TokenQuery, out var processToken))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var buffers = new List<nint>();
        try
        {
            var entries = new SidAndAttributes[sidsToDisable.Length];
            for (var i = 0; i < sidsToDisable.Length; i++)
            {
                var bytes = new byte[sidsToDisable[i].BinaryLength];
                sidsToDisable[i].GetBinaryForm(bytes, 0);
                var buffer = Marshal.AllocHGlobal(bytes.Length);
                buffers.Add(buffer);
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                entries[i] = new SidAndAttributes { Sid = buffer };
            }

            if (!CreateRestrictedToken(processToken, 0, (uint)entries.Length, entries, 0, 0, 0, 0, out var restricted))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return new SafeAccessTokenHandle(restricted);
        }
        finally
        {
            buffers.ForEach(Marshal.FreeHGlobal);
            CloseHandle(processToken);
        }
    }

    /// <summary>PID del proceso servidor del pipe (lo que la app comprueba antes de enviar nada, §7.1 A-1).</summary>
    public static int GetServerProcessId(SafePipeHandle pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe, out var pid))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (int)pid;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateRestrictedToken(
        nint existingTokenHandle,
        uint flags,
        uint disableSidCount,
        [In] SidAndAttributes[] sidsToDisable,
        uint deletePrivilegeCount,
        nint privilegesToDelete,
        uint restrictedSidCount,
        nint sidsToRestrict,
        out nint newTokenHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
