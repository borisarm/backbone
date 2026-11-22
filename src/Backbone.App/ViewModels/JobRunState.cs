using System.Globalization;
using Backbone.App.Ipc;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamJsonRpc;

namespace Backbone.App.ViewModels;

/// <summary>
/// Estado de ejecución de un trabajo en la interfaz (HU-14): botones Copiar ahora y Cancelar, "Cancelando…" y progreso resumido.
/// Solo traduce eventos del servicio; no hay lógica de copia.
/// </summary>
internal sealed partial class JobRunState : ObservableObject
{
    private readonly IBackboneService _service;
    private readonly Func<string, string> _getString;
    private readonly BackboneErrorTexts _errorTexts;
    private bool _requestInFlight;

    public JobRunState(IBackboneService service, Func<string, string> getString, Guid jobId)
    {
        _service = service;
        _getString = getString;
        _errorTexts = new BackboneErrorTexts(getString);
        JobId = jobId;
        Notice = string.Empty;
        Error = string.Empty;
        ProgressText = string.Empty;
    }

    /// <summary>Trabajo.</summary>
    public Guid JobId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunNow), nameof(ShowRunNow), nameof(ShowCancel), nameof(CanCancel), nameof(IsActive))]
    public partial JobActivity Activity { get; set; }

    /// <summary>Se pidió cancelar y aún no ha llegado <c>RunCompleted</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel), nameof(CancelText))]
    public partial bool IsCancelling { get; set; }

    /// <summary>Fase y archivos/bytes procesados; vacío si no hay ejecución.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress))]
    public partial string ProgressText { get; set; }

    /// <summary>Aviso suave (p. ej. "ya en marcha").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string Notice { get; set; }

    /// <summary>Error de la última petición.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; }

    /// <summary>El trabajo está en cola o en ejecución.</summary>
    public bool IsActive => Activity != JobActivity.Idle;

    /// <summary>Copiar ahora habilitado: sin ejecución ni en cola.</summary>
    public bool CanRunNow => Activity == JobActivity.Idle;

    /// <summary>Copiar ahora visible (el botón es Cancelar mientras hay ejecución o cola).</summary>
    public bool ShowRunNow => Activity == JobActivity.Idle;

    /// <summary>Cancelar visible mientras el trabajo está en cola o en ejecución.</summary>
    public bool ShowCancel => IsActive;

    /// <summary>Cancelar habilitado: visible y sin una cancelación en curso.</summary>
    public bool CanCancel => IsActive && !IsCancelling;

    /// <summary>Texto del botón Cancelar.</summary>
    public string CancelText => _getString(IsCancelling ? "Run_Cancelling" : "Run_Cancel");

    /// <summary>Hay texto de progreso.</summary>
    public bool HasProgress => ProgressText.Length > 0;

    /// <summary>Hay un aviso.</summary>
    public bool HasNotice => Notice.Length > 0;

    /// <summary>Hay un error.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Aplica el estado del trabajo recibido en <c>GetStatus</c> o <c>StatusChanged</c>.</summary>
    /// <param name="job">Estado del trabajo.</param>
    /// <param name="current">Ejecución en curso global, si la hay.</param>
    public void ApplyStatus(JobStatus job, RunProgressEvent? current)
    {
        ArgumentNullException.ThrowIfNull(job);
        Activity = job.Activity;
        if (job.Activity == JobActivity.Idle)
        {
            Reset();
        }
        else if (job.Activity == JobActivity.Running && current is { } run && run.JobId == job.JobId)
        {
            ApplyProgress(run);
        }
        else if (job.Activity == JobActivity.Queued)
        {
            ProgressText = string.Empty;
        }
    }

    /// <summary>Aplica un <c>RunProgress</c> de este trabajo.</summary>
    /// <param name="e">Evento.</param>
    public void ApplyProgress(RunProgressEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var phase = _getString("Run_Phase_" + e.Phase);
        ProgressText = e.Phase == RunPhase.Copying || e.FilesProcessed > 0 || e.BytesProcessed > 0
            ? string.Format(CultureInfo.CurrentCulture, _getString("Run_ProgressFormat"), phase, e.FilesProcessed, JobEditorViewModel.FormatBytes(e.BytesProcessed))
            : phase;
    }

    /// <summary>La ejecución terminó (<c>RunCompleted</c>): vuelve a reposo.</summary>
    public void Complete()
    {
        Activity = JobActivity.Idle;
        Reset();
    }

    /// <summary>Lanza o encola la ejecución. <c>JobAlreadyRunning</c> es un aviso, no un error.</summary>
    /// <returns>Una tarea que termina al responder el servicio.</returns>
    [RelayCommand]
    public async Task RunNowAsync()
    {
        if (_requestInFlight || !CanRunNow)
        {
            return;
        }

        _requestInFlight = true;
        Notice = string.Empty;
        Error = string.Empty;
        try
        {
            await _service.RunNowAsync(JobId).ConfigureAwait(true);
        }
        catch (RemoteInvocationException ex) when (ConnectionFailures.TryGetErrorCode(ex, out var code, out _) && code == BackboneErrorCode.JobAlreadyRunning)
        {
            Notice = _getString("Run_AlreadyRunning");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = _errorTexts.Describe(ex);
        }
        finally
        {
            _requestInFlight = false;
        }
    }

    /// <summary>Pide cancelar; muestra "Cancelando…" hasta que llega <c>RunCompleted</c>.</summary>
    /// <returns>Una tarea que termina al responder el servicio.</returns>
    [RelayCommand]
    public async Task CancelAsync()
    {
        if (!CanCancel)
        {
            return;
        }

        Notice = string.Empty;
        Error = string.Empty;
        IsCancelling = true;
        try
        {
            await _service.CancelRunAsync(JobId).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsCancelling = false;
            Error = _errorTexts.Describe(ex);
        }
    }

    private void Reset()
    {
        IsCancelling = false;
        ProgressText = string.Empty;
    }
}

/// <summary>Mantiene un <see cref="JobRunState"/> por trabajo a partir de los eventos del servicio.</summary>
internal sealed class JobRunTracker : IDisposable
{
    private readonly IBackboneService _service;
    private readonly Func<string, string> _getString;
    private readonly Dictionary<Guid, JobRunState> _states = [];

    public JobRunTracker(IBackboneService service, Func<string, string> getString)
    {
        _service = service;
        _getString = getString;
        _service.RunProgress += OnRunProgress;
        _service.RunCompleted += OnRunCompleted;
    }

    /// <summary>Estado de un trabajo (se crea la primera vez).</summary>
    /// <param name="jobId">Trabajo.</param>
    /// <returns>El estado compartido.</returns>
    public JobRunState Get(Guid jobId)
    {
        if (!_states.TryGetValue(jobId, out var state))
        {
            state = new JobRunState(_service, _getString, jobId);
            _states[jobId] = state;
        }

        return state;
    }

    /// <summary>Actualiza todos los trabajos con un estado del servicio.</summary>
    /// <param name="status">Estado.</param>
    public void Apply(ServiceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        foreach (var job in status.Jobs)
        {
            Get(job.JobId).ApplyStatus(job, status.CurrentRun);
        }
    }

    public void Dispose()
    {
        _service.RunProgress -= OnRunProgress;
        _service.RunCompleted -= OnRunCompleted;
    }

    private void OnRunProgress(object? sender, RunProgressEvent e) => Get(e.JobId).ApplyProgress(e);

    private void OnRunCompleted(object? sender, RunCompletedEvent e) => Get(e.Run.JobId).Complete();
}
