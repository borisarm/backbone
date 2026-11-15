using System.ComponentModel;
using System.Reflection;
using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Os;
using Backbone.Service.Persistence;

namespace Backbone.Service.Ipc;

/// <summary>Datos del proceso del servicio para <see cref="ServiceInfo"/>.</summary>
internal sealed class ServiceRuntimeInfo(TimeProvider timeProvider)
{
    /// <summary>Instante de arranque (UTC).</summary>
    public DateTimeOffset StartedUtc { get; } = timeProvider.GetUtcNow();

    /// <summary>Versión del producto (<c>Mayor.Menor.Revisión</c>, sin metadatos de compilación).</summary>
    public string Version { get; } = ReadVersion();

    public string MachineName { get; } = Environment.MachineName;

    private static string ReadVersion()
    {
        var assembly = typeof(ServiceRuntimeInfo).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? informational : informational[..plus];
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

/// <summary>
/// Operaciones del servicio expuestas por IPC, ya autorizadas y con la entrada genérica validada por
/// <see cref="ConnectionRpcTarget"/>. En HT-03: información del servicio y usuarios autorizados (HU-31).
/// </summary>
internal sealed class BackboneOperations(
    ServiceRuntimeInfo runtime,
    SettingsStore settings,
    IAccountDirectory accounts)
{
    public Task<ServiceInfo> GetServiceInfoAsync(CallerContext caller, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ServiceInfo
        {
            ServiceVersion = runtime.Version,
            ContractVersion = ContractVersion.Value,
            MachineName = runtime.MachineName,
            StartedUtc = runtime.StartedUtc.ToUniversalTime(),
            Caller = new CallerInfo
            {
                UserName = caller.AccountName,
                UserSid = caller.Sid,
                IsElevated = caller.Identity.IsElevated,
            },
        });
    }

    public Task<IReadOnlyList<AuthorizedUser>> GetAuthorizedUsersAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<AuthorizedUser> users =
        [
            .. settings.Sids.Select(sid => new AuthorizedUser { Sid = sid, AccountName = TryResolveName(sid) }),
        ];
        return Task.FromResult(users);
    }

    /// <summary>
    /// §7.3: cliente elevado; solo cuentas de usuario cualificadas (<c>EQUIPO\usuario</c>, <c>DOMINIO\usuario</c> o SID)
    /// miembros actuales del grupo local Administradores. Idempotente.
    /// </summary>
    public async Task<AuthorizedUser> AddAuthorizedUserAsync(CallerContext caller, string account, CancellationToken ct)
    {
        RequireManager(caller);

        var info = ResolveAccount(account.Trim());
        if (info.Kind != AccountKind.User || !SettingsStore.IsAccountSid(info.Sid))
        {
            throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.AccountNotUser);
        }

        bool isAdministrator;
        try
        {
            isAdministrator = accounts.IsLocalAdministrator(info.Sid);
        }
        catch (Win32Exception)
        {
            isAdministrator = false;
        }

        if (!isAdministrator)
        {
            throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.AccountNotAdministrator);
        }

        await settings.AddAuthorizedUserAsync(info.Sid, ct).ConfigureAwait(false);
        return new AuthorizedUser { Sid = info.Sid.Value, AccountName = info.AccountName };
    }

    /// <summary>§7.3: cliente elevado. Quitar un SID que no está no es un error; las conexiones de ese SID se revalidan.</summary>
    public async Task RemoveAuthorizedUserAsync(CallerContext caller, string sid, CancellationToken ct)
    {
        RequireManager(caller);

        SecurityIdentifier parsed;
        try
        {
            parsed = new SecurityIdentifier(sid.Trim());
        }
        catch (ArgumentException)
        {
            throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.AccountNotFound);
        }

        await settings.RemoveAuthorizedUserAsync(parsed, ct).ConfigureAwait(false);
    }

    private static void RequireManager(CallerContext caller)
    {
        if (!caller.CanManageAuthorizedUsers)
        {
            throw BackboneException.NotAuthorized("Managing authorized users requires an elevated client.");
        }
    }

    private AccountInfo ResolveAccount(string account)
    {
        AccountInfo? info;
        if (account.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            SecurityIdentifier sid;
            try
            {
                sid = new SecurityIdentifier(account);
            }
            catch (ArgumentException)
            {
                throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.AccountNotFound);
            }

            info = Lookup(() => accounts.LookupBySid(sid));
        }
        else
        {
            // Solo nombres cualificados DOMINIO\usuario: un nombre suelto o un UPN se rechazan como "no es un usuario".
            var separator = account.IndexOf('\\', StringComparison.Ordinal);
            if (separator <= 0 || separator == account.Length - 1 || account.IndexOf('\\', separator + 1) >= 0)
            {
                throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.AccountNotUser);
            }

            info = Lookup(() => accounts.LookupByName(account));
        }

        return info ?? throw BackboneException.ValidationFailed(string.Empty, ValidationIssueCode.AccountNotFound);
    }

    private static AccountInfo? Lookup(Func<AccountInfo?> lookup)
    {
        try
        {
            return lookup();
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private string? TryResolveName(string sid)
    {
        try
        {
            return accounts.LookupBySid(new SecurityIdentifier(sid))?.AccountName;
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            return null;
        }
    }
}
