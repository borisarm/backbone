using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Jobs;
using Backbone.Service.Nas;
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
/// <see cref="ConnectionRpcTarget"/>. En HT-03: información del servicio y usuarios autorizados (HU-31). En HU-04:
/// credenciales del NAS. En HU-05: "Probar conexión". En HU-07: trabajos (<c>jobs.json</c>) y su validación.
/// </summary>
internal sealed class BackboneOperations(
    ServiceRuntimeInfo runtime,
    SettingsStore settings,
    IAccountDirectory accounts,
    ICredentialStore credentials,
    JobStore jobs,
    JobValidator jobValidator,
    IJobActivity jobActivity,
    ServiceStatusProvider status,
    ConnectionTester connectionTester)
{
    // ---- Estado y trabajos (HU-07) ----

    /// <summary>Estado actual (§7.2). En HU-07, la lista de trabajos (ver <see cref="ServiceStatusProvider"/>).</summary>
    public Task<ServiceStatus> GetStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(status.GetStatus());
    }

    /// <summary>Trabajos guardados, en el orden de <c>jobs.json</c>.</summary>
    public Task<IReadOnlyList<JobDefinition>> GetJobsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<JobDefinition> list = [.. jobs.Jobs];
        return Task.FromResult(list);
    }

    /// <summary>RF-03 (HU-07 criterios 2, 3, 4 y 6): errores y avisos del trabajo, sin guardar nada.</summary>
    public Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(jobValidator.Validate(job, jobs.Jobs));
    }

    /// <summary>
    /// Crea (<c>Id</c> vacío: el servicio asigna uno) o sustituye un trabajo (§7.2, §7.5):
    /// <list type="number">
    /// <item><see cref="BackboneErrorCode.JobNotFound"/> si el <c>Id</c> no está vacío y no existe;</item>
    /// <item><see cref="BackboneErrorCode.ValidationFailed"/> con el resultado completo si hay errores (los avisos no lo
    /// impiden);</item>
    /// <item><see cref="BackboneErrorCode.CredentialRequired"/> si no hay credencial guardada para el recurso del destino
    /// (§7.5 A-3: nunca se usa la cuenta del equipo);</item>
    /// <item>escritura atómica de <c>jobs.json</c>, con el destino normalizado, y <c>StatusChanged</c>.</item>
    /// </list>
    /// Existencia y destino repetido se comprueban otra vez bajo el bloqueo de escritura, por si otro cliente ha guardado
    /// entretanto.
    /// </summary>
    /// <returns>El trabajo tal como se ha guardado.</returns>
    public async Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct)
    {
        if (job.Id != Guid.Empty && jobs.Find(job.Id) is null)
        {
            throw JobNotFound();
        }

        var validation = jobValidator.Validate(job, jobs.Jobs);
        if (!validation.IsValid)
        {
            throw BackboneException.ValidationFailed(validation);
        }

        if (!JobValidator.TryNormalizeDestination(job.Destination, out var destination, out var issue)
            || !CredentialStore.TryNormalizeShareRoot(destination, out var shareRoot, out issue))
        {
            throw BackboneException.ValidationFailed(nameof(JobDefinition.Destination), issue);
        }

        if (await credentials.GetInfoAsync(shareRoot, ct).ConfigureAwait(false) is null)
        {
            throw new BackboneException(BackboneErrorCode.CredentialRequired, "No credential is saved for the destination share.");
        }

        var toSave = job with { Id = job.Id == Guid.Empty ? Guid.NewGuid() : job.Id, Destination = destination };
        return await jobs.UpdateAsync(
            list =>
            {
                var index = IndexOf(list, toSave.Id);
                if (job.Id != Guid.Empty && index < 0)
                {
                    throw JobNotFound();
                }

                if (JobValidator.IsDestinationUsedByOtherJob(toSave.Id, destination, list))
                {
                    throw BackboneException.ValidationFailed(nameof(JobDefinition.Destination), ValidationIssueCode.DestinationUsedByOtherJob);
                }

                return (index < 0 ? list.Add(toSave) : list.SetItem(index, toSave), toSave);
            },
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Borra un trabajo de <c>jobs.json</c> (RF-01: el repositorio del NAS no se toca) y emite <c>StatusChanged</c>.
    /// <see cref="BackboneErrorCode.JobNotFound"/> si no existe; <see cref="BackboneErrorCode.JobAlreadyRunning"/> si está en
    /// ejecución o en cola (§7.5).
    /// </summary>
    public Task DeleteJobAsync(Guid jobId, CancellationToken ct) =>
        jobs.UpdateAsync(
            list =>
            {
                var index = IndexOf(list, jobId);
                if (index < 0)
                {
                    throw JobNotFound();
                }

                if (jobActivity.GetActivity(jobId) != JobActivity.Idle)
                {
                    throw new BackboneException(BackboneErrorCode.JobAlreadyRunning, "The job is running or queued; cancel it first.");
                }

                return (list.RemoveAt(index), true);
            },
            ct);

    private static int IndexOf(ImmutableArray<JobDefinition> list, Guid id)
    {
        for (var i = 0; i < list.Length; i++)
        {
            if (list[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private static BackboneException JobNotFound() => new(BackboneErrorCode.JobNotFound, "The job does not exist.");

    // ---- Credenciales (HU-04) ----
    /// <summary>HU-05 (RF-61): prueba la conexión con el destino; un fallo es un resultado, no una excepción (§7.4).</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(string destination, CredentialInput? credential, CancellationToken ct) =>
        connectionTester.TestAsync(destination, credential, ct);


    /// <summary>
    /// HU-04 (RF-60, ADR-07): guarda o sustituye la credencial del recurso. El usuario no puede estar vacío ni tener
    /// caracteres de control; ni el usuario ni la contraseña pueden tener NUL ni suplentes sueltas (no se pasarían intactos a
    /// la API de Windows). Los errores no llevan valores.
    /// </summary>
    public async Task SaveCredentialAsync(string shareRoot, CredentialInput credential, CancellationToken ct)
    {
        var root = NormalizeShareRoot(shareRoot);
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrWhiteSpace(credential.UserName) || !CredentialStore.IsStorable(credential.UserName, allowControl: false))
        {
            issues.Add(new ValidationIssue { Field = nameof(CredentialInput.UserName), Code = ValidationIssueCode.ValueOutOfRange });
        }

        if (!CredentialStore.IsStorable(credential.Password, allowControl: true))
        {
            issues.Add(new ValidationIssue { Field = nameof(CredentialInput.Password), Code = ValidationIssueCode.ValueOutOfRange });
        }

        if (issues.Count > 0)
        {
            throw BackboneException.ValidationFailed(new ValidationResult { Errors = issues });
        }

        await credentials.SaveAsync(root, credential, ct).ConfigureAwait(false);
    }

    /// <summary>HU-04 (RF-62): usuario y fecha de la credencial guardada, nunca la contraseña.</summary>
    public Task<CredentialInfo?> GetCredentialInfoAsync(string shareRoot, CancellationToken ct) =>
        credentials.GetInfoAsync(NormalizeShareRoot(shareRoot), ct);

    /// <summary>HU-04: borra la credencial del recurso; borrar una que no existe no es un error.</summary>
    public async Task DeleteCredentialAsync(string shareRoot, CancellationToken ct) =>
        await credentials.DeleteAsync(NormalizeShareRoot(shareRoot), ct).ConfigureAwait(false);

    /// <summary>
    /// Raíz normalizada del recurso (<see cref="CredentialStore.TryNormalizeShareRoot"/>). Se admite también un destino con
    /// ruta (<c>\\nas\copias\pc</c>): la credencial es del recurso.
    /// </summary>
    private static string NormalizeShareRoot(string value) =>
        CredentialStore.TryNormalizeShareRoot(value, out var root, out var issue)
            ? root
            : throw BackboneException.ValidationFailed(string.Empty, issue);

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
