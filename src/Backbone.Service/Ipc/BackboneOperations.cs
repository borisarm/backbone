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
/// credenciales del NAS. En HU-05: "Probar conexión". En HU-07: trabajos (<c>jobs.json</c>) y su validación. En HU-06:
/// inicialización o adopción del repositorio del destino al guardar un trabajo.
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
    ConnectionTester connectionTester,
    RepositoryPreparer repositories,
    RepositoryStateTracker repositoryStates,
    RunQueue runs)
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

    /// <summary>
    /// RF-03 (HU-07 criterios 2, 3, 4 y 6): errores y avisos del trabajo, sin guardar nada. Es de escritura (§7.3): abre como
    /// SYSTEM las rutas que indica el cliente. La resolución de los orígenes se hace fuera del hilo RPC (MEDIA-1).
    /// </summary>
    public async Task<ValidationResult> ValidateJobAsync(JobDefinition job, CancellationToken ct) =>
        (await jobValidator.ValidateAsync(job, jobs.Jobs, ct).ConfigureAwait(false)).Issues;

    /// <summary>
    /// Crea (<c>Id</c> vacío: el servicio asigna uno) o sustituye un trabajo (§7.2, §7.5):
    /// <list type="number">
    /// <item><see cref="BackboneErrorCode.JobNotFound"/> si el <c>Id</c> no está vacío y no existe;</item>
    /// <item><see cref="BackboneErrorCode.ValidationFailed"/> con el resultado completo si hay errores (los avisos no lo
    /// impiden);</item>
    /// <item><see cref="BackboneErrorCode.CredentialRequired"/> si no hay credencial guardada para el recurso del destino
    /// (§7.5 A-3: nunca se usa la cuenta del equipo);</item>
    /// <item>inicialización o adopción del repositorio del destino (RF-63, HU-06, <see cref="RepositoryPreparer"/>): un
    /// destino con archivos que no son un repositorio falla con <see cref="BackboneErrorCode.ValidationFailed"/>
    /// (<see cref="ValidationIssueCode.DestinationNotRepository"/>) y uno dañado con
    /// <see cref="BackboneErrorCode.RepositoryCorrupt"/>, en los dos casos sin escribir nada; uno de formato superior se
    /// guarda con <see cref="Contracts.RepositoryState.ReadOnlyNewerFormat"/> en <see cref="JobStatus.RepositoryState"/>
    /// (RNF-14). Los fallos de conexión dan su código (§7.2);</item>
    /// <item>escritura atómica de <c>jobs.json</c>, con las rutas de origen y el destino <b>normalizados</b> (§7.5 "Ruta de
    /// origen", BAJA-1), y <c>StatusChanged</c> (que ya lleva el estado del repositorio).</item>
    /// </list>
    /// Existencia, destino repetido y credencial (BAJA-2) se comprueban otra vez bajo el bloqueo de escritura, por si otro
    /// cliente ha guardado o borrado entretanto.
    /// </summary>
    /// <returns>El trabajo tal como se ha guardado (con las rutas normalizadas).</returns>
    public async Task<JobDefinition> SaveJobAsync(JobDefinition job, CancellationToken ct)
    {
        if (job.Id != Guid.Empty && jobs.Find(job.Id) is null)
        {
            throw JobNotFound();
        }

        var validation = await jobValidator.ValidateAsync(job, jobs.Jobs, ct).ConfigureAwait(false);
        if (!validation.Issues.IsValid)
        {
            throw BackboneException.ValidationFailed(validation.Issues);
        }

        var destination = validation.Job.Destination;
        if (!CredentialStore.TryNormalizeShareRoot(destination, out var shareRoot, out var issue))
        {
            throw BackboneException.ValidationFailed(nameof(JobDefinition.Destination), issue);
        }

        await RequireCredentialAsync(shareRoot, ct).ConfigureAwait(false);

        // RF-63: antes de guardar nada en jobs.json, el destino tiene que ser un repositorio utilizable (o quedar inicializado).
        var prepared = await repositories.PrepareAsync(destination, ct).ConfigureAwait(false);
        var repositoryState = prepared == RepositoryPreparationOutcome.ReadOnlyNewerFormat
            ? Contracts.RepositoryState.ReadOnlyNewerFormat
            : Contracts.RepositoryState.ReadWrite;

        var toSave = validation.Job with { Id = job.Id == Guid.Empty ? Guid.NewGuid() : job.Id };
        var previousState = repositoryStates.Get(toSave.Id);
        try
        {
            return await SaveValidatedJobAsync(job, toSave, destination, shareRoot, repositoryState, ct).ConfigureAwait(false);
        }
        catch
        {
            // Solo si nadie lo ha cambiado entretanto (otro guardado del mismo trabajo): su valor es más reciente que el nuestro.
            repositoryStates.RestoreIfUnchanged(toSave.Id, repositoryState, previousState);
            throw;
        }
    }

    private Task<JobDefinition> SaveValidatedJobAsync(
        JobDefinition job,
        JobDefinition toSave,
        string destination,
        string shareRoot,
        Contracts.RepositoryState repositoryState,
        CancellationToken ct) =>
        jobs.UpdateAsync(
            async (list, token) =>
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

                // BAJA-2: la credencial puede haberse borrado mientras se validaba.
                await RequireCredentialAsync(shareRoot, token).ConfigureAwait(false);

                // Antes de persistir: StatusChanged se emite al persistir y ya debe llevar el estado del repositorio.
                repositoryStates.Set(toSave.Id, repositoryState);
                return (index < 0 ? list.Add(toSave) : list.SetItem(index, toSave), toSave);
            },
            ct);

    private async Task RequireCredentialAsync(string shareRoot, CancellationToken ct)
    {
        if (await credentials.GetInfoAsync(shareRoot, ct).ConfigureAwait(false) is null)
        {
            throw new BackboneException(BackboneErrorCode.CredentialRequired, "No credential is saved for the destination share.");
        }
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

                repositoryStates.Remove(jobId);
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

    // ---- Ejecución (HU-14) ----

    /// <summary>
    /// "Copiar ahora" (RF-20): pone el trabajo en la cola o lo lanza ya, aunque esté desactivado o haya pausa; no espera a que
    /// termine (el resultado llega con <c>RunCompleted</c>). <see cref="BackboneErrorCode.JobNotFound"/>;
    /// <see cref="BackboneErrorCode.JobAlreadyRunning"/> si ya está en curso o en cola (no se duplica).
    /// </summary>
    public Task RunNowAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        runs.Enqueue(jobId, RunTrigger.Manual);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancela la ejecución en curso o en cola del trabajo (RF-21). Idempotente: sin ejecución, no hace nada.
    /// <see cref="BackboneErrorCode.JobNotFound"/> si el trabajo no existe. No espera a que la ejecución termine de liberar sus
    /// recursos.
    /// </summary>
    public Task CancelRunAsync(Guid jobId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        runs.Cancel(jobId);
        return Task.CompletedTask;
    }

    // ---- Credenciales (HU-04) ----
    /// <summary>HU-05 (RF-61): prueba la conexión con el destino; un fallo es un resultado, no una excepción (§7.4).</summary>
    public Task<ConnectionTestResult> TestConnectionAsync(CallerContext caller, string destination, CredentialInput? credential, CancellationToken ct) =>
        connectionTester.TestAsync(destination, credential, caller.Sid, ct);


    /// <summary>
    /// HU-04 (RF-60, ADR-07): guarda o sustituye la credencial del recurso. El usuario no puede estar vacío ni tener
    /// caracteres de control; ni el usuario ni la contraseña pueden tener NUL ni suplentes sueltas (no se pasarían intactos a
    /// la API de Windows). Los errores no llevan valores.
    /// </summary>
    public async Task SaveCredentialAsync(string callerSid, string shareRoot, CredentialInput credential, CancellationToken ct)
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

        // B-2 (revisión de seguridad de HU-14): probar la credencial recién guardada cuenta como probar una explícita.
        connectionTester.NoteCredentialSaved(callerSid, root);
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
