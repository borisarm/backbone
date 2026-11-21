using Backbone.Contracts;
using Backbone.Service.Hosting;
using Backbone.Service.Jobs;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Jobs;

/// <summary>
/// HU-07 criterios 2, 3, 4 y 6 (RF-03, §7.5, §8.3): reglas de un trabajo en el servicio, cada error con su campo.
/// Las carpetas locales se simulan con <see cref="FakeLocalFileSystemInspector"/>.
/// </summary>
[Trait("Story", "HU-07")]
public sealed class JobValidatorTests
{
    private const string Documents = @"C:\Users\ana\Documentos";
    private const string Pictures = @"C:\Users\ana\Imágenes";

    private readonly FakeLocalFileSystemInspector _fileSystem = new FakeLocalFileSystemInspector()
        .AddFolder(Documents)
        .AddFolder(Pictures)
        .AddFolder(@"C:\")
        .AddFolder(@"C:\ProgramData")
        .AddFolder(@"C:\ProgramData\Backbone")
        .AddFolder(@"C:\ProgramData\Backbone\credentials")
        .AddFolder(@"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef");

    private readonly FakeHostResolver _hosts = new();

    private JobValidator Validator => TestJobValidators.Create(_fileSystem, @"C:\ProgramData\Backbone", _hosts);

    private static JobDefinition ValidJob(params JobSource[] sources) =>
        JobDefaults.NewJob() with
        {
            Name = "Documentos",
            Sources = sources.Length > 0 ? sources : [new JobSource { Alias = "Documentos", Path = Documents }],
            Destination = @"\\nas\backups\pc-salon",
        };

    [Fact]
    public async Task A_valid_job_has_no_errors_or_warnings()
    {
        var result = await Validator.CheckAsync(ValidJob(), []);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task The_name_is_required(string name)
    {
        await AssertSingleErrorAsync(ValidJob() with { Name = name }, "Name", ValidationIssueCode.NameRequired);
    }

    [Fact]
    public async Task A_name_with_control_characters_or_lone_surrogates_is_rejected()
    {
        // Las suplentes sueltas no se pueden escribir en un atributo (los textos de los atributos se guardan en UTF-8).
        foreach (var name in new[] { "Docs\u0007", "Docs\ud800", "\udc00Docs" })
        {
            await AssertSingleErrorAsync(ValidJob() with { Name = name }, "Name", ValidationIssueCode.ValueOutOfRange);
        }

        Assert.True((await Validator.CheckAsync(ValidJob() with { Name = "Fotos 📷" }, [])).IsValid);
    }

    [Fact]
    public async Task At_least_one_source_is_required()
    {
        await AssertSingleErrorAsync(ValidJob() with { Sources = [] }, "Sources", ValidationIssueCode.SourcesRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task The_alias_is_required(string alias)
    {
        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = alias, Path = Documents }), "Sources[0].Alias", ValidationIssueCode.AliasRequired);
    }

    [Theory]
    [InlineData("Docs/2024")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("..")]
    [InlineData("Docs.")]
    [InlineData("Docs ")]
    public async Task An_alias_that_is_not_a_valid_folder_name_is_rejected(string alias)
    {
        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = alias, Path = Documents }), "Sources[0].Alias", ValidationIssueCode.AliasInvalid);
    }

    [Fact]
    public async Task Two_equal_aliases_ignoring_case_mark_the_repeated_one_with_its_value()
    {
        var job = ValidJob(
            new JobSource { Alias = "Fotos", Path = Documents },
            new JobSource { Alias = "Imágenes", Path = Pictures },
            new JobSource { Alias = "FOTOS", Path = Pictures });

        var error = await AssertSingleErrorAsync(job, "Sources[2].Alias", ValidationIssueCode.AliasDuplicate);
        Assert.Equal("FOTOS", error.Value);
    }

    [Fact]
    public async Task A_missing_folder_is_rejected_on_its_path()
    {
        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Otra", Path = @"C:\no\existe" });

        await AssertSingleErrorAsync(job, "Sources[1].Path", ValidationIssueCode.SourceNotFound);
    }

    [Theory]
    [InlineData((int)LocalFolderStatus.NotFound, (int)ValidationIssueCode.SourceNotFound)]
    [InlineData((int)LocalFolderStatus.Unreadable, (int)ValidationIssueCode.SourceUnreadable)]
    [InlineData((int)LocalFolderStatus.NotLocal, (int)ValidationIssueCode.SourceNotLocal)]
    public async Task Each_resolution_result_has_its_own_code(int status, int code)
    {
        // BAJA-1 (contrato 1.3): acceso denegado, plazo vencido o componentes que no se atraviesan dan SourceUnreadable.
        _fileSystem.AddStatus(@"C:\x", (LocalFolderStatus)status);

        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "X", Path = @"C:\x" }), "Sources[0].Path", (ValidationIssueCode)code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Documentos")]
    [InlineData(@"relativa\carpeta")]
    [InlineData("C:docs")]
    [InlineData("C:")]
    public async Task An_empty_or_relative_path_is_SourcePathInvalid(string path)
    {
        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourcePathInvalid);
    }

    [Theory]
    [InlineData(@"\\nas\fotos")]
    [InlineData(@"\\?\C:\Users")]
    [InlineData(@"\\.\C:\Users")]
    public async Task A_UNC_or_device_path_is_not_local(string path)
    {
        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Fact]
    public async Task A_mapped_network_drive_is_not_local()
    {
        _fileSystem.AddStatus(@"Z:\Fotos", LocalFolderStatus.NotLocal);

        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "Fotos", Path = @"Z:\Fotos" }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Theory]
    [InlineData((int)FakeVolumeKind.Remote)]
    [InlineData((int)FakeVolumeKind.Removable)]
    [InlineData((int)FakeVolumeKind.Other)]
    public async Task A_folder_whose_final_path_is_not_on_a_fixed_disk_is_not_local(int volume)
    {
        _fileSystem.AddFolder(@"C:\Users\ana\Enlace", @"E:\Fotos", volume: (FakeVolumeKind)volume);

        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "Fotos", Path = @"C:\Users\ana\Enlace" }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    [InlineData("ReFS")]
    [InlineData("")]
    public async Task A_folder_on_a_volume_that_is_not_NTFS_is_rejected(string fileSystem)
    {
        _fileSystem.AddFolder(@"D:\Fotos", fileSystem: fileSystem);

        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "Fotos", Path = @"D:\Fotos" }), "Sources[0].Path", ValidationIssueCode.SourceNotNtfs);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Backbone", @"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone\credentials", @"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef", @"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef")]
    public async Task The_data_folder_a_folder_inside_it_or_a_quarantined_folder_is_SourceNotAllowed(string path, string protectedPath)
    {
        var error = await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourceNotAllowed);
        Assert.Equal(protectedPath, error.Value);
    }

    [Fact]
    public async Task A_junction_to_the_data_folder_is_SourceNotAllowed_comparing_final_paths()
    {
        _fileSystem.AddFolder(@"C:\Users\ana\Atajo", finalPath: @"C:\ProgramData\Backbone\credentials");

        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Atajo", Path = @"C:\Users\ana\Atajo" });

        await AssertSingleErrorAsync(job, "Sources[1].Path", ValidationIssueCode.SourceNotAllowed);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\ProgramData")]
    public async Task A_source_containing_the_data_folder_is_saved_with_the_SourceContainsDataFolder_warning(string path)
    {
        var result = await Validator.CheckAsync(ValidJob(new JobSource { Alias = "Disco", Path = path }), []);

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Sources[0].Path", warning.Field);
        Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, warning.Code);
        Assert.Equal(@"C:\ProgramData\Backbone", warning.Value);
    }

    [Fact]
    public async Task A_source_containing_ProgramData_fails_closed_with_more_than_100_quarantined_folders()
    {
        _fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, 101).Select(i => $"Backbone.untrusted-{i}"));

        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Disco", Path = @"C:\" });

        var error = await AssertSingleErrorAsync(job, "Sources[1].Path", ValidationIssueCode.SourceNotAllowed);
        Assert.Equal(@"C:\ProgramData", error.Value);
    }

    [Fact]
    public async Task The_protected_folders_are_computed_once_per_validation()
    {
        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Imágenes", Path = Pictures });

        await Validator.CheckAsync(job, []);

        Assert.Equal(1, _fileSystem.EnumerationCount);
    }

    [Theory]
    [InlineData("", ValidationIssueCode.DestinationRequired)]
    [InlineData(@"C:\copias", ValidationIssueCode.DestinationNotUnc)]
    [InlineData(@"\\nas", ValidationIssueCode.DestinationNotUnc)]
    [InlineData("//nas/backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\?\UNC\nas\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\localhost\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\127.0.0.1\backups", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\C$", ValidationIssueCode.DestinationNotAllowed)]
    [InlineData(@"\\nas\backups\con", ValidationIssueCode.DestinationNotAllowed)]
    public async Task A_destination_that_is_not_an_allowed_UNC_path_is_rejected(string destination, ValidationIssueCode code)
    {
        await AssertSingleErrorAsync(ValidJob() with { Destination = destination }, "Destination", code);
    }

    [Fact]
    public async Task This_machine_is_not_an_allowed_destination()
    {
        await AssertSingleErrorAsync(ValidJob() with { Destination = $@"\\{Environment.MachineName}\copias" }, "Destination", ValidationIssueCode.DestinationNotAllowed);
    }

    [Fact]
    public async Task A_source_that_leads_to_the_network_is_only_SourceNotLocal()
    {
        // Antes de ADR-20 el enlace se seguía y su ruta final daba además DestinationInsideSource. Ahora el destino del enlace
        // no se abre: no hay ruta final con la que comparar el destino.
        _fileSystem.AddFolder(@"C:\Users\ana\Red", finalPath: @"\\nas\backups", volume: FakeVolumeKind.Remote);
        var job = ValidJob(new JobSource { Alias = "Red", Path = @"C:\Users\ana\Red" }) with { Destination = @"\\NAS\backups\pc" };

        await AssertSingleErrorAsync(job, "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Theory]
    [InlineData(@"c:/Users/ana/Documentos/", @"C:\Users\ana\Documentos")]
    [InlineData(@"c:\", @"C:\")]
    public async Task The_saved_job_has_the_normalized_source_path(string path, string expected)
    {
        _fileSystem.AddFolder(expected);
        var validation = await Validator.ValidateAsync(ValidJob(new JobSource { Alias = "X", Path = path }), [], TestContext.Current.CancellationToken);

        Assert.True(validation.Issues.IsValid);
        Assert.Equal(expected, validation.Job.Sources[0].Path);
        Assert.Equal(expected, Assert.Single(_fileSystem.Resolved, p => p == expected));
    }

    [Theory]
    [InlineData(@"C:\ProgramData::$INDEX_ALLOCATION")]
    [InlineData(@"C:\a:b")]
    [InlineData(@"C:\a\\b")]
    [InlineData(@"C:\a\.\b")]
    [InlineData(@"C:\a\..\b")]
    [InlineData(@"C:\docs.")]
    [InlineData(@"C:\docs ")]
    [InlineData(@"C:\a\CON")]
    [InlineData("C:\\a\u0001b")]
    public async Task A_path_that_is_not_normalized_is_SourcePathInvalid_and_never_opened(string path)
    {
        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourcePathInvalid);
        Assert.Empty(_fileSystem.Resolved);
    }

    [Theory]
    [InlineData(@"\??\C:\Users")]
    [InlineData("//nas/fotos")]
    public async Task Device_and_forward_slash_UNC_paths_are_not_local(string path)
    {
        await AssertSingleErrorAsync(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Fact]
    public async Task A_destination_whose_name_resolves_to_this_machine_is_not_allowed()
    {
        _hosts.Addresses["nas-falso"] = [System.Net.IPAddress.Loopback];

        await AssertSingleErrorAsync(ValidJob() with { Destination = @"\\nas-falso\copias" }, "Destination", ValidationIssueCode.DestinationNotAllowed);
    }

    [Fact]
    public async Task A_destination_whose_name_does_not_resolve_is_not_rejected_for_it()
    {
        _hosts.Errors["no-existe"] = FakeHostResolver.HostNotFound();

        Assert.True((await Validator.CheckAsync(ValidJob() with { Destination = @"\\no-existe\copias" }, [])).IsValid);
    }

    [Theory]
    [InlineData(@"\\nas\backups\pc-salon")]
    [InlineData(@"\\NAS\Backups\PC-Salon\")]
    [InlineData(@"\\nas\backups")]
    [InlineData(@"\\nas\backups\pc-salon\sub")]
    public async Task A_destination_used_by_another_job_is_rejected(string otherDestination)
    {
        var other = ValidJob() with { Id = Guid.NewGuid(), Destination = otherDestination };

        await AssertSingleErrorAsync(ValidJob(), [other], "Destination", ValidationIssueCode.DestinationUsedByOtherJob);
    }

    [Theory]
    [InlineData(@"\\nas\backups\pc-salon2")]
    [InlineData(@"\\nas2\backups\pc-salon")]
    [InlineData(@"\\nas\otras\pc-salon")]
    public async Task A_different_destination_of_another_job_is_allowed(string otherDestination)
    {
        var other = ValidJob() with { Id = Guid.NewGuid(), Destination = otherDestination };

        Assert.True((await Validator.CheckAsync(ValidJob(), [other])).IsValid);
    }

    [Fact]
    public async Task The_job_being_edited_does_not_conflict_with_its_own_destination()
    {
        var saved = ValidJob() with { Id = Guid.NewGuid() };

        Assert.True((await Validator.CheckAsync(saved with { Name = "Otro nombre" }, [saved])).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*.tmp\n")]
    public async Task An_empty_exclusion_pattern_or_one_with_control_characters_is_rejected(string pattern)
    {
        await AssertSingleErrorAsync(ValidJob() with { Exclusions = ["*.tmp", pattern] }, "Exclusions[1]", ValidationIssueCode.ExclusionPatternInvalid);
    }

    [Fact]
    public async Task A_daily_schedule_without_times_or_days_is_incomplete()
    {
        var job = ValidJob() with { Schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [], Days = DaysOfWeek.None } };

        var result = await Validator.CheckAsync(job, []);

        Assert.Equal(
            [("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete), ("Schedule.Days", ValidationIssueCode.ScheduleIncomplete)],
            result.Errors.Select(e => (e.Field, e.Code)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public async Task An_interval_outside_1_to_24_hours_is_out_of_range(int hours)
    {
        var job = ValidJob() with { Schedule = new JobSchedule { Kind = ScheduleKind.Interval, IntervalHours = hours } };

        await AssertSingleErrorAsync(job, "Schedule.IntervalHours", ValidationIssueCode.ValueOutOfRange);
    }

    [Fact]
    public async Task Negative_retention_bad_battery_threshold_and_non_positive_limits_are_out_of_range()
    {
        var job = ValidJob() with
        {
            Retention = new RetentionPolicy { KeepLast = -1 },
            Conditions = new JobConditions { MinBatteryPercent = 101 },
            MaxFileSizeBytes = 0,
            BandwidthLimitBytesPerSecond = -5,
        };

        var result = await Validator.CheckAsync(job, []);

        Assert.Equal(
            ["Retention.KeepLast", "Conditions.MinBatteryPercent", "MaxFileSizeBytes", "BandwidthLimitBytesPerSecond"],
            result.Errors.Select(e => e.Field));
        Assert.All(result.Errors, e => Assert.Equal(ValidationIssueCode.ValueOutOfRange, e.Code));
    }

    [Fact]
    public async Task Every_problem_is_reported_at_once_with_its_own_field()
    {
        var job = new JobDefinition
        {
            Name = "",
            Sources =
            [
                new JobSource { Alias = "A", Path = Documents },
                new JobSource { Alias = "a", Path = @"C:\no\existe" },
                new JobSource { Alias = "", Path = @"C:\ProgramData\Backbone" },
            ],
            Destination = @"C:\copias",
        };

        var result = await Validator.CheckAsync(job, []);

        Assert.Equal(
            [
                ("Name", ValidationIssueCode.NameRequired),
                ("Sources[1].Alias", ValidationIssueCode.AliasDuplicate),
                ("Sources[1].Path", ValidationIssueCode.SourceNotFound),
                ("Sources[2].Alias", ValidationIssueCode.AliasRequired),
                ("Sources[2].Path", ValidationIssueCode.SourceNotAllowed),
                ("Destination", ValidationIssueCode.DestinationNotUnc),
            ],
            result.Errors.Select(e => (e.Field, e.Code)));
    }

    [Theory]
    [InlineData(@"\\NAS\Backups\PC-Salon\", @"\\nas\backups\PC-Salon")]
    [InlineData(@"\\nas\backups", @"\\nas\backups")]
    public void The_destination_is_normalized_like_the_credentials(string destination, string expected)
    {
        Assert.True(JobValidator.TryNormalizeDestination(destination, out var normalized, out _));
        Assert.Equal(expected, normalized);
    }

    private Task<ValidationIssue> AssertSingleErrorAsync(JobDefinition job, string field, ValidationIssueCode code) =>
        AssertSingleErrorAsync(job, [], field, code);

    private async Task<ValidationIssue> AssertSingleErrorAsync(JobDefinition job, IReadOnlyList<JobDefinition> saved, string field, ValidationIssueCode code)
    {
        var result = await Validator.CheckAsync(job, saved);
        var error = Assert.Single(result.Errors);
        Assert.Equal(field, error.Field);
        Assert.Equal(code, error.Code);
        return error;
    }
}
