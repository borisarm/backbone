using Backbone.Contracts;
using Backbone.Service.Ipc;

namespace Backbone.Service.Tests.Ipc;

/// <summary>RNF-08 / §7.5: validación genérica de las entradas IPC.</summary>
[Trait("Story", "HT-03")]
public sealed class IpcInputValidatorTests
{
    [Fact]
    public void A_valid_default_job_passes()
    {
        var job = JobDefaults.NewJob() with
        {
            Name = new string('n', ContractLimits.MaxJobNameLength),
            Sources = [new JobSource { Alias = "Docs", Path = @"C:\Users\ana\Documents" }],
            Schedule = new JobSchedule { Kind = ScheduleKind.Daily, Days = DaysOfWeek.All, TimesOfDay = [new TimeOnly(13, 0)] },
        };

        Assert.Same(job, IpcInputValidator.Job(job));
    }

    public static TheoryData<JobDefinition, string, ValidationIssueCode> InvalidJobs => new()
    {
        { new JobDefinition { Name = new string('n', ContractLimits.MaxJobNameLength + 1) }, "Name", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Destination = new string('d', ContractLimits.MaxDestinationLength + 1) }, "Destination", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Sources = [.. Enumerable.Repeat(new JobSource(), ContractLimits.MaxSources + 1)] }, "Sources", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Sources = [new JobSource(), null!] }, "Sources[1]", ValidationIssueCode.ValueOutOfRange },
        { new JobDefinition { Sources = [new JobSource { Alias = new string('a', ContractLimits.MaxAliasLength + 1) }] }, "Sources[0].Alias", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Sources = [new JobSource { Path = new string('p', ContractLimits.MaxLocalPathLength + 1) }] }, "Sources[0].Path", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Exclusions = [.. Enumerable.Repeat("*.tmp", ContractLimits.MaxExclusions + 1)] }, "Exclusions", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Exclusions = ["*.tmp", new string('e', ContractLimits.MaxExclusionPatternLength + 1)] }, "Exclusions[1]", ValidationIssueCode.LimitExceeded },
        { new JobDefinition { Exclusions = [null!] }, "Exclusions[0]", ValidationIssueCode.ValueOutOfRange },
        { new JobDefinition { Schedule = new JobSchedule { Kind = (ScheduleKind)3 } }, "Schedule.Kind", ValidationIssueCode.ValueOutOfRange },
        { new JobDefinition { Schedule = new JobSchedule { Kind = (ScheduleKind)(-1) } }, "Schedule.Kind", ValidationIssueCode.ValueOutOfRange },
        { new JobDefinition { Schedule = new JobSchedule { Days = DaysOfWeek.All | (DaysOfWeek)128 } }, "Schedule.Days", ValidationIssueCode.ValueOutOfRange },
        { new JobDefinition { Schedule = new JobSchedule { TimesOfDay = [.. Enumerable.Repeat(TimeOnly.MinValue, ContractLimits.MaxTimesOfDay + 1)] } }, "Schedule.TimesOfDay", ValidationIssueCode.LimitExceeded },
    };

    [Theory]
    [MemberData(nameof(InvalidJobs))]
    public void Invalid_jobs_are_rejected_with_the_field_and_code(JobDefinition job, string field, ValidationIssueCode code)
    {
        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Job(job));

        Assert.Equal(BackboneErrorCode.ValidationFailed, ex.Code);
        Assert.Contains(ex.Validation!.Errors, e => e.Field == field && e.Code == code);
    }

    [Fact]
    public void All_errors_are_reported_at_once()
    {
        var job = new JobDefinition
        {
            Name = new string('n', 500),
            Schedule = new JobSchedule { Kind = (ScheduleKind)9, Days = (DaysOfWeek)1024 },
        };

        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Job(job));

        Assert.Equal(["Name", "Schedule.Kind", "Schedule.Days"], ex.Validation!.Errors.Select(e => e.Field));
        Assert.Equal("9", ex.Validation.Errors[1].Value);
    }

    [Fact]
    public void Null_inputs_are_rejected()
    {
        Assert.Equal(BackboneErrorCode.ValidationFailed, Assert.Throws<BackboneException>(() => IpcInputValidator.Job(null)).Code);
        Assert.Throws<BackboneException>(() => IpcInputValidator.Pause(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.History(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Restore(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Settings(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Account(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Destination(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Credential(null));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Diagnostics(null));
        Assert.Null(IpcInputValidator.OptionalCredential(null));
    }

    [Fact]
    public void Input_dates_are_normalized_to_UTC()
    {
        var local = new DateTimeOffset(2026, 11, 15, 13, 0, 0, TimeSpan.FromHours(1));

        var pause = IpcInputValidator.Pause(new PauseRequest { Paused = true, UntilUtc = local });
        var query = IpcInputValidator.History(new HistoryQuery { FromUtc = local, ToUtc = local.AddDays(1) });

        Assert.Equal(TimeSpan.Zero, pause.UntilUtc!.Value.Offset);
        Assert.Equal(local, pause.UntilUtc.Value);
        Assert.Equal(TimeSpan.Zero, query.FromUtc!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, query.ToUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(-1, 100, "Offset", ValidationIssueCode.ValueOutOfRange)]
    [InlineData(0, 0, "Limit", ValidationIssueCode.ValueOutOfRange)]
    [InlineData(0, ContractLimits.MaxPageSize + 1, "Limit", ValidationIssueCode.LimitExceeded)]
    public void History_paging_is_checked(int offset, int limit, string field, ValidationIssueCode code)
    {
        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.History(new HistoryQuery { Offset = offset, Limit = limit }));

        Assert.Contains(ex.Validation!.Errors, e => e.Field == field && e.Code == code);
    }

    [Fact]
    public void History_rejects_From_after_To_comparing_instants_not_local_times()
    {
        var from = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.FromHours(2)); // 10:00 UTC
        var to = new DateTimeOffset(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);            // 11:00 UTC

        Assert.NotNull(IpcInputValidator.History(new HistoryQuery { FromUtc = from, ToUtc = to }));
        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.History(new HistoryQuery { FromUtc = to, ToUtc = from }));
        Assert.Contains(ex.Validation!.Errors, e => e.Field == "FromUtc" && e.Code == ValidationIssueCode.ValueOutOfRange);
    }

    [Fact]
    public void History_rejects_undefined_states()
    {
        var ex = Assert.Throws<BackboneException>(
            () => IpcInputValidator.History(new HistoryQuery { States = [RunState.Failed, (RunState)77] }));

        Assert.Contains(ex.Validation!.Errors, e => e.Field == "States[1]" && e.Code == ValidationIssueCode.ValueOutOfRange);
    }

    [Theory]
    [InlineData("20261115T100000Z-a1b2", "", 0, 1)]
    [InlineData("20261115T100000Z-a1b2", "Docs/Facturas", 10, 1000)]
    public void Valid_browse_requests_pass(string snapshotId, string path, int offset, int limit)
    {
        IpcInputValidator.Browse(snapshotId, path, offset, limit);
    }

    [Theory]
    [InlineData("no-es-un-id", "", 0, 10, "snapshotId", ValidationIssueCode.SnapshotPathInvalid)]
    [InlineData("20261115T100000Z-a1b2", "/Docs", 0, 10, "path", ValidationIssueCode.SnapshotPathInvalid)]
    [InlineData("20261115T100000Z-a1b2", "Docs/..", 0, 10, "path", ValidationIssueCode.SnapshotPathInvalid)]
    [InlineData("20261115T100000Z-a1b2", "Docs\\a", 0, 10, "path", ValidationIssueCode.SnapshotPathInvalid)]
    [InlineData("20261115T100000Z-a1b2", null, 0, 10, "path", ValidationIssueCode.SnapshotPathInvalid)]
    [InlineData("20261115T100000Z-a1b2", "", -1, 10, "offset", ValidationIssueCode.ValueOutOfRange)]
    [InlineData("20261115T100000Z-a1b2", "", 0, 1001, "limit", ValidationIssueCode.LimitExceeded)]
    public void Invalid_browse_requests_are_rejected(string snapshotId, string? path, int offset, int limit, string field, ValidationIssueCode code)
    {
        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Browse(snapshotId, path, offset, limit));

        Assert.Contains(ex.Validation!.Errors, e => e.Field == field && e.Code == code);
    }

    [Fact]
    public void Too_long_snapshot_paths_are_rejected_with_LimitExceeded()
    {
        var path = string.Join('/', Enumerable.Repeat(new string('a', 200), 200)); // > 32 767

        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.SnapshotFilePath(path));

        Assert.Equal(ValidationIssueCode.LimitExceeded, Assert.Single(ex.Validation!.Errors).Code);
    }

    [Theory]
    [InlineData(null, 10, "text", ValidationIssueCode.ValueOutOfRange)]
    [InlineData("", 10, "text", ValidationIssueCode.ValueOutOfRange)]
    [InlineData("factura", 0, "limit", ValidationIssueCode.ValueOutOfRange)]
    [InlineData("factura", 1001, "limit", ValidationIssueCode.LimitExceeded)]
    public void Invalid_searches_are_rejected(string? text, int limit, string field, ValidationIssueCode code)
    {
        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Search("20261115T100000Z-a1b2", text, limit));

        Assert.Contains(ex.Validation!.Errors, e => e.Field == field && e.Code == code);
    }

    [Fact]
    public void Search_text_is_limited_to_256_characters()
    {
        IpcInputValidator.Search("20261115T100000Z-a1b2", new string('a', ContractLimits.MaxSearchTextLength), 10);

        var ex = Assert.Throws<BackboneException>(
            () => IpcInputValidator.Search("20261115T100000Z-a1b2", new string('a', ContractLimits.MaxSearchTextLength + 1), 10));
        Assert.Contains(ex.Validation!.Errors, e => e.Field == "text" && e.Code == ValidationIssueCode.LimitExceeded);
    }

    public static TheoryData<RestoreRequest, string, ValidationIssueCode> InvalidRestores => new()
    {
        { new RestoreRequest { SnapshotId = "x", Paths = ["Docs"] }, "SnapshotId", ValidationIssueCode.SnapshotPathInvalid },
        { new RestoreRequest { SnapshotId = "20261115T100000Z-a1b2", Paths = [] }, "Paths", ValidationIssueCode.ValueOutOfRange },
        { new RestoreRequest { SnapshotId = "20261115T100000Z-a1b2", Paths = [.. Enumerable.Repeat("Docs", 1001)] }, "Paths", ValidationIssueCode.LimitExceeded },
        { new RestoreRequest { SnapshotId = "20261115T100000Z-a1b2", Paths = ["Docs", "../x"] }, "Paths[1]", ValidationIssueCode.SnapshotPathInvalid },
        { new RestoreRequest { SnapshotId = "20261115T100000Z-a1b2", Paths = [null!] }, "Paths[0]", ValidationIssueCode.SnapshotPathInvalid },
        { new RestoreRequest { SnapshotId = "20261115T100000Z-a1b2", Paths = ["Docs"], ConflictPolicy = (RestoreConflictPolicy)4 }, "ConflictPolicy", ValidationIssueCode.ValueOutOfRange },
        { new RestoreRequest { SnapshotId = "20261115T100000Z-a1b2", Paths = ["Docs"], TargetFolder = new string('t', ContractLimits.MaxLocalPathLength + 1) }, "TargetFolder", ValidationIssueCode.LimitExceeded },
    };

    [Theory]
    [MemberData(nameof(InvalidRestores))]
    public void Invalid_restore_requests_are_rejected(RestoreRequest request, string field, ValidationIssueCode code)
    {
        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Restore(request));

        Assert.Contains(ex.Validation!.Errors, e => e.Field == field && e.Code == code);
    }

    [Fact]
    public void Undefined_conflict_resolution_is_rejected()
    {
        IpcInputValidator.ConflictResolution(ConflictResolution.KeepBoth);

        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.ConflictResolution((ConflictResolution)3));
        Assert.Contains(ex.Validation!.Errors, e => e.Field == "resolution" && e.Code == ValidationIssueCode.ValueOutOfRange);
    }

    [Fact]
    public void Credentials_respect_the_account_and_password_limits()
    {
        IpcInputValidator.Credential(new CredentialInput
        {
            UserName = new string('u', ContractLimits.MaxAccountNameLength),
            Password = new string('p', ContractLimits.MaxPasswordLength),
        });

        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Credential(new CredentialInput
        {
            UserName = new string('u', ContractLimits.MaxAccountNameLength + 1),
            Password = new string('p', ContractLimits.MaxPasswordLength + 1),
        }));
        Assert.Equal(["UserName", "Password"], ex.Validation!.Errors.Select(e => e.Field));
        Assert.All(ex.Validation.Errors, e => Assert.Null(e.Value));
    }

    [Theory]
    [InlineData(-1, 3, "MissedRunGracePeriod")]
    [InlineData(24 * 60 + 1, 3, "MissedRunGracePeriod")]
    [InlineData(5, 0, "OutdatedAfterDays")]
    [InlineData(5, 366, "OutdatedAfterDays")]
    public void Settings_out_of_range_are_rejected(int graceMinutes, int outdatedDays, string field)
    {
        var settings = new ServiceSettings { MissedRunGracePeriod = TimeSpan.FromMinutes(graceMinutes), OutdatedAfterDays = outdatedDays };

        var ex = Assert.Throws<BackboneException>(() => IpcInputValidator.Settings(settings));

        Assert.Contains(ex.Validation!.Errors, e => e.Field == field && e.Code == ValidationIssueCode.ValueOutOfRange);
    }

    [Fact]
    public void Settings_at_the_limits_are_accepted()
    {
        IpcInputValidator.Settings(new ServiceSettings { MissedRunGracePeriod = TimeSpan.Zero, OutdatedAfterDays = 1 });
        IpcInputValidator.Settings(new ServiceSettings { MissedRunGracePeriod = TimeSpan.FromHours(24), OutdatedAfterDays = 365 });
    }

    [Fact]
    public void Accounts_and_destinations_have_length_limits()
    {
        IpcInputValidator.Account(new string('a', ContractLimits.MaxAccountNameLength));
        IpcInputValidator.Destination(new string('d', ContractLimits.MaxDestinationLength));

        Assert.Throws<BackboneException>(() => IpcInputValidator.Account(new string('a', ContractLimits.MaxAccountNameLength + 1)));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Destination(new string('d', ContractLimits.MaxDestinationLength + 1)));
        Assert.Throws<BackboneException>(() => IpcInputValidator.Diagnostics(new DiagnosticsExportRequest { TargetPath = new string('t', ContractLimits.MaxLocalPathLength + 1) }));
    }
}
