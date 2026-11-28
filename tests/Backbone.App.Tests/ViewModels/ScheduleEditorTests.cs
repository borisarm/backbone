using Backbone.App.Tests.Ipc;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-09 (app), criterio 1: la sección Programación del editor (Manual, Diaria, Intervalo).</summary>
public sealed class ScheduleEditorTests
{
    private static readonly string[] AllDayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private static (JobEditorViewModel Editor, FakeBackboneService Fake, List<JobDefinition> Validated) Create(JobDefinition? existing = null, Func<ValidationResult>? validation = null)
    {
        var (service, fake) = FakeBackboneService.Create();
        var validated = new List<JobDefinition>();
        fake.On("ValidateJobAsync", a =>
        {
            validated.Add((JobDefinition)a[0]!);
            return Task.FromResult(validation?.Invoke() ?? new ValidationResult());
        });
        fake.On("SaveJobAsync", a => Task.FromResult(((JobDefinition)a[0]!) with { Id = Guid.NewGuid() }));
        return (new JobEditorViewModel(service, new FakePicker(), k => k, existing), fake, validated);
    }

    private static JobDefinition Job(JobSchedule schedule) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Fotos",
        Destination = @"\\nas\b\f",
        Sources = [new JobSource { Path = @"C:\Datos", Alias = "Datos" }],
        Schedule = schedule,
    };

    private static ValidationIssue Issue(string field, ValidationIssueCode code) => new() { Field = field, Code = code };

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_NewJobStartsManualWithAllDaysAndNoTimes()
    {
        var (editor, _, _) = Create();
        using var _ = editor;

        var schedule = editor.BuildJob().Schedule;

        Assert.Equal(ScheduleKind.Manual, editor.Kind);
        Assert.Equal(ScheduleKind.Manual, schedule.Kind);
        Assert.Equal(DaysOfWeek.All, schedule.Days);
        Assert.Empty(schedule.TimesOfDay);
        Assert.False(editor.IsDaily);
        Assert.False(editor.IsInterval);
        Assert.False(editor.IsScheduled);
        Assert.Equal(AllDayNames, editor.DayChoices.Select(d => d.Name.Replace("Sched_Day_", "", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_DailyScheduleRoundTripsToJobDefinition()
    {
        var original = new JobSchedule
        {
            Kind = ScheduleKind.Daily,
            TimesOfDay = [new TimeOnly(18, 30), new TimeOnly(2, 0)],
            Days = DaysOfWeek.Monday | DaysOfWeek.Wednesday | DaysOfWeek.Sunday,
            IntervalHours = 6,
            CatchUpMissedRuns = false,
        };
        var (editor, _, _) = Create(Job(original));
        using var _ = editor;

        Assert.True(editor.IsDaily);
        Assert.Equal([new TimeSpan(2, 0, 0), new TimeSpan(18, 30, 0)], editor.Times.Select(t => t.Time));
        Assert.Equal(["Monday", "Wednesday", "Sunday"], editor.DayChoices.Where(d => d.IsSelected).Select(d => d.Day.ToString()));

        var built = editor.BuildJob().Schedule;

        // Las horas salen ordenadas; el resto, tal cual.
        Assert.Equal(original with { TimesOfDay = [new TimeOnly(2, 0), new TimeOnly(18, 30)] }, built, new ScheduleComparer());
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_IntervalScheduleRoundTrips()
    {
        var (editor, _, _) = Create(Job(new JobSchedule { Kind = ScheduleKind.Interval, IntervalHours = 8 }));
        using var _ = editor;

        Assert.True(editor.IsInterval);
        Assert.Equal(8, editor.IntervalHours);

        editor.IntervalHours = 12.4;
        var built = editor.BuildJob().Schedule;

        Assert.Equal(ScheduleKind.Interval, built.Kind);
        Assert.Equal(12, built.IntervalHours);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_EmptyIntervalBoxSendsZeroSoTheServiceRejectsIt()
    {
        var (editor, _, _) = Create(Job(new JobSchedule { Kind = ScheduleKind.Interval, IntervalHours = 8 }));
        using var _ = editor;

        editor.IntervalHours = double.NaN;

        Assert.Equal(0, editor.BuildJob().Schedule.IntervalHours);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_IntervalLimitsComeFromTheContract()
    {
        Assert.Equal(JobDefaults.MinIntervalHours, JobEditorViewModel.MinIntervalHours);
        Assert.Equal(JobDefaults.MaxIntervalHours, JobEditorViewModel.MaxIntervalHours);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_ChoosingDailyOnEmptyListProposesTwoAM_AndChoosingManualKeepsTimes()
    {
        var (editor, _, _) = Create();
        using var _ = editor;

        editor.ScheduleKindIndex = (int)ScheduleKind.Daily;

        Assert.Equal(TimeSpan.FromHours(2), Assert.Single(editor.Times).Time);

        editor.ScheduleKindIndex = (int)ScheduleKind.Manual;
        editor.ScheduleKindIndex = (int)ScheduleKind.Daily;
        Assert.Single(editor.Times);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_AddTimeSkipsTakenHoursAndRejectsDuplicates()
    {
        var (editor, _, _) = Create();
        using var _ = editor;
        editor.ScheduleKindIndex = (int)ScheduleKind.Daily;

        Assert.True(editor.AddTime());
        Assert.True(editor.AddTime());
        Assert.Equal([TimeSpan.FromHours(2), TimeSpan.FromHours(3), TimeSpan.FromHours(4)], editor.Times.Select(t => t.Time));

        Assert.False(editor.AddTime(new TimeSpan(3, 0, 0)));
        Assert.Equal(3, editor.Times.Count);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_DuplicateTimesAreStoredOnceAndNoted()
    {
        var (editor, _, _) = Create();
        using var _ = editor;
        editor.ScheduleKindIndex = (int)ScheduleKind.Daily;
        editor.AddTime(new TimeSpan(9, 0, 0));
        Assert.False(editor.HasDuplicateTimesNote);

        // El usuario cambia la hora de 02:00 a 09:00 con el TimePicker: queda repetida.
        editor.Times[0].Time = new TimeSpan(9, 0, 30);

        Assert.True(editor.HasDuplicateTimesNote);
        Assert.Equal("Sched_DuplicateTimes", editor.DuplicateTimesNote);
        Assert.Equal([new TimeOnly(9, 0)], editor.BuildJob().Schedule.TimesOfDay);

        editor.Times[0].Time = new TimeSpan(10, 0, 0);
        Assert.False(editor.HasDuplicateTimesNote);
        Assert.Equal([new TimeOnly(9, 0), new TimeOnly(10, 0)], editor.BuildJob().Schedule.TimesOfDay);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_DaysCheckboxesBuildTheFlags()
    {
        var (editor, _, _) = Create();
        using var _ = editor;
        editor.ScheduleKindIndex = (int)ScheduleKind.Daily;

        foreach (var day in editor.DayChoices)
        {
            day.IsSelected = day.Day is DaysOfWeek.Saturday or DaysOfWeek.Sunday;
        }

        Assert.Equal(DaysOfWeek.Saturday | DaysOfWeek.Sunday, editor.BuildJob().Schedule.Days);

        foreach (var day in editor.DayChoices)
        {
            day.IsSelected = false;
        }

        Assert.Equal(DaysOfWeek.None, editor.BuildJob().Schedule.Days);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Criterion1_CatchUpFlagRoundTrips()
    {
        var (editor, _, _) = Create(Job(new JobSchedule { Kind = ScheduleKind.Interval, CatchUpMissedRuns = false }));
        using var _ = editor;
        Assert.False(editor.CatchUpMissedRuns);

        editor.CatchUpMissedRuns = true;

        Assert.True(editor.BuildJob().Schedule.CatchUpMissedRuns);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public async Task Criterion1_SavedJobCarriesTheEditedSchedule()
    {
        var (editor, _, _) = Create(Job(new JobSchedule()));
        using var _ = editor;
        JobDefinition? saved = null;
        editor.Saved += (_, j) => saved = j;
        editor.ScheduleKindIndex = (int)ScheduleKind.Interval;
        editor.IntervalHours = 4;

        await editor.SaveAsync();

        Assert.NotNull(saved);
        Assert.Equal(ScheduleKind.Interval, saved.Schedule.Kind);
        Assert.Equal(4, saved.Schedule.IntervalHours);
    }

    // --- Validación en línea ---

    [Fact]
    [Trait("Story", "HU-09")]
    public async Task Criterion1_ServiceErrorsAppearNextToTheirScheduleFields()
    {
        var (editor, _, _) = Create(Job(new JobSchedule { Kind = ScheduleKind.Daily }), () => new ValidationResult
        {
            Errors =
            [
                Issue("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete),
                Issue("Schedule.Days", ValidationIssueCode.ScheduleIncomplete),
                Issue("Schedule.IntervalHours", ValidationIssueCode.ValueOutOfRange),
            ],
        });
        using var _ = editor;

        await editor.ValidateAsync();

        Assert.Equal("Validation_ScheduleIncomplete", editor.TimesError);
        Assert.Equal("Validation_ScheduleIncomplete", editor.DaysError);
        Assert.Equal("Validation_ValueOutOfRange", editor.IntervalError);
        Assert.True(editor.HasTimesError && editor.HasDaysError && editor.HasIntervalError);
        Assert.False(editor.HasGeneralError);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public async Task Criterion1_ValidateScheduleSendsTheJobButShowsOnlyScheduleErrors()
    {
        var (editor, _, validated) = Create(Job(new JobSchedule { Kind = ScheduleKind.Daily }) with { Name = string.Empty }, () => new ValidationResult
        {
            Errors = [Issue("Name", ValidationIssueCode.NameRequired), Issue("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete)],
        });
        using var _ = editor;
        editor.Times.Clear();

        await editor.ValidateScheduleAsync();

        Assert.Single(validated);
        Assert.Equal(ScheduleKind.Daily, validated[0].Schedule.Kind);
        Assert.True(editor.HasTimesError);
        Assert.False(editor.HasNameError);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public async Task Criterion1_ScheduleErrorsClearWhenTheServiceAcceptsTheFix()
    {
        var invalid = true;
        var (editor, _, _) = Create(Job(new JobSchedule { Kind = ScheduleKind.Daily }), () => invalid
            ? new ValidationResult { Errors = [Issue("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete)] }
            : new ValidationResult());
        using var _ = editor;
        await editor.ValidateScheduleAsync();
        Assert.True(editor.HasTimesError);

        invalid = false;
        await editor.AddTimeToListCommand.ExecuteAsync(null);

        Assert.False(editor.HasTimesError);
        Assert.Single(editor.Times);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public async Task Criterion1_RemovingAllTimesRevalidatesAndShowsTheServiceError()
    {
        var (editor, _, validated) = Create(
            Job(new JobSchedule { Kind = ScheduleKind.Daily, TimesOfDay = [new TimeOnly(2, 0)] }),
            () => new ValidationResult { Errors = [Issue("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete)] });
        using var _ = editor;

        await editor.RemoveTimeCommand.ExecuteAsync(editor.Times[0]);

        Assert.Empty(editor.Times);
        Assert.Empty(Assert.Single(validated).Schedule.TimesOfDay);
        Assert.Equal("Validation_ScheduleIncomplete", editor.TimesError);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public async Task Criterion1_SaveWithScheduleErrorDoesNotSaveAndShowsIt()
    {
        var (editor, fake, _) = Create(Job(new JobSchedule { Kind = ScheduleKind.Daily }), () => new ValidationResult
        {
            Errors = [Issue("Schedule.TimesOfDay", ValidationIssueCode.ScheduleIncomplete)],
        });
        using var _ = editor;

        await editor.SaveAsync();

        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
        Assert.Equal("Validation_ScheduleIncomplete", editor.TimesError);
    }

    [Fact]
    [Trait("Story", "HU-09")]
    public void Resources_ScheduleKeysExistInBothLanguages()
    {
        foreach (var culture in new[] { "es-ES", "en-US" })
        {
            var res = ResourceCoverageTests.LoadResources(culture);
            string[] keys =
            [
                "Editor_ScheduleHeader.Text", "Editor_ScheduleKind.Header", "Sched_Manual.Content", "Sched_Daily.Content", "Sched_Interval.Content",
                "Editor_TimesHeader.Text", "Editor_Time.Header", "Editor_AddTimeText.Text", "Editor_AddTime.AccessKey", "Editor_DaysHeader.Text",
                "Editor_IntervalHours.Header", "Editor_CatchUp.Content", "Sched_DuplicateTimes",
                "Editor_RemoveTime.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name",
                .. AllDayNames.Select(d => "Sched_Day_" + d),
            ];
            foreach (var key in keys)
            {
                Assert.True(res.TryGetValue(key, out var text) && !string.IsNullOrWhiteSpace(text), $"{culture}: {key}");
            }
        }
    }

    private sealed class ScheduleComparer : IEqualityComparer<JobSchedule>
    {
        public bool Equals(JobSchedule? x, JobSchedule? y) =>
            x is not null && y is not null
            && x.Kind == y.Kind && x.Days == y.Days && x.IntervalHours == y.IntervalHours && x.CatchUpMissedRuns == y.CatchUpMissedRuns
            && x.TimesOfDay.SequenceEqual(y.TimesOfDay);

        public int GetHashCode(JobSchedule obj) => obj.Kind.GetHashCode();
    }
}
