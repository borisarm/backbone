using System.Collections.ObjectModel;
using System.Globalization;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backbone.App.ViewModels;

/// <summary>Tarjeta de un trabajo en Inicio (§9.2). Los botones Copiar ahora y el progreso llegan con HU-10 y HU-14.</summary>
/// <param name="JobId">Identificador.</param>
/// <param name="Name">Nombre.</param>
/// <param name="Summary">Estado en una línea.</param>
/// <param name="Run">Estado de ejecución: Copiar ahora, Cancelar y progreso (HU-14).</param>
/// <param name="IsReadOnly">El destino tiene un formato más nuevo (RNF-14): se muestra el aviso de solo lectura.</param>
internal sealed record JobCard(Guid JobId, string Name, string Summary, JobRunState Run, bool IsReadOnly = false);

/// <summary>Página Inicio (§9.2): una tarjeta por trabajo y estado vacío si no hay ninguno (§9.3).</summary>
internal sealed partial class HomeViewModel : ObservableObject, IDisposable
{
    private readonly IBackboneService _service;
    private readonly Func<string, string> _getString;
    private readonly BackboneErrorTexts _errorTexts;
    private readonly JobRunTracker _tracker;
    private int _version;

    public HomeViewModel(IBackboneService service, Func<string, string> getString)
    {
        _service = service;
        _getString = getString;
        _errorTexts = new BackboneErrorTexts(getString);
        Error = string.Empty;
        _tracker = new JobRunTracker(service, getString);
        _service.StatusChanged += OnStatusChanged;
    }

    /// <summary>Una tarjeta por trabajo.</summary>
    public ObservableCollection<JobCard> Cards { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; }

    /// <summary>La lectura terminó y no hay trabajos: estado vacío con botón para crear uno.</summary>
    public bool IsEmpty => IsLoaded && Cards.Count == 0;

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
    }

    [RelayCommand]
    private void CreateJob() => CreateJobRequested?.Invoke(this, EventArgs.Empty);

    private void OnStatusChanged(object? sender, StatusChangedEvent e)
    {
        _version++;
        Apply(e.Status);
        Error = string.Empty;
    }

    private void Apply(ServiceStatus status)
    {
        _tracker.Apply(status);
        Cards.Clear();
        foreach (var job in status.Jobs)
        {
            Cards.Add(new JobCard(job.JobId, job.JobName, Describe(job), _tracker.Get(job.JobId), job.RepositoryState == RepositoryState.ReadOnlyNewerFormat));
        }

        IsLoaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private string Describe(JobStatus job)
    {
        if (job.Activity == JobActivity.Running)
        {
            return _getString("Home_Job_Running");
        }

        if (job.Activity == JobActivity.Queued)
        {
            return _getString("Home_Job_Queued");
        }

        return job.LastSuccessUtc is { } last
            ? string.Format(CultureInfo.CurrentCulture, _getString("Home_Job_LastSuccessFormat"), last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
            : _getString("Home_Job_NeverRun");
    }
}
