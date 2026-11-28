using System.Collections.ObjectModel;
using System.Globalization;
using Backbone.App.Ipc;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backbone.App.ViewModels;

/// <summary>
/// Tarjeta de un trabajo en Inicio (§9.2, RF-51). Se actualiza en el sitio (no se recrea) para no perder el foco del teclado
/// ni repetir anuncios del Narrador cuando llega un <c>StatusChanged</c>.
/// </summary>
internal sealed partial class JobCard : ObservableObject
{
    private readonly Func<string, string> _getString;

    public JobCard(Guid jobId, JobRunState run, Func<string, string> getString)
    {
        JobId = jobId;
        Run = run;
        _getString = getString;
        Name = string.Empty;
        StateText = string.Empty;
        LastRunText = string.Empty;
        NextRunText = string.Empty;
    }

    /// <summary>Identificador.</summary>
    public Guid JobId { get; }

    /// <summary>Estado de ejecución: Copiar ahora, Cancelar y progreso en vivo.</summary>
    public JobRunState Run { get; }

    /// <summary>Nombre.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName), nameof(RunNowAutomationName), nameof(CancelAutomationName), nameof(ProgressAutomationName))]
    public partial string Name { get; set; }

    /// <summary>Estado en una frase (Al día, Con error, Copiando ahora…). Región viva: el Narrador anuncia sus cambios.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string StateText { get; set; }

    /// <summary>Última ejecución con su resultado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string LastRunText { get; set; }

    /// <summary>Próxima ejecución, "Manual" o "—".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial string NextRunText { get; set; }

    /// <summary>El destino tiene un formato más nuevo (RNF-14): se muestra el aviso de solo lectura.</summary>
    [ObservableProperty]
    public partial bool IsReadOnly { get; set; }

    /// <summary>Nombre accesible de la tarjeta para el Narrador: el resumen completo en una frase.</summary>
    public string AutomationName => $"{Name}. {StateText}. {LastRunText}. {NextRunText}";

    /// <summary>Nombre accesible de "Copiar ahora", con el trabajo.</summary>
    public string RunNowAutomationName => string.Format(CultureInfo.CurrentCulture, _getString("Home_RunNowFor"), Name);

    /// <summary>Nombre accesible de "Cancelar", con el trabajo.</summary>
    public string CancelAutomationName => string.Format(CultureInfo.CurrentCulture, _getString("Home_CancelFor"), Name);

    /// <summary>Nombre accesible de la barra de progreso, con el trabajo.</summary>
    public string ProgressAutomationName => string.Format(CultureInfo.CurrentCulture, _getString("Home_ProgressFor"), Name);
}

/// <summary>Página Inicio (§9.2): estado general, una tarjeta por trabajo y los estados especiales de §9.3.</summary>
internal sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly IBackboneService _service;
    private readonly Func<string, string> _getString;
    private readonly BackboneErrorTexts _errorTexts;
    private readonly JobRunTracker _tracker;
    private readonly Dictionary<Guid, ScheduleKind> _scheduleKinds = [];
    private ServiceStatus? _lastStatus;
    private int _version;

    public HomeViewModel(
        IBackboneService service,
        Func<string, string> getString,
        IConnectionMonitor? connection = null,
        IServiceStarter? starter = null,
        TimeProvider? time = null)
    {
        _service = service;
        _getString = getString;
        _errorTexts = new BackboneErrorTexts(getString);
        Error = string.Empty;
        OverallTitle = string.Empty;
        OverallDetail = string.Empty;
        OverallGlyph = GlyphOk;
        _tracker = new JobRunTracker(service, getString, time);
        Connection = new ConnectionStatusViewModel(connection, starter, getString);
        Connection.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionStatusViewModel.State))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(ShowOverall));
            }
        };
        _service.StatusChanged += OnStatusChanged;
    }

    private const string GlyphOk = "";
    private const string GlyphWarning = "";
    private const string GlyphError = "";
    private const string GlyphCopying = "";

    /// <summary>Estados especiales de la conexión (§9.3).</summary>
    public ConnectionStatusViewModel Connection { get; }

    /// <summary>Una tarjeta por trabajo.</summary>
    public ObservableCollection<JobCard> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(ShowOverall))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; }

    /// <summary>Título de la tarjeta de estado general.</summary>
    [ObservableProperty]
    public partial string OverallTitle { get; set; }

    /// <summary>Detalle de la tarjeta de estado general.</summary>
    [ObservableProperty]
    public partial string OverallDetail { get; set; }

    /// <summary>Glifo de Segoe Fluent Icons de la tarjeta de estado general.</summary>
    [ObservableProperty]
    public partial string OverallGlyph { get; set; }

    /// <summary>La lectura terminó, hay conexión y no hay trabajos: estado vacío con botón para crear uno (§9.3).</summary>
    public bool IsEmpty => IsLoaded && Cards.Count == 0 && Connection.ShowContent;

    /// <summary>Se muestra la tarjeta de estado general.</summary>
    public bool ShowOverall => IsLoaded && Cards.Count > 0 && Connection.ShowContent;

    /// <summary>Hay un error de lectura.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Se pide crear un trabajo (la página Trabajos abre el editor).</summary>
    public event EventHandler? CreateJobRequested;

    /// <summary>Lee el estado del servicio. Llamar al conectar y tras reconectar (los eventos perdidos no se recuperan, §9.1).</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al actualizar las tarjetas.</returns>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var version = ++_version;
        try
        {
            var status = await _service.GetStatusAsync(ct).ConfigureAwait(true);
            await RefreshScheduleKindsAsync(ct).ConfigureAwait(true);
            if (version == _version)
            {
                Apply(status);
                Error = string.Empty;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                Error = _errorTexts.Describe(ex);
            }
        }
    }

    public void Dispose()
    {
        _service.StatusChanged -= OnStatusChanged;
        _tracker.Dispose();
        Connection.Dispose();
    }

    [RelayCommand]
    private void CreateJob() => CreateJobRequested?.Invoke(this, EventArgs.Empty);

    private void OnStatusChanged(object? sender, StatusChangedEvent e)
    {
        _version++;
        Apply(e.Status);
        Error = string.Empty;
        _ = ReapplyIfScheduleKindsChangedAsync();
    }

    /// <summary>
    /// El estado no dice si un trabajo es manual: se lee de <c>GetJobs</c> (solo para mostrar "Manual" en lugar de "—").
    /// Un fallo aquí no es un error de la página: la próxima ejecución se muestra como "—".
    /// </summary>
    private async Task<bool> RefreshScheduleKindsAsync(CancellationToken ct)
    {
        try
        {
            var jobs = await _service.GetJobsAsync(ct).ConfigureAwait(true);
            var changed = jobs.Count != _scheduleKinds.Count || jobs.Any(j => !_scheduleKinds.TryGetValue(j.Id, out var kind) || kind != j.Schedule.Kind);
            _scheduleKinds.Clear();
            foreach (var job in jobs)
            {
                _scheduleKinds[job.Id] = job.Schedule.Kind;
            }

            return changed;
        }
#pragma warning disable CA1031 // Dato accesorio: sin él se muestra "—".
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    private async Task ReapplyIfScheduleKindsChangedAsync()
    {
        var version = _version;
        if (await RefreshScheduleKindsAsync(CancellationToken.None).ConfigureAwait(true) && version == _version && _lastStatus is { } status)
        {
            Apply(status);
        }
    }

    private void Apply(ServiceStatus status)
    {
        _lastStatus = status;
        _tracker.Apply(status);
        SyncCards(status);

        DescribeOverall(status);
        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowOverall));
    }

    /// <summary>Actualiza las tarjetas en el sitio, conservando las que siguen existiendo y su orden.</summary>
    private void SyncCards(ServiceStatus status)
    {
        var wanted = status.Jobs.Select(j => j.JobId).ToHashSet();
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Cards[i].JobId))
            {
                Cards.RemoveAt(i);
            }
        }

        for (var i = 0; i < status.Jobs.Count; i++)
        {
            var job = status.Jobs[i];
            var existing = Cards.FirstOrDefault(c => c.JobId == job.JobId);
            if (existing is null)
            {
                existing = new JobCard(job.JobId, _tracker.Get(job.JobId), _getString);
                Cards.Insert(i, existing);
            }
            else if (Cards.IndexOf(existing) != i)
            {
                Cards.Move(Cards.IndexOf(existing), i);
            }

            existing.Name = UntrustedText.Describe(job.JobName);
            existing.StateText = DescribeState(job);
            existing.LastRunText = DescribeLastRun(job);
            existing.NextRunText = DescribeNextRun(job);
            existing.IsReadOnly = job.RepositoryState == RepositoryState.ReadOnlyNewerFormat;
        }
    }

    private string DescribeState(JobStatus job)
    {
        if (job.Activity == JobActivity.Running)
        {
            return _getString("Home_Job_Running");
        }

        if (job.Activity == JobActivity.Queued)
        {
            return _getString("Home_Job_Queued");
        }

        if (!job.Enabled)
        {
            return _getString("Home_Job_Disabled");
        }

        return _getString(job.Health switch
        {
            HealthState.Error => "Home_Job_Error",
            HealthState.Warning => job.IsOutdated ? "Home_Job_Outdated" : "Home_Job_Warning",
            _ => job.IsOutdated ? "Home_Job_Outdated" : "Home_Job_Ok",
        });
    }

    private string DescribeLastRun(JobStatus job)
    {
        var culture = CultureInfo.CurrentCulture;
        if (job.LastRun is not { } run)
        {
            return _getString("Home_Job_NoRuns");
        }

        var when = (run.CompletedUtc ?? run.StartedUtc).ToLocalTime().ToString("g", culture);
        return string.Format(culture, _getString("Home_Job_LastRunFormat"), when, _getString("Home_Result_" + run.State));
    }

    private string DescribeNextRun(JobStatus job)
    {
        var culture = CultureInfo.CurrentCulture;
        string value;
        if (job.NextRunUtc is { } next)
        {
            value = next.ToLocalTime().ToString("g", culture);
        }
        else if (job.Enabled && _scheduleKinds.TryGetValue(job.JobId, out var kind) && kind == ScheduleKind.Manual)
        {
            value = _getString("Home_Job_Manual");
        }
        else
        {
            value = "—";
        }

        return string.Format(culture, _getString("Home_Job_NextRunFormat"), value);
    }

    private void DescribeOverall(ServiceStatus status)
    {
        var culture = CultureInfo.CurrentCulture;
        var running = status.Jobs.Count(j => j.Activity == JobActivity.Running);
        var failed = status.Jobs.Count(j => j.Enabled && j.Health == HealthState.Error);
        var warned = status.Jobs.Count(j => j.Enabled && j.Health != HealthState.Error && (j.Health == HealthState.Warning || j.IsOutdated));
        var parts = new List<string>
        {
            string.Format(culture, _getString("Home_Overall_JobsFormat"), status.Jobs.Count),
        };
        if (running > 0)
        {
            parts.Add(string.Format(culture, _getString("Home_Overall_RunningFormat"), running));
        }

        if (failed > 0)
        {
            parts.Add(string.Format(culture, _getString("Home_Overall_FailedFormat"), failed));
        }

        if (warned > 0)
        {
            parts.Add(string.Format(culture, _getString("Home_Overall_WarnedFormat"), warned));
        }

        if (status.Pause is not null)
        {
            parts.Add(_getString("Home_Overall_Paused"));
        }

        (OverallTitle, OverallGlyph) = status.Health switch
        {
            HealthState.Error => (_getString("Home_Overall_Error"), GlyphError),
            HealthState.Warning => (_getString("Home_Overall_Warning"), GlyphWarning),
            _ when running > 0 => (_getString("Home_Overall_Copying"), GlyphCopying),
            _ => (_getString("Home_Overall_Ok"), GlyphOk),
        };
        OverallDetail = string.Join(" ", parts);
    }
}
