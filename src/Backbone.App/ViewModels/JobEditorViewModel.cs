using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Backbone.App.Ipc;
using Backbone.App.Validation;
using Backbone.Contracts;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamJsonRpc;

namespace Backbone.App.ViewModels;

/// <summary>
/// Editor de un trabajo (HU-07, RF-01 a RF-03; §9.2 "Trabajos"). Solo es interfaz: la validación la hace el servicio con
/// <see cref="IBackboneService.ValidateJobAsync"/> y aquí se reparte cada problema junto a su campo; la app no repite reglas.
/// </summary>
internal sealed partial class JobEditorViewModel : ObservableObject, IDisposable
{
    private static readonly Regex s_sourceField = new(@"^Sources\[(\d+)\]\.(Path|Alias)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IBackboneService _service;
    private readonly IFolderPicker _picker;
    private readonly Func<string, string> _getString;
    private readonly ValidationIssueTexts _issueTexts;
    private readonly BackboneErrorTexts _errorTexts;
    private readonly JobDefinition _original;
    private int _validationVersion;
    private CancellationTokenSource? _operation;
    private bool _committing;

    /// <summary>Crea el editor de un trabajo nuevo (<paramref name="existing"/> nulo) o de uno existente.</summary>
    public JobEditorViewModel(IBackboneService service, IFolderPicker picker, Func<string, string> getString, JobDefinition? existing = null)
    {
        _service = service;
        _picker = picker;
        _getString = getString;
        _issueTexts = new ValidationIssueTexts(getString);
        _errorTexts = new BackboneErrorTexts(getString);
        _original = existing ?? JobDefaults.NewJob();
        IsNew = existing is null;
        Name = _original.Name;
        Destination = _original.Destination;
        UserName = Password = NameError = SourcesError = DestinationError = GeneralError = ConnectionMessage = string.Empty;
        foreach (var source in _original.Sources)
        {
            Sources.Add(new SourceItemViewModel(source.Path, source.Alias));
        }

        LoadSchedule(_original.Schedule);
    }

    /// <summary>Cancela cualquier llamada en curso y libera sus recursos.</summary>
    public void Dispose()
    {
        Password = string.Empty;
        if (!_committing)
        {
            _operation?.Cancel();
        }

        _operation?.Dispose();
        _operation = null;
    }

    /// <summary>Trabajo nuevo (aún sin identificador).</summary>
    public bool IsNew { get; }

    /// <summary>Carpetas de origen.</summary>
    public ObservableCollection<SourceItemViewModel> Sources { get; } = [];

    /// <summary>Avisos que no están junto a un origen (p. ej. repositorio de formato superior).</summary>
    public ObservableCollection<string> Warnings { get; } = [];

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Destination { get; set; }

    [ObservableProperty]
    public partial string UserName { get; set; }

    /// <summary>Contraseña escrita en el <c>PasswordBox</c>; se vacía al guardar la credencial, al cerrar el editor (cancelar o liberar) y nunca se muestra ni se registra.</summary>
    [ObservableProperty]
    public partial string Password { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    public partial string NameError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourcesError))]
    public partial string SourcesError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDestinationError))]
    public partial string DestinationError { get; set; }

    /// <summary>Errores sin campo propio (entrada entera, campos que este editor no muestra, fallos de la llamada).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGeneralError))]
    public partial string GeneralError { get; set; }

    /// <summary>Resultado de "Probar conexión", con texto localizado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConnectionMessage))]
    public partial string ConnectionMessage { get; set; }

    /// <summary>La última prueba de conexión fue correcta.</summary>
    [ObservableProperty]
    public partial bool ConnectionSucceeded { get; set; }

    /// <summary>Hay una llamada al servicio en curso.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestConnectionCommand), nameof(AddFolderCommand), nameof(ValidateCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Se muestra la nota fija de que la carpeta de datos de Backbone se excluye siempre (§9.2).</summary>
    [ObservableProperty]
    public partial bool ShowDataFolderNote { get; set; }

    /// <summary>Hay avisos que no están junto a un origen.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Error junto al nombre.</summary>
    public bool HasNameError => NameError.Length > 0;

    /// <summary>Error junto a la lista de orígenes.</summary>
    public bool HasSourcesError => SourcesError.Length > 0;

    /// <summary>Error junto al destino.</summary>
    public bool HasDestinationError => DestinationError.Length > 0;

    /// <summary>Hay errores generales.</summary>
    public bool HasGeneralError => GeneralError.Length > 0;

    /// <summary>Hay un resultado de la prueba de conexión.</summary>
    public bool HasConnectionMessage => ConnectionMessage.Length > 0;

    /// <summary>No hay ninguna llamada en curso.</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>Se produce al guardar el trabajo, con el trabajo que devuelve el servicio.</summary>
    public event EventHandler<JobDefinition>? Saved;

    /// <summary>Se produce cuando el usuario cancela la edición.</summary>
    public event EventHandler? Cancelled;

    /// <summary>Trabajo tal como está en el editor (identificador, exclusiones, programación… se conservan del original).</summary>
    public JobDefinition BuildJob() => _original with
    {
        Name = Name.Trim(),
        Destination = Destination.Trim(),
        Sources = [.. Sources.Select(s => new JobSource { Path = s.Path, Alias = s.Alias.Trim() })],
        Schedule = BuildSchedule(),
    };

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task AddFolderAsync()
    {
        string? path;
        try
        {
            path = await _picker.PickFolderAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            GeneralError = _errorTexts.Translate(BackboneErrorCode.Internal);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Sources.Add(new SourceItemViewModel(path, AliasSuggester.Suggest(path)));
        SourcesError = string.Empty;
        await ValidateAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void RemoveSource(SourceItemViewModel? source)
    {
        if (source is not null && Sources.Remove(source))
        {
            ClearMessages();
        }
    }

    /// <summary>Valida con el servicio y reparte los problemas junto a sus campos.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task ValidateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var version = ++_validationVersion;
        try
        {
            var ct = BeginOperation();
            var result = await _service.ValidateJobAsync(BuildJob(), ct).ConfigureAwait(true);
            if (version == _validationVersion)
            {
                ApplyIssues(result);
            }
        }
        catch (OperationCanceledException)
        {
            // El usuario canceló la validación: no es un error.
        }
        catch (Exception ex)
        {
            if (version == _validationVersion)
            {
                ApplyFailure(ex);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Valida y, si no hay errores, guarda la credencial escrita (si la hay) y el trabajo.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task SaveAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _validationVersion++;
        try
        {
            var ct = BeginOperation();
            var job = BuildJob();
            var result = await _service.ValidateJobAsync(job, ct).ConfigureAwait(true);
            ApplyIssues(result);
            if (!result.IsValid)
            {
                return;
            }

            var credential = TypedCredential();
            string? shareRoot = null;
            if (credential is not null && !UncPaths.TryGetShareRoot(job.Destination, out shareRoot))
            {
                // El servicio dio por válido un destino del que no se saca el recurso: no se guarda nada.
                GeneralError = _errorTexts.Translate(BackboneErrorCode.Internal);
                return;
            }

            // Desde aquí Cancelar queda desactivado y las llamadas no se cancelan: evita una credencial sin trabajo o un
            // trabajo guardado dos veces al reintentar tras una cancelación cuando el servicio ya lo guardó.
            _committing = true;
            CancelCommand.NotifyCanExecuteChanged();
            if (credential is not null && shareRoot is not null)
            {
                await _service.SaveCredentialAsync(shareRoot, credential, CancellationToken.None).ConfigureAwait(true);
                Password = string.Empty;
            }

            var saved = await _service.SaveJobAsync(job, CancellationToken.None).ConfigureAwait(true);
            Password = string.Empty;
            Saved?.Invoke(this, saved);
        }
        catch (OperationCanceledException)
        {
            // El usuario canceló: no se guarda nada más.
        }
        catch (Exception ex)
        {
            ApplyFailure(ex);
        }
        finally
        {
            _committing = false;
            EndOperation();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"Probar conexión" (RF-61): usa la credencial escrita o, si no hay, la guardada para el recurso.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task TestConnectionAsync()
    {
        ConnectionMessage = string.Empty;
        ConnectionSucceeded = false;
        DestinationError = string.Empty;
        try
        {
            var ct = BeginOperation();
            var result = await _service.TestConnectionAsync(Destination.Trim(), TypedCredential(), ct).ConfigureAwait(true);
            ConnectionSucceeded = result.Success;
            ConnectionMessage = result.Success
                ? DescribeSuccess(result)
                : _errorTexts.Translate(result.ErrorCode ?? BackboneErrorCode.Internal);
        }
        catch (RemoteInvocationException ex) when (ConnectionFailures.TryGetErrorCode(ex, out var code, out var data)
                                                   && code == BackboneErrorCode.ValidationFailed)
        {
            // Destino que no es UNC: es un error de validación del campo Destino.
            ApplyIssues(data?.Validation ?? new ValidationResult());
            ConnectionMessage = string.Empty;
        }
        catch (OperationCanceledException)
        {
            // El usuario canceló la prueba.
        }
        catch (Exception ex)
        {
            ConnectionMessage = _errorTexts.Describe(ex);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Con una llamada en curso la cancela (la validación puede tardar hasta 60 s); si no, cierra el editor.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_operation is { } operation)
        {
            operation.Cancel();
            return;
        }

        Password = string.Empty;
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    private bool CanCancel() => !_committing;

    private CancellationToken BeginOperation()
    {
        _operation = new CancellationTokenSource();
        IsBusy = true;
        return _operation.Token;
    }

    private void EndOperation()
    {
        _operation?.Dispose();
        _operation = null;
        IsBusy = false;
    }

    private CredentialInput? TypedCredential() =>
        UserName.Trim().Length > 0 && Password.Length > 0
            ? new CredentialInput { UserName = UserName.Trim(), Password = Password }
            : null;

    private string DescribeSuccess(ConnectionTestResult result)
    {
        var parts = new List<string> { _getString("Connection_Success") };
        if (result.FreeBytes is { } free && result.TotalBytes is { } total)
        {
            parts.Add(string.Format(CultureInfo.CurrentCulture, _getString("Connection_SpaceFormat"), FormatBytes(free), FormatBytes(total)));
        }

        if (!string.IsNullOrWhiteSpace(result.SmbDialect))
        {
            // El dialecto lo informa el servidor del NAS: texto no fiable (§6.1, HU-04).
            parts.Add(string.Format(CultureInfo.CurrentCulture, _getString("Connection_SmbFormat"), UntrustedText.Describe(result.SmbDialect)));
        }

        return string.Join(' ', parts);
    }

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{(unit == 0 ? value.ToString("0", CultureInfo.CurrentCulture) : value.ToString("0.#", CultureInfo.CurrentCulture))} {units[unit]}");
    }

    private void ApplyFailure(Exception ex)
    {
        if (ex is RemoteInvocationException rpc
            && ConnectionFailures.TryGetErrorCode(rpc, out var code, out var data)
            && code == BackboneErrorCode.ValidationFailed
            && data?.Validation is { } validation)
        {
            ApplyIssues(validation);
            return;
        }

        GeneralError = _errorTexts.Describe(ex);
    }

    private void ClearMessages()
    {
        NameError = SourcesError = DestinationError = GeneralError = string.Empty;
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        ShowDataFolderNote = false;
        ClearScheduleMessages();
        foreach (var source in Sources)
        {
            source.ClearMessages();
        }
    }

    private void ApplyIssues(ValidationResult result)
    {
        ClearMessages();
        var general = new List<string>();
        foreach (var issue in result.Errors)
        {
            var text = _issueTexts.Translate(issue);
            if (issue.Field == "Name")
            {
                NameError = Join(NameError, text);
            }
            else if (issue.Field == "Sources")
            {
                SourcesError = Join(SourcesError, text);
            }
            else if (issue.Field == "Destination")
            {
                DestinationError = Join(DestinationError, text);
            }
            else if (TryApplyScheduleIssue(issue.Field, text))
            {
                // Mostrado junto al campo de la programación.
            }
            else if (TryGetSource(issue.Field, out var source, out var isPath))
            {
                if (isPath)
                {
                    source.PathError = Join(source.PathError, text);
                }
                else
                {
                    source.AliasError = Join(source.AliasError, text);
                }
            }
            else
            {
                general.Add(text);
            }
        }

        GeneralError = string.Join(Environment.NewLine, general);

        foreach (var issue in result.Warnings)
        {
            var text = _issueTexts.Translate(issue);
            if (issue.Code == ValidationIssueCode.SourceContainsDataFolder)
            {
                ShowDataFolderNote = true;
            }

            if (TryGetSource(issue.Field, out var source, out var isPath) && isPath)
            {
                source.PathWarning = Join(source.PathWarning, text);
            }
            else
            {
                Warnings.Add(text);
            }
        }

        OnPropertyChanged(nameof(HasWarnings));
    }

    private bool TryGetSource(string field, [NotNullWhen(true)] out SourceItemViewModel? source, out bool isPath)
    {
        source = null;
        isPath = false;
        var match = s_sourceField.Match(field);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            || index >= Sources.Count)
        {
            return false;
        }

        source = Sources[index];
        isPath = match.Groups[2].Value == "Path";
        return true;
    }

    private static string Join(string current, string text) => current.Length == 0 ? text : current + Environment.NewLine + text;
}
