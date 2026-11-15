using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Backbone.Service.Os;

/// <summary>
/// <see cref="IAccountDirectory"/> con la API de Windows: <c>LookupAccountName</c>/<c>LookupAccountSid</c> para resolver
/// cuentas y Authz (<c>AuthzInitializeContextFromSid</c>) para calcular la pertenencia actual a grupos, incluidos los
/// anidados, sin depender del token con el que el cliente inició sesión.
/// </summary>
internal sealed class WindowsAccountDirectory : IAccountDirectory
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public AccountInfo? LookupByName(string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifiedName);

        uint cbSid = 0;
        uint cchDomain = 0;
        if (!NativeMethods.LookupAccountName(null, qualifiedName, null, ref cbSid, null, ref cchDomain, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorNoneMapped)
            {
                return null;
            }

            if (error != NativeMethods.ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }
        }

        var sidBytes = new byte[cbSid];
        var domain = new char[cchDomain];
        if (!NativeMethods.LookupAccountName(null, qualifiedName, sidBytes, ref cbSid, domain, ref cchDomain, out var use))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ErrorNoneMapped ? null : throw new Win32Exception(error);
        }

        var sid = new SecurityIdentifier(sidBytes, 0);
        // El nombre devuelto es el canónico (mayúsculas y dominio como los guarda el sistema).
        return LookupBySid(sid) ?? new AccountInfo(sid, qualifiedName, ToKind(use));
    }

    public AccountInfo? LookupBySid(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);

        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);

        uint cchName = 0;
        uint cchDomain = 0;
        if (!NativeMethods.LookupAccountSid(null, sidBytes, null, ref cchName, null, ref cchDomain, out _))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorNoneMapped)
            {
                return null;
            }

            if (error != NativeMethods.ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }
        }

        var name = new char[cchName];
        var domain = new char[cchDomain];
        if (!NativeMethods.LookupAccountSid(null, sidBytes, name, ref cchName, domain, ref cchDomain, out var use))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == NativeMethods.ErrorNoneMapped ? null : throw new Win32Exception(error);
        }

        // Tras el éxito, cchName y cchDomain son las longitudes sin el carácter nulo final.
        var account = new string(name, 0, (int)cchName);
        var domainName = new string(domain, 0, (int)cchDomain);
        var qualified = domainName.Length == 0 ? account : domainName + "\\" + account;
        return new AccountInfo(sid, qualified, ToKind(use));
    }

    public bool IsLocalAdministrator(SecurityIdentifier sid)
    {
        ArgumentNullException.ThrowIfNull(sid);

        if (!NativeMethods.AuthzInitializeResourceManager(NativeMethods.AuthzRmFlagNoAudit, 0, 0, 0, null, out var resourceManager))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            var sidBytes = new byte[sid.BinaryLength];
            sid.GetBinaryForm(sidBytes, 0);

            // Flags = 0: Authz calcula los grupos actuales (S4U o consulta de la cuenta), incluidos los alias locales anidados.
            if (!NativeMethods.AuthzInitializeContextFromSid(0, sidBytes, resourceManager, 0, default, 0, out var context))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                return ContextContainsGroup(context, Administrators);
            }
            finally
            {
                NativeMethods.AuthzFreeContext(context);
            }
        }
        finally
        {
            NativeMethods.AuthzFreeResourceManager(resourceManager);
        }
    }

    private static bool ContextContainsGroup(nint context, SecurityIdentifier group)
    {
        if (!NativeMethods.AuthzGetInformationFromContext(context, NativeMethods.AuthzContextInfoGroupsSids, 0, out var size, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != NativeMethods.ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }
        }

        var buffer = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (!NativeMethods.AuthzGetInformationFromContext(context, NativeMethods.AuthzContextInfoGroupsSids, size, out _, buffer))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            // TOKEN_GROUPS { DWORD GroupCount; SID_AND_ATTRIBUTES Groups[]; }: el array empieza alineado a puntero y cada
            // SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } ocupa dos punteros con el relleno.
            var count = Marshal.ReadInt32(buffer);
            var first = buffer + IntPtr.Size;
            var entrySize = 2 * IntPtr.Size;
            for (var i = 0; i < count; i++)
            {
                var sidPointer = Marshal.ReadIntPtr(first + (i * entrySize));
                if (sidPointer != 0 && new SecurityIdentifier(sidPointer) == group)
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static AccountKind ToKind(int use) => use == NativeMethods.SidTypeUser ? AccountKind.User : AccountKind.Other;
}
