using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

internal sealed class FakePicker : IFolderPicker
{
    public Queue<string?> Picks { get; } = new();

    public Task<string?> PickFolderAsync(CancellationToken ct = default) => Task.FromResult(Picks.Count > 0 ? Picks.Dequeue() : null);
}

/// <summary>HU-07: editor de trabajos. El texto de cada cadena es su clave (getString = identidad).</summary>
public sealed class JobEditorTests
{
    private static string Key(string k) => k;

    private static (JobEditorViewModel Editor, FakeBackboneService Fake, FakePicker Picker) Create(ValidationResult? validation = null)
    {
        var (service, fake) = FakeBackboneService.Create();
        var picker = new FakePicker();
        fake.On("ValidateJobAsync", _ => Task.FromResult(validation ?? new ValidationResult()));
        fake.On("SaveJobAsync", a => Task.FromResult(((JobDefinition)a[0]!) with { Id = Guid.NewGuid() }));
        return (new JobEditorViewModel(service, picker, Key), fake, picker);
    }

    private static ValidationIssue Issue(string field, ValidationIssueCode code) => new() { Field = field, Code = code };

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion1_SavesJobAndRaisesSavedWithServiceResult()
    {
        var (editor, fake, picker) = Create();
        picker.Picks.Enqueue(@"C:\Datos\Fotos");
        editor.Name = " Mis fotos ";
        editor.Destination = @"\\nas\backups\fotos";
        await editor.AddFolderCommand.ExecuteAsync(null);
        JobDefinition? saved = null;
        editor.Saved += (_, j) => saved = j;

        await editor.SaveAsync();

        Assert.NotNull(saved);
        Assert.Equal("Mis fotos", saved.Name);
        Assert.Equal(@"C:\Datos\Fotos", Assert.Single(saved.Sources).Path);
        Assert.Contains("SaveJobAsync", fake.Calls);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion1_DoesNotSaveWhenServiceReportsErrors()
    {
        var (editor, fake, _) = Create(new ValidationResult { Errors = [Issue("Name", ValidationIssueCode.NameRequired)] });

        await editor.SaveAsync();

        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
        Assert.Equal("Validation_NameRequired", editor.NameError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion1_ShowsSavedPathAsReturnedByService()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("ValidateJobAsync", _ => Task.FromResult(new ValidationResult()));
        fake.On("SaveJobAsync", a => Task.FromResult(((JobDefinition)a[0]!) with
        {
            Id = Guid.NewGuid(),
            Sources = [new JobSource { Path = @"\\?\C:\Datos\Fotos", Alias = "Fotos" }],
        }));
        var editor = new JobEditorViewModel(service, new FakePicker(), Key);
        JobDefinition? saved = null;
        editor.Saved += (_, j) => saved = j;

        await editor.SaveAsync();

        Assert.Equal(@"\\?\C:\Datos\Fotos", Assert.Single(saved!.Sources).Path);
    }

    [Theory]
    [Trait("Story", "HU-07")]
    [InlineData(@"C:\Datos\Fotos", "Fotos")]
    [InlineData(@"C:\Datos\Fotos\", "Fotos")]
    [InlineData(@"C:\", "C")]
    [InlineData(@"D:\Mi: carpeta?", "Mi carpeta")]
    [InlineData("", "Carpeta")]
    public void Criterion2_AliasIsProposedFromFolderName(string path, string expected) =>
        Assert.Equal(expected, AliasSuggester.Suggest(path));

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion2_ProposedAliasIsEditableAndDuplicateShowsInlineError()
    {
        var (editor, _, picker) = Create(new ValidationResult { Errors = [Issue("Sources[1].Alias", ValidationIssueCode.AliasDuplicate)] });
        picker.Picks.Enqueue(@"C:\A\Datos");
        picker.Picks.Enqueue(@"C:\B\Datos");
        await editor.AddFolderCommand.ExecuteAsync(null);
        await editor.AddFolderCommand.ExecuteAsync(null);

        Assert.All(editor.Sources, s => Assert.Equal("Datos", s.Alias));
        editor.Sources[1].Alias = "Datos2";
        Assert.Equal("Datos2", editor.BuildJob().Sources[1].Alias);
        Assert.Equal(string.Empty, editor.Sources[0].AliasError);
        Assert.Equal("Validation_AliasDuplicate", editor.Sources[1].AliasError);
        Assert.True(editor.Sources[1].HasAliasError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion3_EachErrorAppearsNextToItsField()
    {
        var (editor, _, picker) = Create(new ValidationResult
        {
            Errors =
            [
                Issue("Sources[0].Path", ValidationIssueCode.SourceNotFound),
                Issue("Sources[1].Path", ValidationIssueCode.SourceNotNtfs),
                Issue("Sources[2].Path", ValidationIssueCode.SourceNotLocal),
                Issue("Destination", ValidationIssueCode.DestinationNotUnc),
                Issue("Sources", ValidationIssueCode.SourcesRequired),
            ],
        });
        foreach (var p in new[] { @"C:\a", @"D:\b", @"Z:\c" })
        {
            picker.Picks.Enqueue(p);
            await editor.AddFolderCommand.ExecuteAsync(null);
        }

        Assert.Equal("Validation_SourceNotFound", editor.Sources[0].PathError);
        Assert.Equal("Validation_SourceNotNtfs", editor.Sources[1].PathError);
        Assert.Equal("Validation_SourceNotLocal", editor.Sources[2].PathError);
        Assert.Equal("Validation_DestinationNotUnc", editor.DestinationError);
        Assert.Equal("Validation_SourcesRequired", editor.SourcesError);
        Assert.Equal(string.Empty, editor.GeneralError);
    }

    [Theory]
    [Trait("Story", "HU-07")]
    [InlineData(ValidationIssueCode.DestinationInsideSource)]
    [InlineData(ValidationIssueCode.DestinationUsedByOtherJob)]
    public async Task Criterion3_DestinationRulesShowOnDestination(ValidationIssueCode code)
    {
        var (editor, _, _) = Create(new ValidationResult { Errors = [Issue("Destination", code)] });

        await editor.ValidateAsync();

        Assert.Equal("Validation_" + code, editor.DestinationError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion4_AppDelegatesValidationToService()
    {
        var (editor, fake, _) = Create();
        editor.Name = "";
        editor.Destination = "no es una ruta";

        await editor.ValidateAsync();

        // Sin reglas propias: nombre vacío y destino no UNC se envían tal cual al servicio, que decide.
        Assert.Equal(["ValidateJobAsync"], fake.Calls);
        Assert.Equal(string.Empty, editor.NameError);
        Assert.Equal(string.Empty, editor.DestinationError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion4_ServiceFailureOnSaveIsShownAndEditorStaysUsable()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("ValidateJobAsync", _ => Task.FromResult(new ValidationResult()));
        fake.On("SaveJobAsync", _ => throw new ServiceUnavailableException());
        var editor = new JobEditorViewModel(service, new FakePicker(), Key);

        await editor.SaveAsync();

        Assert.Equal("Error_ServiceUnavailable", editor.GeneralError);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion6_SourceNotAllowedShowsErrorNextToSourceAndBlocksSave()
    {
        var (editor, fake, picker) = Create(new ValidationResult { Errors = [Issue("Sources[0].Path", ValidationIssueCode.SourceNotAllowed)] });
        picker.Picks.Enqueue(@"C:\ProgramData\Backbone\jobs");
        await editor.AddFolderCommand.ExecuteAsync(null);

        await editor.SaveAsync();

        Assert.Equal("Validation_SourceNotAllowed", editor.Sources[0].PathError);
        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion6_SourceContainingDataFolderSavesWithWarning()
    {
        var (editor, fake, picker) = Create(new ValidationResult { Warnings = [Issue("Sources[0].Path", ValidationIssueCode.SourceContainsDataFolder)] });
        picker.Picks.Enqueue(@"C:\");
        await editor.AddFolderCommand.ExecuteAsync(null);
        var saved = false;
        editor.Saved += (_, _) => saved = true;

        await editor.SaveAsync();

        Assert.True(saved);
        Assert.Contains("SaveJobAsync", fake.Calls);
        Assert.Equal("Validation_SourceContainsDataFolder", editor.Sources[0].PathWarning);
        Assert.True(editor.ShowDataFolderNote);
        Assert.Equal(string.Empty, editor.Sources[0].PathError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Criterion6_UnknownIssueCodeFromNewerServiceUsesGenericText()
    {
        var (editor, _, _) = Create(new ValidationResult { Errors = [Issue("Name", (ValidationIssueCode)9999)] });

        await editor.ValidateAsync();

        Assert.Equal("Validation_Other", editor.NameError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Validation_ShowsProgressAndCanBeCancelledWithoutBlockingUi()
    {
        var (service, fake) = FakeBackboneService.Create();
        var started = new TaskCompletionSource();
        fake.On("ValidateJobAsync", a => Wait(started, (CancellationToken)a[1]!));
        var editor = new JobEditorViewModel(service, new FakePicker(), Key);

        var run = editor.ValidateAsync();
        await started.Task;
        Assert.True(editor.IsBusy);
        Assert.False(editor.SaveCommand.CanExecute(null));

        editor.CancelCommand.Execute(null);
        await run;

        Assert.False(editor.IsBusy);
        Assert.Equal(string.Empty, editor.GeneralError);
        Assert.True(editor.SaveCommand.CanExecute(null));

        static async Task<ValidationResult> Wait(TaskCompletionSource started, CancellationToken ct)
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ValidationResult();
        }
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public void Cancel_WhenIdleClosesEditor()
    {
        var (editor, _, _) = Create();
        var cancelled = false;
        editor.Cancelled += (_, _) => cancelled = true;

        editor.CancelCommand.Execute(null);

        Assert.True(cancelled);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_SendsTypedCredentialForShareRootAndClearsPassword()
    {
        var (editor, fake, _) = Create();
        string? root = null;
        fake.On("SaveCredentialAsync", a =>
        {
            root = (string)a[0]!;
            return Task.CompletedTask;
        });
        editor.Destination = @"\\nas\backups\fotos";
        editor.UserName = "ana";
        editor.Password = "secreto";

        await editor.SaveAsync();

        Assert.Equal(@"\\nas\backups", root);
        Assert.Equal(string.Empty, editor.Password);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_CancelDuringSavePhase_IsDisabledAndSavesOnceWithCredential()
    {
        var (editor, fake, _) = Create();
        var inSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveCalls = 0;
        var credentialCalls = 0;
        CancellationToken saveToken = default;
        fake.On("SaveCredentialAsync", _ =>
        {
            credentialCalls++;
            return Task.CompletedTask;
        });
        fake.On("SaveJobAsync", a =>
        {
            saveCalls++;
            saveToken = (CancellationToken)a[1]!;
            inSave.SetResult();
            return Hold((JobDefinition)a[0]!);
        });
        async Task<JobDefinition> Hold(JobDefinition job)
        {
            await release.Task;
            return job;
        }
        editor.Destination = @"\\nas\backups\fotos";
        editor.UserName = "ana";
        editor.Password = "pw";

        var run = editor.SaveAsync();
        await inSave.Task;
        Assert.False(editor.CancelCommand.CanExecute(null));
        Assert.False(saveToken.CanBeCanceled);
        Assert.Equal(string.Empty, editor.Password);
        release.SetResult();
        await run;

        Assert.Equal(1, saveCalls);
        Assert.Equal(1, credentialCalls);
        Assert.True(editor.CancelCommand.CanExecute(null));
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public void Cancel_ClearsPassword()
    {
        var (editor, _, _) = Create();
        editor.Password = "pw";
        var closed = false;
        editor.Cancelled += (_, _) => closed = true;

        editor.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Equal(string.Empty, editor.Password);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public void Dispose_ClearsPassword()
    {
        var (editor, _, _) = Create();
        editor.Password = "pw";

        editor.Dispose();

        Assert.Equal(string.Empty, editor.Password);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_CredentialWithoutShareRoot_ShowsInternalAndSavesNothing()
    {
        var (editor, fake, _) = Create();
        editor.Destination = "no-unc";
        editor.UserName = "ana";
        editor.Password = "pw";

        await editor.SaveAsync();

        Assert.Equal("Error_Internal", editor.GeneralError);
        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
        Assert.DoesNotContain("SaveCredentialAsync", fake.Calls);
    }
}
