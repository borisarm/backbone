using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Backbone.Service.Tests.Persistence;

/// <summary>
/// §8.2 "Copias apartadas <c>.bad</c>" (revisión de seguridad de HU-07, BAJA-3), comunes a <c>settings.json</c> y
/// <c>jobs.json</c>: nombre con marca UTC de 100 ns, renombrado por handle sin reemplazar, como mucho 5 copias borrando la más
/// antigua validada antes, y un <c>.bad</c> sin marca que no se toca ni cuenta.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class SetAsideCopiesTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 12, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly FakeTimeProvider _time = new(Start.AddTicks(3_456_789));

    public SetAsideCopiesTests() => Directory.CreateDirectory(_temp.Combine("config"));

    public void Dispose() => _temp.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string JobsPath => _temp.Combine("config", "jobs.json");

    private string SettingsPath => _temp.Combine("config", "settings.json");

    [Fact]
    public void The_copy_name_has_the_UTC_stamp_with_100_ns_precision()
    {
        File.WriteAllText(JobsPath, "{ roto");

        var copy = TestFileTrust.CurrentUser.SetAsideCorrupt(JobsPath, _time.GetUtcNow());

        Assert.Equal(_temp.Combine("config", "jobs.json.bad-20261121T0930123456789Z"), copy);
        Assert.Equal("{ roto", File.ReadAllText(copy!));
        Assert.False(File.Exists(JobsPath));
    }

    [Fact]
    public void A_missing_file_sets_nothing_aside()
    {
        Assert.Null(TestFileTrust.CurrentUser.SetAsideCorrupt(JobsPath, _time.GetUtcNow()));
    }

    [Fact]
    public void An_existing_copy_with_the_same_name_is_never_overwritten()
    {
        File.WriteAllText(JobsPath, "nuevo");
        var existing = _temp.Combine("config", "jobs.json.bad-20261121T0930123456789Z");
        File.WriteAllText(existing, "anterior");

        Assert.ThrowsAny<IOException>(() => TestFileTrust.CurrentUser.SetAsideCorrupt(JobsPath, _time.GetUtcNow()));

        Assert.Equal("anterior", File.ReadAllText(existing));
        Assert.Equal("nuevo", File.ReadAllText(JobsPath));
    }

    [Fact]
    public void At_most_five_copies_are_kept_and_the_oldest_is_deleted()
    {
        for (var i = 0; i < 7; i++)
        {
            File.WriteAllText(JobsPath, $"corrupto {i}");
            TestFileTrust.CurrentUser.SetAsideCorrupt(JobsPath, _time.GetUtcNow());
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var copies = BadCopies.Of(JobsPath);
        Assert.Equal(5, copies.Count);
        Assert.Equal(["corrupto 2", "corrupto 3", "corrupto 4", "corrupto 5", "corrupto 6"], copies.Select(File.ReadAllText));
    }

    [Fact]
    public void An_unstamped_bad_file_from_an_earlier_version_is_not_touched_and_does_not_count()
    {
        File.WriteAllText(JobsPath + ".bad", "antiguo");
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(JobsPath, $"corrupto {i}");
            TestFileTrust.CurrentUser.SetAsideCorrupt(JobsPath, _time.GetUtcNow());
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal("antiguo", File.ReadAllText(JobsPath + ".bad"));
        Assert.Equal(5, BadCopies.Of(JobsPath).Count);
    }

    [Fact]
    public void An_untrusted_oldest_copy_is_not_deleted_and_nothing_is_set_aside()
    {
        // La más antigua es una carpeta (se rechaza como NotAFile): no se borra, no se aparta nada.
        var oldest = _temp.Combine("config", "jobs.json.bad-20000101T0000000000000Z");
        Directory.CreateDirectory(oldest);
        for (var i = 0; i < 4; i++)
        {
            File.WriteAllText(_temp.Combine("config", $"jobs.json.bad-2001010{i}T0000000000000Z"), "x");
        }

        File.WriteAllText(JobsPath, "corrupto");

        Assert.Throws<UntrustedFileException>(() => TestFileTrust.CurrentUser.SetAsideCorrupt(JobsPath, _time.GetUtcNow()));

        Assert.True(Directory.Exists(oldest));
        Assert.Equal("corrupto", File.ReadAllText(JobsPath));
        Assert.Equal(4, BadCopies.Of(JobsPath).Count); // archivos; la carpeta sigue aparte
    }

    [Fact]
    public async Task SettingsStore_and_JobStore_use_the_same_rules()
    {
        await File.WriteAllTextAsync(SettingsPath + ".bad-20000101T0000000000000Z", "anterior", Ct);
        await File.WriteAllTextAsync(SettingsPath, "{ roto", Ct);
        await File.WriteAllTextAsync(JobsPath, "{ roto", Ct);
        using var settings = new SettingsStore(SettingsPath, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance, null, _time);
        using var jobs = new JobStore(JobsPath, TestFileTrust.CurrentUser, NullLogger<JobStore>.Instance, JobStore.MaxFileBytes, _time);
        await settings.LoadAsync(Ct);
        await jobs.LoadAsync(Ct);

        Assert.True(jobs.LoadedFileWasCorrupt);
        await settings.AddAuthorizedUserAsync(new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001"), Ct);
        await jobs.UpdateAsync(list => (list.Add(Backbone.Contracts.JobDefaults.NewJob() with { Id = Guid.NewGuid(), Name = "Docs" }), 0), Ct);

        Assert.Equal(["anterior", "{ roto"], BadCopies.Of(SettingsPath).Select(File.ReadAllText));
        Assert.Equal("{ roto", File.ReadAllText(BadCopies.Single(JobsPath)));
    }
}
