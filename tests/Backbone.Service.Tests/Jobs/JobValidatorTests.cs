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

    private JobValidator Validator => new(
        _fileSystem,
        new ProtectedFolderResolver(new ServicePaths(@"C:\ProgramData\Backbone"), _fileSystem, NullLogger<ProtectedFolderResolver>.Instance));

    private static JobDefinition ValidJob(params JobSource[] sources) =>
        JobDefaults.NewJob() with
        {
            Name = "Documentos",
            Sources = sources.Length > 0 ? sources : [new JobSource { Alias = "Documentos", Path = Documents }],
            Destination = @"\\nas\backups\pc-salon",
        };

    [Fact]
    public void A_valid_job_has_no_errors_or_warnings()
    {
        var result = Validator.Validate(ValidJob(), []);

        Assert.True(result.IsValid);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void The_name_is_required(string name)
    {
        AssertSingleError(ValidJob() with { Name = name }, "Name", ValidationIssueCode.NameRequired);
    }

    [Fact]
    public void A_name_with_control_characters_or_lone_surrogates_is_rejected()
    {
        // Las suplentes sueltas no se pueden escribir en un atributo (los textos de los atributos se guardan en UTF-8).
        foreach (var name in new[] { "Docs\u0007", "Docs\ud800", "\udc00Docs" })
        {
            AssertSingleError(ValidJob() with { Name = name }, "Name", ValidationIssueCode.ValueOutOfRange);
        }

        Assert.True(Validator.Validate(ValidJob() with { Name = "Fotos 📷" }, []).IsValid);
    }

    [Fact]
    public void At_least_one_source_is_required()
    {
        AssertSingleError(ValidJob() with { Sources = [] }, "Sources", ValidationIssueCode.SourcesRequired);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void The_alias_is_required(string alias)
    {
        AssertSingleError(ValidJob(new JobSource { Alias = alias, Path = Documents }), "Sources[0].Alias", ValidationIssueCode.AliasRequired);
    }

    [Theory]
    [InlineData("Docs/2024")]
    [InlineData("a:b")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("..")]
    [InlineData("Docs.")]
    [InlineData("Docs ")]
    public void An_alias_that_is_not_a_valid_folder_name_is_rejected(string alias)
    {
        AssertSingleError(ValidJob(new JobSource { Alias = alias, Path = Documents }), "Sources[0].Alias", ValidationIssueCode.AliasInvalid);
    }

    [Fact]
    public void Two_equal_aliases_ignoring_case_mark_the_repeated_one_with_its_value()
    {
        var job = ValidJob(
            new JobSource { Alias = "Fotos", Path = Documents },
            new JobSource { Alias = "Imágenes", Path = Pictures },
            new JobSource { Alias = "FOTOS", Path = Pictures });

        var error = AssertSingleError(job, "Sources[2].Alias", ValidationIssueCode.AliasDuplicate);
        Assert.Equal("FOTOS", error.Value);
    }

    [Fact]
    public void A_missing_folder_is_rejected_on_its_path()
    {
        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Otra", Path = @"C:\no\existe" });

        AssertSingleError(job, "Sources[1].Path", ValidationIssueCode.SourceNotFound);
    }

    [Theory]
    [InlineData((int)LocalFolderStatus.NotFound)]
    [InlineData((int)LocalFolderStatus.NotADirectory)]
    [InlineData((int)LocalFolderStatus.Unreadable)]
    public void A_path_that_is_not_an_openable_folder_is_SourceNotFound(int status)
    {
        _fileSystem.AddStatus(@"C:\x", (LocalFolderStatus)status);

        AssertSingleError(ValidJob(new JobSource { Alias = "X", Path = @"C:\x" }), "Sources[0].Path", ValidationIssueCode.SourceNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Documentos")]
    [InlineData(@"relativa\carpeta")]
    public void An_empty_or_relative_path_is_SourceNotFound(string path)
    {
        AssertSingleError(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourceNotFound);
    }

    [Theory]
    [InlineData(@"\\nas\fotos")]
    [InlineData(@"\\?\C:\Users")]
    [InlineData(@"\\.\C:\Users")]
    public void A_UNC_or_device_path_is_not_local(string path)
    {
        AssertSingleError(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Fact]
    public void A_mapped_network_drive_is_not_local()
    {
        _fileSystem.AddStatus(@"Z:\Fotos", LocalFolderStatus.Remote);

        AssertSingleError(ValidJob(new JobSource { Alias = "Fotos", Path = @"Z:\Fotos" }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Theory]
    [InlineData((int)LocalVolumeKind.Remote)]
    [InlineData((int)LocalVolumeKind.Removable)]
    [InlineData((int)LocalVolumeKind.Other)]
    public void A_folder_whose_final_path_is_not_on_a_fixed_disk_is_not_local(int volume)
    {
        _fileSystem.AddFolder(@"C:\Users\ana\Enlace", @"E:\Fotos", volume: (LocalVolumeKind)volume);

        AssertSingleError(ValidJob(new JobSource { Alias = "Fotos", Path = @"C:\Users\ana\Enlace" }), "Sources[0].Path", ValidationIssueCode.SourceNotLocal);
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("exFAT")]
    [InlineData("ReFS")]
    [InlineData("")]
    public void A_folder_on_a_volume_that_is_not_NTFS_is_rejected(string fileSystem)
    {
        _fileSystem.AddFolder(@"D:\Fotos", fileSystem: fileSystem);

        AssertSingleError(ValidJob(new JobSource { Alias = "Fotos", Path = @"D:\Fotos" }), "Sources[0].Path", ValidationIssueCode.SourceNotNtfs);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Backbone", @"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone\credentials", @"C:\ProgramData\Backbone")]
    [InlineData(@"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef", @"C:\ProgramData\Backbone.untrusted-0123456789abcdef0123456789abcdef")]
    public void The_data_folder_a_folder_inside_it_or_a_quarantined_folder_is_SourceNotAllowed(string path, string protectedPath)
    {
        var error = AssertSingleError(ValidJob(new JobSource { Alias = "X", Path = path }), "Sources[0].Path", ValidationIssueCode.SourceNotAllowed);
        Assert.Equal(protectedPath, error.Value);
    }

    [Fact]
    public void A_junction_to_the_data_folder_is_SourceNotAllowed_comparing_final_paths()
    {
        _fileSystem.AddFolder(@"C:\Users\ana\Atajo", finalPath: @"C:\ProgramData\Backbone\credentials");

        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Atajo", Path = @"C:\Users\ana\Atajo" });

        AssertSingleError(job, "Sources[1].Path", ValidationIssueCode.SourceNotAllowed);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\ProgramData")]
    public void A_source_containing_the_data_folder_is_saved_with_the_SourceContainsDataFolder_warning(string path)
    {
        var result = Validator.Validate(ValidJob(new JobSource { Alias = "Disco", Path = path }), []);

        Assert.True(result.IsValid);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("Sources[0].Path", warning.Field);
        Assert.Equal(ValidationIssueCode.SourceContainsDataFolder, warning.Code);
        Assert.Equal(@"C:\ProgramData\Backbone", warning.Value);
    }

    [Fact]
    public void A_source_containing_ProgramData_fails_closed_with_more_than_100_quarantined_folders()
    {
        _fileSystem.QuarantinedFolders.AddRange(Enumerable.Range(0, 101).Select(i => $"Backbone.untrusted-{i}"));

        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Disco", Path = @"C:\" });

        var error = AssertSingleError(job, "Sources[1].Path", ValidationIssueCode.SourceNotAllowed);
        Assert.Equal(@"C:\ProgramData", error.Value);
    }

    [Fact]
    public void The_protected_folders_are_computed_once_per_validation()
    {
        var job = ValidJob(
            new JobSource { Alias = "Documentos", Path = Documents },
            new JobSource { Alias = "Imágenes", Path = Pictures });

        Validator.Validate(job, []);

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
    public void A_destination_that_is_not_an_allowed_UNC_path_is_rejected(string destination, ValidationIssueCode code)
    {
        AssertSingleError(ValidJob() with { Destination = destination }, "Destination", code);
    }

    [Fact]
    public void This_machine_is_not_an_allowed_destination()
    {
        AssertSingleError(ValidJob() with { Destination = $@"\\{Environment.MachineName}\copias" }, "Destination", ValidationIssueCode.DestinationNotAllowed);
    }

    [Fact]
    public void A_destination_inside_a_source_is_rejected()
    {
        // Solo es posible si el origen lleva a la red (que además no es local).
        _fileSystem.AddFolder(@"C:\Users\ana\Red", finalPath: @"\\nas\backups", volume: LocalVolumeKind.Remote);
        var job = ValidJob(new JobSource { Alias = "Red", Path = @"C:\Users\ana\Red" }) with { Destination = @"\\NAS\backups\pc" };

        var result = Validator.Validate(job, []);

        Assert.Contains(result.Errors, e => e.Field == "Destination" && e.Code == ValidationIssueCode.DestinationInsideSource);
        Assert.Contains(result.Errors, e => e.Field == "Sources[0].Path" && e.Code == ValidationIssueCode.SourceNotLocal);
    }

    [Theory]
    [InlineData(@"\\nas\backups\pc-salon")]
    [InlineData(@"\\NAS\Backups\PC-Salon\")]
    [InlineData(@"\\nas\backups")]
    [InlineData(@"\\nas\backups\pc-salon\sub")]
    public void A_destination_used_by_another_job_is_rejected(string otherDestination)
    {
        var other = ValidJob() with { Id = Guid.NewGuid(), Destination = otherDestination };

        AssertSingleError(ValidJob(), [other], "Destination", ValidationIssueCode.DestinationUsedByOtherJob);
    }

    [Theory]
    [InlineData(@"\\nas\backups\pc-salon2")]
    [InlineData(@"\\nas2\backups\pc-salon")]
    [InlineData(@"\\nas\otras\pc-salon")]
    public void A_different_destination_of_another_job_is_allowed(string otherDestination)
    {
        var other = ValidJob() with { Id = Guid.NewGuid(), Destination = otherDestination };

        Assert.True(Validator.Validate(ValidJob(), [other]).IsValid);
    }

    [Fact]
    public void The_job_being_edited_does_not_conflict_with_its_own_destination()
    {
        var saved = ValidJob() with { Id = Guid.NewGuid() };

        Assert.True(Validator.Validate(saved with { Name = "Otro nombre" }, [saved]).IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("*.tmp\n")]
    public void An_empty_exclusion_pattern_or_one_with_control_characters_is_rejected(string pattern)
    {
        AssertSingleError(ValidJob() with { Exclusions = ["*.tmp", pattern] }, "Exclusions[1]", ValidationIssueCode.ExclusionPatternInvalid);
    }

    [Fact]
    public void A_daily_schedule_without_times_or_days_is_incomplete()
    {
        var job = ValidJob() with { Schedule = new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [], Days = DaysOfWeek.None } };

        var result = Validator.Validate(job, []);

        Assert.Equal(
            [("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete), ("Schedule.Days", ValidationIssueCode.ScheduleIncomplete)],
            result.Errors.Select(e => (e.Field, e.Code)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    [InlineData(-1)]
    public void An_interval_outside_1_to_24_hours_is_out_of_range(int hours)
    {
        var job = ValidJob() with { Schedule = new JobSchedule { Kind = ScheduleKind.Interval, IntervalHours = hours } };

        AssertSingleError(job, "Schedule.IntervalHours", ValidationIssueCode.ValueOutOfRange);
    }

    [Fact]
    public void Negative_retention_bad_battery_threshold_and_non_positive_limits_are_out_of_range()
    {
        var job = ValidJob() with
        {
            Retention = new RetentionPolicy { KeepLast = -1 },
            Conditions = new JobConditions { MinBatteryPercent = 101 },
            MaxFileSizeBytes = 0,
            BandwidthLimitBytesPerSecond = -5,
        };

        var result = Validator.Validate(job, []);

        Assert.Equal(
            ["Retention.KeepLast", "Conditions.MinBatteryPercent", "MaxFileSizeBytes", "BandwidthLimitBytesPerSecond"],
            result.Errors.Select(e => e.Field));
        Assert.All(result.Errors, e => Assert.Equal(ValidationIssueCode.ValueOutOfRange, e.Code));
    }

    [Fact]
    public void Every_problem_is_reported_at_once_with_its_own_field()
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

        var result = Validator.Validate(job, []);

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

    private ValidationIssue AssertSingleError(JobDefinition job, string field, ValidationIssueCode code) =>
        AssertSingleError(job, [], field, code);

    private ValidationIssue AssertSingleError(JobDefinition job, IReadOnlyList<JobDefinition> saved, string field, ValidationIssueCode code)
    {
        var result = Validator.Validate(job, saved);
        var error = Assert.Single(result.Errors);
        Assert.Equal(field, error.Field);
        Assert.Equal(code, error.Code);
        return error;
    }
}
