using System.Globalization;
using System.Text;
using System.Text.Json;
using Backbone.Core.Repository;
using Backbone.Core.Repository.Locking;
using Backbone.Core.Storage;
using Backbone.Core.Tests.Support;

namespace Backbone.Core.Tests.Repository;

/// <summary>HT-04 criterio 4: bloqueos shared/exclusive con latido y caducidad a los 15 minutos (§6.4.4).</summary>
[Trait("Story", "HT-04")]
public sealed class RepositoryLockTests
{
    private static readonly LockOwner Salon = new("PC-SALON", 1234);
    private static readonly LockOwner Estudio = new("PC-ESTUDIO", 42);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HT04_4_Shared_lock_writes_locks_host_pid_file_with_heartbeat()
    {
        var (storage, time, repository) = await CreateAsync();

        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        Assert.Contains("locks/PC-SALON-1234.lock", storage.FilePaths);
        using var json = JsonDocument.Parse(storage.GetFileContent("locks/PC-SALON-1234.lock"));
        Assert.Equal("PC-SALON", json.RootElement.GetProperty("host").GetString());
        Assert.Equal(1234, json.RootElement.GetProperty("pid").GetInt32());
        Assert.Equal("shared", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("2026-11-15T02:00:00Z", json.RootElement.GetProperty("heartbeatUtc").GetString());
        Assert.Equal(time.UtcNow, repositoryLock.LastHeartbeatUtc);
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HT04_4_Release_deletes_the_lock_file()
    {
        var (storage, _, repository) = await CreateAsync();
        var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        await repositoryLock.DisposeAsync();

        Assert.True(repositoryLock.IsReleased);
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("locks/", StringComparison.Ordinal));
        await using var again = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
    }

    [Fact]
    public async Task HT04_4_Shared_locks_from_different_processes_coexist()
    {
        var (storage, time, first) = await CreateAsync();
        var second = await BackupRepository.OpenAsync(storage, time, Ct);

        await using var a = await first.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
        await using var b = await second.AcquireLockAsync(RepositoryLockType.Shared, Estudio, Ct);

        Assert.Equal(2, (await first.ListLocksAsync(Ct)).Count);
    }

    [Fact]
    public async Task HT04_4_Exclusive_lock_is_refused_while_a_shared_lock_is_alive()
    {
        var (storage, time, backup) = await CreateAsync();
        var prune = await BackupRepository.OpenAsync(storage, time, Ct);
        await using var shared = await backup.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
        time.Advance(TimeSpan.FromMinutes(14));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => prune.AcquireLockAsync(RepositoryLockType.Exclusive, Estudio, Ct));

        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        Assert.Contains("PC-SALON", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["locks/PC-SALON-1234.lock"], storage.FilePaths.Where(p => p.StartsWith("locks/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HT04_4_Shared_lock_is_refused_while_an_exclusive_lock_is_alive()
    {
        var (storage, time, prune) = await CreateAsync();
        var backup = await BackupRepository.OpenAsync(storage, time, Ct);
        await using var exclusive = await prune.AcquireLockAsync(RepositoryLockType.Exclusive, Estudio, Ct);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => backup.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));

        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        Assert.DoesNotContain("locks/PC-SALON-1234.lock", storage.FilePaths);
    }

    [Fact]
    public async Task HT04_4_Two_exclusive_locks_exclude_each_other()
    {
        var (storage, time, first) = await CreateAsync();
        var second = await BackupRepository.OpenAsync(storage, time, Ct);
        await using var a = await first.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => second.AcquireLockAsync(RepositoryLockType.Exclusive, Estudio, Ct));
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
    }

    public static TheoryData<int, bool> Ages => new()
    {
        { 0, false },
        { 14 * 60, false },
        { (15 * 60) - 1, false },
        { 15 * 60, true },
        { 3 * 24 * 3600, true },
    };

    [Theory]
    [MemberData(nameof(Ages))]
    public async Task HT04_4_Lock_without_heartbeat_for_15_minutes_is_abandoned_and_removed_by_exclusive(int ageSeconds, bool abandoned)
    {
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-VIEJO-7.lock", "shared", time.UtcNow.AddSeconds(-ageSeconds));

        if (abandoned)
        {
            await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
            Assert.DoesNotContain("locks/PC-VIEJO-7.lock", storage.FilePaths);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct));
            Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
            Assert.Contains("locks/PC-VIEJO-7.lock", storage.FilePaths);
        }

        Assert.True(RepositoryLockPolicy.IsStale(time.UtcNow.AddSeconds(-ageSeconds), time.UtcNow.AddSeconds(-ageSeconds), time.UtcNow) == abandoned);
    }

    /// <summary>
    /// Regla de vigencia de §6.4.4 en minutos respecto a <c>ahora</c>: latido, fecha de modificación y si está abandonado.
    /// </summary>
    public static TheoryData<int, int, bool> VigencyTable => new()
    {
        // Latido no anómalo: manda el latido, la fecha de modificación no importa.
        { 0, -60, false },
        { -14, -60, false },
        { -15, 0, true },
        { -60, 0, true },
        // Latido futuro dentro de la tolerancia (ahora + 15 min incluido): vigente.
        { 1, 0, false },
        { 14, 0, false },
        { 15, 0, false },
        { 15, 60, false },
        // Latido anómalo (más de ahora + T): se usa la fecha de modificación si no es anómala.
        { 16, 0, false },
        { 16, -14, false },
        { 16, -15, true },
        { 525_600, 15, false },
        { 525_600, -60, true },
        // Latido y fecha de modificación anómalos: abandonado.
        { 16, 16, true },
        { 525_600, 525_600, true },
    };

    [Theory]
    [MemberData(nameof(VigencyTable))]
    [Trait("Requirement", "§6.4.4")]
    public void N2_Vigency_rule_with_clock_tolerance(int heartbeatMinutes, int modifiedMinutes, bool abandoned)
    {
        var now = new DateTime(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc);

        Assert.Equal(abandoned, RepositoryLockPolicy.IsStale(now.AddMinutes(heartbeatMinutes), now.AddMinutes(modifiedMinutes), now));
    }

    [Fact]
    public async Task N2_A_heartbeat_in_the_future_within_the_tolerance_is_alive()
    {
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-ADELANTADO-7.lock", "shared", time.UtcNow.AddMinutes(14));
        storage.SetLastWriteUtc("locks/PC-ADELANTADO-7.lock", time.UtcNow.AddHours(-3));

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct));

        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        Assert.Contains("locks/PC-ADELANTADO-7.lock", storage.FilePaths);
    }

    [Fact]
    public async Task N2_An_anomalous_heartbeat_uses_an_acceptable_modification_time()
    {
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-ADELANTADO-7.lock", "shared", time.UtcNow.AddDays(365));
        storage.SetLastWriteUtc("locks/PC-ADELANTADO-7.lock", time.UtcNow.AddMinutes(-1));

        // Fecha de modificación de hace 1 minuto: vigente.
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct));
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);

        // Sin escrituras nuevas, a los 15 minutos de su fecha de modificación queda abandonado y se borra.
        time.Advance(TimeSpan.FromMinutes(14));
        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
        Assert.DoesNotContain("locks/PC-ADELANTADO-7.lock", storage.FilePaths);
    }

    [Fact]
    public async Task N2_An_anomalous_heartbeat_with_an_anomalous_modification_time_is_abandoned()
    {
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-FUTURO-7.lock", "exclusive", time.UtcNow.AddDays(365));
        storage.SetLastWriteUtc("locks/PC-FUTURO-7.lock", time.UtcNow.AddDays(365));

        var info = Assert.Single(await repository.ListLocksAsync(Ct));
        Assert.True(RepositoryLockPolicy.IsStale(info, time.UtcNow));

        // No bloquea una copia (que no lo borra) y la limpieza lo borra.
        await using (var shared = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct))
        {
            Assert.Contains("locks/PC-FUTURO-7.lock", storage.FilePaths);
        }

        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
        Assert.DoesNotContain("locks/PC-FUTURO-7.lock", storage.FilePaths);
    }

    [Fact]
    public async Task N2_An_unreadable_lock_with_a_far_future_modification_time_does_not_block()
    {
        var (storage, time, repository) = await CreateAsync();
        storage.SetFileContent("locks/raro-1.lock", "no es json"u8);
        storage.SetLastWriteUtc("locks/raro-1.lock", time.UtcNow.AddDays(30));

        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);

        Assert.DoesNotContain("locks/raro-1.lock", storage.FilePaths);
    }

    [Fact]
    public async Task N5_A_lock_that_cannot_be_opened_is_unreadable_instead_of_failing()
    {
        var inner = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        await BackupRepository.InitializeAsync(inner, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        WriteForeignLock(inner, "PC-ESTUDIO-42.lock", "shared", time.UtcNow);
        WriteForeignLock(inner, "PC-COCINA-9.lock", "shared", time.UtcNow);
        inner.SetLastWriteUtc("locks/PC-ESTUDIO-42.lock", time.UtcNow);
        inner.SetLastWriteUtc("locks/PC-COCINA-9.lock", time.UtcNow);
        var storage = new FailingOpenStorage(inner, new Dictionary<string, Exception>(StringComparer.Ordinal)
        {
            ["locks/PC-ESTUDIO-42.lock"] = new UnauthorizedAccessException("denegado"),
            ["locks/PC-COCINA-9.lock"] = new IOException("en uso"),
        });
        var repository = await BackupRepository.OpenAsync(storage, time, Ct);

        var locks = await repository.ListLocksAsync(Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));

        Assert.Equal(2, locks.Count);
        Assert.All(locks, l => Assert.False(l.IsReadable));
        Assert.All(locks, l => Assert.Equal(RepositoryLockType.Exclusive, l.Type));
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
    }

    [Fact]
    public async Task HT04_4_Abandoned_exclusive_lock_does_not_block_a_backup()
    {
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-VIEJO-7.lock", "exclusive", time.UtcNow.AddMinutes(-16));

        await using var shared = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        // La copia no borra bloqueos ajenos; solo la limpieza (exclusiva) lo hace.
        Assert.Contains("locks/PC-VIEJO-7.lock", storage.FilePaths);
    }

    [Fact]
    public async Task HT04_4_Unreadable_lock_is_treated_as_exclusive_until_its_file_is_15_minutes_old()
    {
        var (storage, time, repository) = await CreateAsync();
        storage.SetFileContent("locks/raro-1.lock", "no es json"u8);
        storage.SetLastWriteUtc("locks/raro-1.lock", time.UtcNow);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        var info = Assert.Single(await repository.ListLocksAsync(Ct));
        Assert.False(info.IsReadable);

        storage.SetLastWriteUtc("locks/raro-1.lock", time.UtcNow.AddMinutes(-15));
        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
        Assert.DoesNotContain("locks/raro-1.lock", storage.FilePaths);
    }

    [Fact]
    public async Task HT04_4_Files_in_locks_without_lock_extension_are_ignored()
    {
        var (storage, _, repository) = await CreateAsync();
        storage.SetFileContent("locks/LEEME.txt", "x"u8);

        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);

        Assert.Contains("locks/LEEME.txt", storage.FilePaths);
    }

    [Fact]
    public async Task HT04_4_Leftover_lock_with_our_own_name_is_replaced()
    {
        // Un proceso anterior con el mismo PID murió sin liberar: su archivo es nuestro.
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-SALON-1234.lock", "exclusive", time.UtcNow.AddMinutes(-1));

        await using var shared = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        using var json = JsonDocument.Parse(storage.GetFileContent("locks/PC-SALON-1234.lock"));
        Assert.Equal("shared", json.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task HT04_4_Heartbeat_is_renewed_every_60_seconds()
    {
        var (storage, time, repository) = await CreateAsync();
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        for (var minute = 1; minute <= 20; minute++)
        {
            await WaitUntilAsync(() => time.ActiveTimerCount == 1);
            time.Advance(RepositoryLockPolicy.HeartbeatInterval);
            var expected = time.UtcNow;
            await WaitUntilAsync(() => repositoryLock.LastHeartbeatUtc == expected);
        }

        using var json = JsonDocument.Parse(storage.GetFileContent("locks/PC-SALON-1234.lock"));
        Assert.Equal("2026-11-15T02:20:00Z", json.RootElement.GetProperty("heartbeatUtc").GetString());

        // Con latido al día, el bloqueo sigue vigente para los demás aunque hayan pasado 20 minutos.
        var other = await BackupRepository.OpenAsync(storage, time, Ct);
        var ex = await Assert.ThrowsAsync<RepositoryException>(() => other.AcquireLockAsync(RepositoryLockType.Exclusive, Estudio, Ct));
        Assert.Equal(RepositoryErrorKind.Locked, ex.Kind);
        repositoryLock.ThrowIfLost();
    }

    [Fact]
    public async Task HT04_4_Lock_is_lost_when_its_file_was_removed_by_another_process()
    {
        var (storage, _, repository) = await CreateAsync();
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
        await storage.DeleteFileAsync("locks/PC-SALON-1234.lock", Ct);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repositoryLock.EnsureHeldAsync(Ct));

        Assert.Equal(RepositoryErrorKind.LockLost, ex.Kind);
        Assert.True(repositoryLock.IsLost);
        Assert.DoesNotContain("locks/PC-SALON-1234.lock", storage.FilePaths);
    }

    [Fact]
    public async Task HT04_4_Lock_is_lost_when_heartbeats_fail_for_too_long()
    {
        var inner = new InMemoryRepositoryStorage();
        var faults = new FaultInjectingStorage(inner);
        var time = new ManualTimeProvider();
        var repository = await BackupRepository.InitializeAsync(faults, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        // La red se corta: todos los latidos fallan.
        faults.Mode = FaultMode.Crash;
        faults.FailAt = faults.OperationCount + 1;
        for (var minute = 1; minute <= 12; minute++)
        {
            await WaitUntilAsync(() => time.ActiveTimerCount == 1);
            time.Advance(RepositoryLockPolicy.HeartbeatInterval);
            await WaitUntilAsync(() => repositoryLock.LastHeartbeatError is not null);
        }

        // Tras 12 minutos sin latido aún se considera vigente; a los 13 el titular se rinde,
        // antes de que otros lo den por abandonado (15).
        repositoryLock.ThrowIfLost();
        time.Advance(TimeSpan.FromMinutes(1));
        var ex = Assert.Throws<RepositoryException>(repositoryLock.ThrowIfLost);
        Assert.Equal(RepositoryErrorKind.LockLost, ex.Kind);
        Assert.True(RepositoryLockPolicy.OwnerGivesUpAfter < RepositoryLockPolicy.StaleAfter);
    }

    [Fact]
    public async Task HT04_4_Lock_is_lost_after_a_long_suspension()
    {
        var (_, time, repository) = await CreateAsync();
        await using var repositoryLock = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
        await WaitUntilAsync(() => time.ActiveTimerCount == 1);

        // El equipo se suspende 20 minutos: el siguiente latido descubre el hueco y no reescribe el bloqueo.
        time.Jump(TimeSpan.FromMinutes(20));
        await WaitUntilAsync(() => repositoryLock.IsLost);

        var ex = await Assert.ThrowsAsync<RepositoryException>(() => repositoryLock.EnsureHeldAsync(Ct));
        Assert.Equal(RepositoryErrorKind.LockLost, ex.Kind);
        Assert.Equal(new DateTime(2026, 11, 15, 2, 0, 0, DateTimeKind.Utc), repositoryLock.LastHeartbeatUtc);
    }

    [Fact]
    public async Task HT04_4_One_instance_holds_one_lock_at_a_time()
    {
        var (_, _, repository) = await CreateAsync();
        var first = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));

        await first.ReleaseAsync(Ct);
        await first.ReleaseAsync(Ct); // idempotente
        await using var second = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
    }

    [Fact]
    public async Task HT04_4_Failed_acquisition_removes_its_own_lock_file_and_frees_the_instance()
    {
        var (storage, time, repository) = await CreateAsync();
        WriteForeignLock(storage, "PC-ESTUDIO-42.lock", "exclusive", time.UtcNow);

        await Assert.ThrowsAsync<RepositoryException>(() => repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct));
        await storage.DeleteFileAsync("locks/PC-ESTUDIO-42.lock", Ct);

        await using var retry = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct);
        Assert.Equal(["locks/PC-SALON-1234.lock"], storage.FilePaths.Where(p => p.StartsWith("locks/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HT04_4_Clean_temporary_files_requires_the_exclusive_lock()
    {
        var (storage, _, repository) = await CreateAsync();
        storage.SetFileContent("tmp/a.tmp", "x"u8);
        storage.SetFileContent("tmp/b.tmp", "y"u8);

        await using (var shared = await repository.AcquireLockAsync(RepositoryLockType.Shared, Salon, Ct))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CleanTemporaryFilesAsync(shared, Ct));
        }

        await using var exclusive = await repository.AcquireLockAsync(RepositoryLockType.Exclusive, Salon, Ct);
        Assert.Equal(2, await repository.CleanTemporaryFilesAsync(exclusive, Ct));
        Assert.DoesNotContain(storage.FilePaths, p => p.StartsWith("tmp/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("PC/SALON")]
    [InlineData("PC\\SALON")]
    [InlineData("..")]
    [InlineData("C:")]
    public void HT04_4_Lock_owner_rejects_host_names_that_are_not_file_names(string host)
    {
        Assert.ThrowsAny<ArgumentException>(() => new LockOwner(host, 1));
    }

    [Fact]
    public void HT04_4_Current_owner_uses_machine_name_and_process_id()
    {
        var owner = LockOwner.Current;

        Assert.Equal(Environment.ProcessId, owner.ProcessId);
        Assert.Equal($"{Environment.MachineName}-{Environment.ProcessId}.lock", owner.FileName);
    }

    private static async Task<(InMemoryRepositoryStorage Storage, ManualTimeProvider Time, BackupRepository Repository)> CreateAsync()
    {
        var storage = new InMemoryRepositoryStorage();
        var time = new ManualTimeProvider();
        var repository = await BackupRepository.InitializeAsync(storage, new RepositoryInitializationOptions { CreatedBy = "x" }, time, Ct);
        return (storage, time, repository);
    }

    private static void WriteForeignLock(InMemoryRepositoryStorage storage, string name, string type, DateTime heartbeatUtc)
    {
        var host = name[..name.LastIndexOf('-')];
        var json = $$"""{ "host": "{{host}}", "pid": 7, "type": "{{type}}", "heartbeatUtc": "{{heartbeatUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}}" }""";
        storage.SetFileContent(RepositoryPath.Combine("locks", name), Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Almacenamiento en el que abrir ciertos archivos falla con la excepción indicada.</summary>
    private sealed class FailingOpenStorage(IRepositoryStorage inner, IReadOnlyDictionary<string, Exception> failures) : IRepositoryStorage
    {
        public Task<Stream> OpenReadAsync(string path, CancellationToken cancellationToken) =>
            failures.TryGetValue(path, out var failure) ? Task.FromException<Stream>(failure) : inner.OpenReadAsync(path, cancellationToken);

        public Task<Stream> CreateNewAsync(string path, CancellationToken cancellationToken) => inner.CreateNewAsync(path, cancellationToken);

        public Task<bool> TryMoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            inner.TryMoveAsync(sourcePath, destinationPath, overwrite, cancellationToken);

        public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => inner.DeleteFileAsync(path, cancellationToken);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => inner.DirectoryExistsAsync(path, cancellationToken);

        public Task<StorageEntry?> GetFileInfoAsync(string path, CancellationToken cancellationToken) => inner.GetFileInfoAsync(path, cancellationToken);

        public IAsyncEnumerable<StorageEntry> EnumerateAsync(string directoryPath, CancellationToken cancellationToken) =>
            inner.EnumerateAsync(directoryPath, cancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("La condición no se cumplió a tiempo.");
            }

            await Task.Delay(5, Ct);
        }
    }
}
