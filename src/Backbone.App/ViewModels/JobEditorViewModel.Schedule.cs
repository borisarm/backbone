using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backbone.App.ViewModels;

/// <summary>Una hora del día de una programación diaria (hora local del equipo, §7: <c>JobSchedule.TimesOfDay</c>).</summary>
internal sealed partial class ScheduleTimeItem : ObservableObject
{
    public ScheduleTimeItem(TimeSpan time) => Time = time;

    /// <summary>Hora del día (el <c>TimePicker</c> trabaja con <see cref="TimeSpan"/>).</summary>
    [ObservableProperty]
    public partial TimeSpan Time { get; set; }

    /// <summary>Hora como <see cref="TimeOnly"/> truncada al minuto.</summary>
    public TimeOnly ToTimeOnly()
    {
        var minutes = (long)Math.Floor(Time.TotalMinutes);
        minutes = ((minutes % 1440) + 1440) % 1440;
        return new TimeOnly((int)(minutes / 60), (int)(minutes % 60));
    }
}

/// <summary>Un día de la semana de la programación diaria.</summary>
internal sealed partial class DayChoiceViewModel : ObservableObject
{
    public DayChoiceViewModel(DaysOfWeek day, string name, bool isSelected)
    {
        Day = day;
        Name = name;
        IsSelected = isSelected;
    }

    /// <summary>Día.</summary>
    public DaysOfWeek Day { get; }

    /// <summary>Nombre localizado.</summary>
    public string Name { get; }

    /// <summary>El trabajo se ejecuta ese día.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Sección "Programación" del editor (HU-09, RF-10, RF-11; §9.2). Solo edita el <see cref="JobSchedule"/>: las reglas las valida el servicio.</summary>
internal sealed partial class JobEditorViewModel
{
    private static readonly TimeSpan s_defaultTime = TimeSpan.FromHours(2);

    private bool _loadingSchedule;

    /// <summary>Mínimo de <see cref="JobSchedule.IntervalHours"/> (del contrato).</summary>
    public static double MinIntervalHours => JobDefaults.MinIntervalHours;

    /// <summary>Máximo de <see cref="JobSchedule.IntervalHours"/> (del contrato).</summary>
    public static double MaxIntervalHours => JobDefaults.MaxIntervalHours;

    /// <summary>Horas del día de la programación diaria.</summary>
    public ObservableCollection<ScheduleTimeItem> Times { get; } = [];

    /// <summary>Los siete días de la semana, de lunes a domingo.</summary>
    public ObservableCollection<DayChoiceViewModel> DayChoices { get; } = [];

    /// <summary>Tipo de programación: 0 = Manual, 1 = Diaria, 2 = Intervalo (mismo orden que <see cref="ScheduleKind"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind), nameof(IsDaily), nameof(IsInterval), nameof(IsScheduled))]
    public partial int ScheduleKindIndex { get; set; }

    /// <summary>Horas entre ejecuciones (<c>NumberBox</c>; <see cref="double.NaN"/> si está vacío).</summary>
    [ObservableProperty]
    public partial double IntervalHours { get; set; }

    /// <summary>Recuperar copias perdidas al arrancar el servicio o al reanudar (RF-11).</summary>
    [ObservableProperty]
    public partial bool CatchUpMissedRuns { get; set; }

    /// <summary>Error de validación junto a la lista de horas.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimesError))]
    public partial string TimesError { get; set; }

    /// <summary>Error de validación junto a los días.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDaysError))]
    public partial string DaysError { get; set; }

    /// <summary>Error de validación junto al intervalo.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntervalError))]
    public partial string IntervalError { get; set; }

    /// <summary>Aviso local: hay horas repetidas y solo se guardará una vez cada una.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDuplicateTimesNote))]
    public partial string DuplicateTimesNote { get; set; }

    /// <summary>Tipo de programación elegido.</summary>
    public ScheduleKind Kind => (ScheduleKind)Math.Clamp(ScheduleKindIndex, 0, 2);

    /// <summary>Programación diaria.</summary>
    public bool IsDaily => Kind == ScheduleKind.Daily;

    /// <summary>Programación por intervalo.</summary>
    public bool IsInterval => Kind == ScheduleKind.Interval;

    /// <summary>Hay programación automática (diaria o por intervalo): tiene sentido "Recuperar copias perdidas".</summary>
    public bool IsScheduled => Kind != ScheduleKind.Manual;

    /// <summary>Hay error en las horas.</summary>
    public bool HasTimesError => TimesError.Length > 0;

    /// <summary>Hay error en los días.</summary>
    public bool HasDaysError => DaysError.Length > 0;

    /// <summary>Hay error en el intervalo.</summary>
    public bool HasIntervalError => IntervalError.Length > 0;

    /// <summary>Hay horas repetidas.</summary>
    public bool HasDuplicateTimesNote => DuplicateTimesNote.Length > 0;

    /// <summary>
    /// Añade una hora. Sin argumento (botón "Añadir hora") toma la primera hora en punto que aún no esté en la lista a partir de las 02:00.
    /// Una hora que ya está no se añade (se devuelve <see langword="false"/>).
    /// </summary>
    /// <param name="time">Hora local; <see langword="null"/> para elegir una libre.</param>
    /// <returns><see langword="true"/> si se añadió.</returns>
    public bool AddTime(TimeSpan? time = null)
    {
        var item = new ScheduleTimeItem(time ?? NextFreeTime());
        if (Times.Any(t => t.ToTimeOnly() == item.ToTimeOnly()))
        {
            return false;
        }

        Times.Add(item);
        ClearTimesErrorOnEdit();
        return true;
    }

    /// <summary>Quita una hora de la lista.</summary>
    /// <param name="item">Hora a quitar.</param>
    [RelayCommand]
    private async Task RemoveTimeAsync(ScheduleTimeItem? item)
    {
        if (item is not null && Times.Remove(item))
        {
            ClearTimesErrorOnEdit();
            await ValidateScheduleAsync().ConfigureAwait(true);
        }
    }

    /// <summary>"Añadir hora": añade una hora libre y valida la sección.</summary>
    /// <returns>Una tarea que termina al validar.</returns>
    [RelayCommand]
    private async Task AddTimeToListAsync()
    {
        AddTime();
        await ValidateScheduleAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Valida con el servicio solo la sección Programación: pide <c>ValidateJobAsync</c> con el trabajo entero, pero muestra
    /// únicamente los problemas de la programación (no marca como erróneos campos que el usuario aún no ha tocado).
    /// </summary>
    /// <returns>Una tarea que termina al repartir los errores.</returns>
    public async Task ValidateScheduleAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var version = ++_validationVersion;
        try
        {
            var result = await _service.ValidateJobAsync(BuildJob(), CancellationToken.None).ConfigureAwait(true);
            if (version == _validationVersion)
            {
                ClearScheduleMessages();
                foreach (var issue in result.Errors)
                {
                    TryApplyScheduleIssue(issue.Field, _issueTexts.Translate(issue));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Sin efecto: no hay nada que mostrar.
        }
        catch (Exception ex)
        {
            if (version == _validationVersion)
            {
                GeneralError = _errorTexts.Describe(ex);
            }
        }
    }

    partial void OnScheduleKindIndexChanged(int value)
    {
        if (!_loadingSchedule && value == (int)ScheduleKind.Daily && Times.Count == 0)
        {
            // Una programación diaria sin horas no es válida: se parte de una hora habitual (02:00) que el usuario puede cambiar.
            Times.Add(new ScheduleTimeItem(s_defaultTime));
        }

        ClearScheduleMessages();
    }

    private void LoadSchedule(JobSchedule schedule)
    {
        TimesError = DaysError = IntervalError = DuplicateTimesNote = string.Empty;
        _loadingSchedule = true;
        try
        {
            ScheduleKindIndex = (int)schedule.Kind;
            IntervalHours = schedule.IntervalHours;
            CatchUpMissedRuns = schedule.CatchUpMissedRuns;
            Times.Clear();
            foreach (var time in schedule.TimesOfDay.Distinct().Order())
            {
                Times.Add(new ScheduleTimeItem(time.ToTimeSpan()));
            }

            DayChoices.Clear();
            foreach (var day in new[] { DaysOfWeek.Monday, DaysOfWeek.Tuesday, DaysOfWeek.Wednesday, DaysOfWeek.Thursday, DaysOfWeek.Friday, DaysOfWeek.Saturday, DaysOfWeek.Sunday })
            {
                DayChoices.Add(new DayChoiceViewModel(day, _getString("Sched_Day_" + day), schedule.Days.HasFlag(day)));
            }
        }
        finally
        {
            _loadingSchedule = false;
        }

        Times.CollectionChanged += OnTimesChanged;
        foreach (var item in Times)
        {
            item.PropertyChanged += OnTimeItemChanged;
        }

        UpdateDuplicateNote();
    }

    private JobSchedule BuildSchedule() => new()
    {
        Kind = Kind,
        // Las horas repetidas se guardan una sola vez y ordenadas.
        TimesOfDay = [.. Times.Select(t => t.ToTimeOnly()).Distinct().Order()],
        Days = DayChoices.Where(d => d.IsSelected).Aggregate(DaysOfWeek.None, (all, d) => all | d.Day),
        IntervalHours = double.IsNaN(IntervalHours) ? 0 : (int)Math.Clamp(Math.Round(IntervalHours), int.MinValue, int.MaxValue),
        CatchUpMissedRuns = CatchUpMissedRuns,
    };

    private TimeSpan NextFreeTime()
    {
        var taken = Times.Select(t => t.ToTimeOnly()).ToHashSet();
        for (var i = 0; i < 24; i++)
        {
            var candidate = s_defaultTime + TimeSpan.FromHours(i);
            var time = new TimeOnly((int)candidate.TotalHours % 24, 0);
            if (!taken.Contains(time))
            {
                return time.ToTimeSpan();
            }
        }

        return s_defaultTime;
    }

    private void OnTimesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ScheduleTimeItem item in e.OldItems)
            {
                item.PropertyChanged -= OnTimeItemChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ScheduleTimeItem item in e.NewItems)
            {
                item.PropertyChanged += OnTimeItemChanged;
            }
        }

        UpdateDuplicateNote();
    }

    private void OnTimeItemChanged(object? sender, PropertyChangedEventArgs e) => UpdateDuplicateNote();

    private void UpdateDuplicateNote()
    {
        var hasDuplicates = Times.Select(t => t.ToTimeOnly()).GroupBy(t => t).Any(g => g.Count() > 1);
        DuplicateTimesNote = hasDuplicates ? _getString("Sched_DuplicateTimes") : string.Empty;
    }

    private void ClearTimesErrorOnEdit() => TimesError = string.Empty;

    private void ClearScheduleMessages() => TimesError = DaysError = IntervalError = string.Empty;

    private bool TryApplyScheduleIssue(string field, string text)
    {
        switch (field)
        {
            case "Schedule.TimesOfDay":
                TimesError = Join(TimesError, text);
                return true;
            case "Schedule.Days":
                DaysError = Join(DaysError, text);
                return true;
            case "Schedule.IntervalHours":
                IntervalError = Join(IntervalError, text);
                return true;
            default:
                return false;
        }
    }
}
