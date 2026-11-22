using System.Collections.ObjectModel;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Backbone.App.ViewModels;

/// <summary>Un trabajo de la lista de Trabajos.</summary>
/// <param name="Job">Definición guardada en el servicio.</param>
/// <param name="Summary">Resumen de una línea (carpetas y destino).</param>
/// <param name="IsReadOnly">El destino tiene un formato más nuevo (RNF-14): se muestra el aviso de solo lectura.</param>
internal sealed record JobListItem(JobDefinition Job, string Summary, bool IsReadOnly = false)
{
    /// <summary>Nombre del trabajo.</summary>
    public string Name => Job.Name;
}

/// <summary>Página Trabajos (§9.2): lista de trabajos y editor.</summary>
internal sealed partial class JobsViewModel : ObservableObject, IDisposable
{
    private readonly IBackboneService _service;
    private readonly IFolderPicker _picker;
    private readonly Func<string, string> _getString;
    private readonly BackboneErrorTexts _errorTexts;
    private readonly Dictionary<Guid, RepositoryState> _repositoryStates = [];
    private int _loadVersion;

    public JobsViewModel(IBackboneService service, IFolderPicker picker, Func<string, string> getString)
    {
        _service = service;
        _picker = picker;
        _getString = getString;
        _errorTexts = new BackboneErrorTexts(getString);
        Error = string.Empty;
        _service.StatusChanged += OnStatusChanged;
    }

    /// <summary>Trabajos guardados.</summary>
    public ObservableCollection<JobListItem> Jobs { get; } = [];

    /// <summary>Editor abierto, o <see langword="null"/> si se ve la lista.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsListVisible))]
    public partial JobEditorViewModel? Editor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoaded { get; set; }

    /// <summary>Hay un editor abierto.</summary>
    public bool IsEditing => Editor is not null;

    /// <summary>Se ve la lista.</summary>
    public bool IsListVisible => Editor is null;

    /// <summary>La lista ya se leyó y no hay trabajos.</summary>
    public bool IsEmpty => IsLoaded && Jobs.Count == 0;

    /// <summary>Hay un error de lectura.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Se produce al guardar un trabajo, tras actualizar la lista.</summary>
    public event EventHandler<JobDefinition>? JobSaved;

    /// <summary>Lee los trabajos del servicio. Llamar al conectar y tras reconectar.</summary>
    /// <param name="ct">Cancelación.</param>
    /// <returns>Una tarea que termina al actualizar la lista.</returns>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        var version = ++_loadVersion;
        try
        {
            var jobs = await _service.GetJobsAsync(ct).ConfigureAwait(true);
            await LoadRepositoryStatesAsync(ct).ConfigureAwait(true);
            if (version != _loadVersion)
            {
                return;
            }

            Jobs.Clear();
            foreach (var job in jobs)
            {
                Jobs.Add(new JobListItem(job, Summarize(job), IsReadOnly(job.Id)));
            }

            IsLoaded = true;
            Error = string.Empty;
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _loadVersion)
            {
                Error = _errorTexts.Describe(ex);
            }
        }
    }

    /// <summary>Abre el editor con un trabajo nuevo.</summary>
    [RelayCommand]
    public void NewJob() => Open(null);

    /// <summary>Abre el editor con un trabajo existente.</summary>
    /// <param name="item">Trabajo de la lista.</param>
    [RelayCommand]
    public void EditJob(JobListItem? item)
    {
        if (item is not null)
        {
            Open(item.Job);
        }
    }

    public void Dispose()
    {
        _service.StatusChanged -= OnStatusChanged;
        Close();
    }

    private void Open(JobDefinition? job)
    {
        Close();
        var editor = new JobEditorViewModel(_service, _picker, _getString, job);
        editor.Saved += OnEditorSaved;
        editor.Cancelled += OnEditorCancelled;
        Editor = editor;
    }

    private void Close()
    {
        if (Editor is { } editor)
        {
            editor.Saved -= OnEditorSaved;
            editor.Cancelled -= OnEditorCancelled;
            Editor = null;
            editor.Dispose();
        }
    }

    private async void OnEditorSaved(object? sender, JobDefinition saved)
    {
        Close();
        await LoadAsync().ConfigureAwait(true);
        JobSaved?.Invoke(this, saved);
    }

    private void OnEditorCancelled(object? sender, EventArgs e) => Close();

    // El servicio emite StatusChanged al guardar o borrar un trabajo (también desde otra app): se vuelve a leer la lista.
    private async void OnStatusChanged(object? sender, StatusChangedEvent e)
    {
        StoreStates(e.Status);
        for (var i = 0; i < Jobs.Count; i++)
        {
            var readOnly = IsReadOnly(Jobs[i].Job.Id);
            if (Jobs[i].IsReadOnly != readOnly)
            {
                Jobs[i] = Jobs[i] with { IsReadOnly = readOnly };
            }
        }

        if (!IsLoaded || Jobs.Count != e.Status.Jobs.Count || e.Status.Jobs.Any(s => !Jobs.Any(j => j.Job.Id == s.JobId && j.Job.Name == s.JobName)))
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private bool IsReadOnly(Guid jobId) => _repositoryStates.TryGetValue(jobId, out var state) && state == RepositoryState.ReadOnlyNewerFormat;

    private void StoreStates(ServiceStatus status)
    {
        _repositoryStates.Clear();
        foreach (var job in status.Jobs)
        {
            _repositoryStates[job.JobId] = job.RepositoryState;
        }
    }

    // El estado del repositorio llega en GetStatus; si falla, la lista se muestra sin aviso (no es un error de la lista).
    private async Task LoadRepositoryStatesAsync(CancellationToken ct)
    {
        try
        {
            StoreStates(await _service.GetStatusAsync(ct).ConfigureAwait(true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _repositoryStates.Clear();
        }
    }

    private string Summarize(JobDefinition job) =>
        string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _getString("Jobs_SummaryFormat"),
            job.Sources.Count,
            job.Destination);
}
