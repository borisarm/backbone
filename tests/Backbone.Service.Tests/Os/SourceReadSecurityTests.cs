using System.Security.Principal;
using Backbone.Contracts;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Sources;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using R = Backbone.Service.Os.NativeMethods.Resolution;
using S = Backbone.Service.Os.NativeMethods.SourceIo;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// Token de lectura de los orígenes y aperturas de solo lectura (ADR-21, §8.2 "Token de lectura de los orígenes"), hilos
/// dedicados, y el contador único de hilos abandonados compartido con la validación (M-1, R12; requisito de HU-14).
/// </summary>
[Trait("Story", "HU-08")]
public sealed class SourceReadSecurityTests
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint GenericAll = 0x1000_0000;
    private const uint GenericExecute = 0x2000_0000;
    private const uint MaximumAllowed = 0x0200_0000;
    private const uint AccessSystemSecurity = 0x0100_0000;
    private const uint Delete = 0x0001_0000;
    private const uint ReadControl = 0x0002_0000;
    private const uint WriteDac = 0x0004_0000;
    private const uint WriteOwner = 0x0008_0000;
    private const uint FileWriteData = 0x0002;
    private const uint FileAppendData = 0x0004;
    private const uint FileWriteEa = 0x0010;
    private const uint FileDeleteChild = 0x0040;
    private const uint FileWriteAttributes = 0x0100;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ============================================================ Solo lectura, comprobado antes de NtCreateFile

    [Theory]
    [InlineData(NativeMethods.GenericRead)]
    [InlineData(NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes | NativeMethods.Synchronize)]
    [InlineData(NativeMethods.FileReadAttributes | NativeMethods.Synchronize)]
    [InlineData(NativeMethods.GenericRead | NativeMethods.FileListDirectory | NativeMethods.FileReadAttributes | NativeMethods.Synchronize)]
    public void Read_only_access_with_FILE_OPEN_is_accepted(uint access) =>
        SourceOpen.Validate(access, R.FileOpen, SourceOpen.RequiredOptions | R.FileOpenReparsePoint, "a", relative: true);

    [Theory]
    [InlineData(0u)]
    [InlineData(GenericWrite)]
    [InlineData(GenericAll)]
    [InlineData(GenericExecute)]
    [InlineData(MaximumAllowed)]
    [InlineData(AccessSystemSecurity)]
    [InlineData(Delete)]
    [InlineData(ReadControl)]
    [InlineData(WriteDac)]
    [InlineData(WriteOwner)]
    [InlineData(FileWriteData)]
    [InlineData(FileAppendData)]
    [InlineData(FileWriteEa)]
    [InlineData(FileDeleteChild)]
    [InlineData(FileWriteAttributes)]
    [InlineData(NativeMethods.GenericRead | FileWriteData)]
    [InlineData(NativeMethods.GenericRead | Delete)]
    public void Any_access_outside_read_is_a_programming_error(uint access) =>
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Validate(access, R.FileOpen, SourceOpen.RequiredOptions, "a", relative: true));

    [Theory]
    [InlineData(0u)] // FILE_SUPERSEDE
    [InlineData(2u)] // FILE_CREATE
    [InlineData(3u)] // FILE_OPEN_IF
    [InlineData(4u)] // FILE_OVERWRITE
    [InlineData(5u)] // FILE_OVERWRITE_IF
    public void Any_disposition_other_than_FILE_OPEN_is_a_programming_error(uint disposition) =>
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Validate(NativeMethods.GenericRead, disposition, SourceOpen.RequiredOptions, "a", relative: true));

    [Theory]
    [InlineData(S.FileDeleteOnClose)]
    [InlineData(S.FileOpenByFileId)]
    public void Delete_on_close_and_open_by_id_are_forbidden(uint option) =>
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Validate(NativeMethods.GenericRead, R.FileOpen, SourceOpen.RequiredOptions | option, "a", relative: true));

    [Fact]
    public void A_relative_open_names_one_component_and_an_absolute_one_needs_a_name()
    {
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Validate(NativeMethods.GenericRead, R.FileOpen, 0, @"a\b", relative: true));
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Validate(NativeMethods.GenericRead, R.FileOpen, 0, string.Empty, relative: false));
        SourceOpen.Validate(NativeMethods.GenericRead, R.FileOpen, 0, string.Empty, relative: true); // reapertura del propio objeto
    }

    [Fact]
    public void The_open_function_refuses_before_calling_the_system()
    {
        // Si llamara a NtCreateFile con una ruta inexistente devolvería un NTSTATUS; aquí tiene que lanzar antes.
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Open(@"\??\Z:\no-existe\", null, NativeMethods.GenericRead | Delete, 0, 0, out _));
        Assert.Throws<InvalidOperationException>(() => SourceOpen.Open(@"\??\Z:\no-existe\", null, GenericWrite, 0, 0, out _));
    }

    [Fact]
    public void Every_source_open_always_adds_backup_intent_and_complete_if_oplocked()
    {
        Assert.Equal(R.FileOpenForBackupIntent | S.FileCompleteIfOplocked, SourceOpen.RequiredOptions);
        Assert.Equal(0x4000u, R.FileOpenForBackupIntent);
        Assert.Equal(0x100u, S.FileCompleteIfOplocked);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void The_parameters_of_each_open_mode_are_read_only_and_never_follow_a_reparse_point(int modeValue)
    {
        var mode = (SourceOpenMode)modeValue;
        var (access, share, options) = Win32SourceFileApi.Parameters(mode);

        SourceOpen.Validate(access, R.FileOpen, options | SourceOpen.RequiredOptions, "a", relative: true);
        Assert.NotEqual(0u, options & R.FileOpenReparsePoint);
        Assert.Equal(mode == SourceOpenMode.Directory, (share & NativeMethods.FileShareDelete) != 0);
        Assert.Equal(mode == SourceOpenMode.Content, (options & R.FileSynchronousIoNonAlert) == 0); // contenido: E/S asíncrona
        Assert.Equal(mode == SourceOpenMode.Directory, (options & S.FileDirectoryFile) != 0);
        Assert.Equal(mode == SourceOpenMode.Content, (options & S.FileNonDirectoryFile) != 0);
    }

    [Fact]
    public void The_whitelist_reopen_drops_only_FILE_OPEN_REPARSE_POINT_and_keeps_reading_without_share_delete()
    {
        SourceOpen.Validate(Win32SourceFileApi.ContentAccess, R.FileOpen, Win32SourceFileApi.ReOpenOptions | SourceOpen.RequiredOptions, string.Empty, relative: true);
        Assert.Equal(0u, Win32SourceFileApi.ReOpenOptions & R.FileOpenReparsePoint);
        Assert.NotEqual(0u, Win32SourceFileApi.ReOpenOptions & S.FileNonDirectoryFile);
        Assert.Equal(0u, Win32SourceFileApi.FileShare & NativeMethods.FileShareDelete);
    }

    [Fact]
    public void The_identification_SQOS_is_not_the_CreateFile_flag_that_collides_with_open_no_recall()
    {
        // §8.2: nunca SECURITY_SQOS_PRESENT en CreateFile (vale lo mismo que FILE_FLAG_OPEN_NO_RECALL); va en OBJECT_ATTRIBUTES.
        Assert.Equal(1, S.SecurityIdentification);
        Assert.Equal(0, S.SecurityStaticTracking);
        Assert.Equal(12, System.Runtime.InteropServices.Marshal.SizeOf<S.SecurityQualityOfService>());
    }

    // ============================================================ Hilos dedicados y token

    [Fact]
    public async Task Reader_threads_are_dedicated_and_do_not_inherit_the_execution_context_of_the_caller()
    {
        var flowing = new AsyncLocal<string?> { Value = "contexto del hilo RPC" };
        var done = new TaskCompletionSource<(bool Pool, string? Name, string? Flowed)>(TaskCreationOptions.RunContinuationsAsynchronously);

        SourceReaderThread.Start("Backbone prueba", SourceReadToken.Unprivileged, () =>
            done.SetResult((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name, flowing.Value)));
        var (pool, name, flowed) = await done.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(pool);
        Assert.Equal("Backbone prueba", name);
        Assert.Null(flowed); // UnsafeStart: ni suplantación ni AsyncLocal del que lo pide
        Assert.Equal("contexto del hilo RPC", flowing.Value);
    }

    [Fact]
    public void Without_a_prepared_copy_the_threads_read_with_the_process_token()
    {
        Assert.False(SourceReadToken.Unprivileged.HasBackupPrivilege);
        using var applied = SourceReadToken.Unprivileged.ApplyToCurrentThread();
        SourceReadToken.Unprivileged.Dispose(); // la instancia compartida no se cierra
        Assert.False(SourceReadToken.Unprivileged.HasBackupPrivilege);
    }

    [Fact]
    public void The_five_dangerous_privileges_are_removed_from_the_copy()
    {
        Assert.Equal(
            ["SeRestorePrivilege", "SeTakeOwnershipPrivilege", "SeSecurityPrivilege", "SeDebugPrivilege", "SeImpersonatePrivilege"],
            SourceReadToken.RemovedPrivileges);
        Assert.Equal("SeBackupPrivilege", SourceReadToken.BackupPrivilege);
    }

    /// <summary>
    /// Sin elevación (sin <c>SeBackupPrivilege</c> en el token) la preparación falla con un aviso y nunca lanza. Elevado, el
    /// hilo dedicado lleva la copia: <c>SeBackupPrivilege</c> activado y ninguno de los cinco quitados; el token del proceso
    /// no cambia.
    /// </summary>
    [Fact]
    public async Task The_read_token_is_a_copy_with_only_backup_enabled_or_a_logged_fallback()
    {
        var logs = new CapturingLoggerProvider();
        using var token = SourceReadToken.Create(new LoggerFactory([logs]).CreateLogger("prueba"));
        if (!token.HasBackupPrivilege)
        {
            Assert.Contains(logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("SeBackupPrivilege", StringComparison.Ordinal));
            return;
        }

        var done = new TaskCompletionSource<(bool Verified, string Problem)>(TaskCreationOptions.RunContinuationsAsynchronously);
        SourceReaderThread.Start("Backbone prueba token", token, () =>
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var verified = SourceReadToken.Verify(identity.AccessToken, out var problem);
            done.SetResult((verified, problem));
        });
        var (threadVerified, threadProblem) = await done.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(threadVerified, threadProblem);

        // El token del proceso no cambia: SeBackupPrivilege sigue sin activar en él.
        using var processIdentity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        Assert.False(SourceReadToken.Verify(processIdentity.AccessToken, out _));
    }

    // ============================================================ Contador único con la validación (HU-14, M-1)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_four_abandoned_threads_validations_and_the_JobRunner_revalidation_fail_without_opening_anything(bool limited)
    {
        var abandoned = new AbandonedSourceThreads();
        for (var i = 0; i < AbandonedSourceThreads.Limit; i++)
        {
            abandoned.OnAbandoned("x");
        }

        using var runner = new SourceValidationRunner(TimeProvider.System, new SourceIoOptions(), SourceReadToken.Unprivileged, abandoned);
        var ran = false;

        var ex = await Assert.ThrowsAsync<SourceReadingStoppedException>(() => runner.RunAsync(_ => ran = true, limited, Ct));

        Assert.False(ran);
        Assert.IsNotAssignableFrom<IOException>(ex);
        Assert.Equal(new SourceIoOptions().MaxConcurrentValidations, runner.AvailableSlots);
    }

    [Fact]
    public async Task A_validation_thread_abandoned_by_a_hung_driver_counts_in_the_same_counter_as_source_reading()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
        var events = new FakeEventLogWriter();
        var abandoned = new AbandonedSourceThreads(events);
        using var fileSystem = new FakeLocalFileSystemInspector().AddFolder(@"C:\Colgada").AddFolder(@"C:\ProgramData");
        fileSystem.Hanging[@"C:\Colgada"] = true;
        fileSystem.IgnoreCancellation = true;
        using var runner = new SourceValidationRunner(time, new SourceIoOptions(), () => new RecordingCanceller(), SourceReadToken.Unprivileged, abandoned);
        var validator = TestJobValidators.Create(fileSystem, @"C:\ProgramData\Backbone", runner: runner, time: time);
        var job = JobDefaults.NewJob() with
        {
            Name = "Prueba",
            Sources = [new JobSource { Alias = "O", Path = @"C:\Colgada" }],
            Destination = @"\\nas\backups",
        };

        var validation = validator.ValidateAsync(job, [], Ct);
        Assert.True(await fileSystem.HangEntered.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        for (var i = 0; i < 200 && !validation.IsCompleted; i++)
        {
            await Task.Delay(10, CancellationToken.None);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var result = await validation;
        Assert.Equal(ValidationIssueCode.SourceUnreadable, Assert.Single(result.Issues.Errors).Code);
        Assert.Equal(1, abandoned.Count);

        // El mismo contador detiene la lectura de orígenes de las ejecuciones (SourceFileSystemFactory).
        for (var i = 1; i < AbandonedSourceThreads.Limit; i++)
        {
            abandoned.OnAbandoned("y");
        }

        Assert.True(abandoned.IsExhausted);
        Assert.Single(events.Entries, e => e.EventId == BackboneEventIds.SourceReadingStopped);
        await Assert.ThrowsAsync<SourceReadingStoppedException>(() => validator.ValidateAsync(job, [], Ct));

        fileSystem.ReleaseHung();
        for (var i = 0; i < 500 && abandoned.Count == AbandonedSourceThreads.Limit; i++)
        {
            await Task.Delay(10, CancellationToken.None);
        }

        Assert.Equal(AbandonedSourceThreads.Limit - 1, abandoned.Count);
    }
}
