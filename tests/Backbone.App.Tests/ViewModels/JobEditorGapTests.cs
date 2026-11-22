using Backbone.App.Ipc;
using Backbone.App.Tests.Ipc;
using Backbone.App.ViewModels;
using Backbone.Contracts;
using StreamJsonRpc;
using Xunit;

namespace Backbone.App.Tests.ViewModels;

/// <summary>HU-07: huecos de la auditoría de QA (cancelaciones, fallos del servicio, reparto de problemas, textos).</summary>
public sealed class JobEditorGapTests
{
    private static string Key(string k) => k;

    private static ValidationIssue Issue(string field, ValidationIssueCode code) => new() { Field = field, Code = code };

    private static (JobEditorViewModel Editor, FakeBackboneService Fake, FakePicker Picker) Create(Func<ValidationResult> validation)
    {
        var (service, fake) = FakeBackboneService.Create();
        var picker = new FakePicker();
        fake.On("ValidateJobAsync", _ => Task.FromResult(validation()));
        fake.On("SaveJobAsync", a => Task.FromResult(((JobDefinition)a[0]!) with { Id = Guid.NewGuid() }));
        fake.On("SaveCredentialAsync", _ => Task.CompletedTask);
        return (new JobEditorViewModel(service, picker, Key), fake, picker);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_CancelledDuringValidation_SavesNothingAndEditorStaysUsable()
    {
        var (service, fake) = FakeBackboneService.Create();
        var started = new TaskCompletionSource();
        fake.On("ValidateJobAsync", a => HangAsync(started, (CancellationToken)a[1]!));
        fake.On("SaveJobAsync", a => Task.FromResult((JobDefinition)a[0]!));
        fake.On("SaveCredentialAsync", _ => Task.CompletedTask);
        var editor = new JobEditorViewModel(service, new FakePicker(), Key) { UserName = "ana", Password = "pw", Destination = @"\\nas\b\f" };
        var savedRaised = false;
        editor.Saved += (_, _) => savedRaised = true;

        var run = editor.SaveAsync();
        await started.Task;
        Assert.True(editor.IsBusy);
        editor.CancelCommand.Execute(null);
        await run;

        Assert.False(savedRaised);
        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
        Assert.DoesNotContain("SaveCredentialAsync", fake.Calls);
        Assert.False(editor.IsBusy);
        Assert.Equal(string.Empty, editor.GeneralError);
        Assert.True(editor.SaveCommand.CanExecute(null));
        Assert.Equal("pw", editor.Password);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_WithErrors_DoesNotSendCredentialNorLeaksPasswordIntoMessages()
    {
        var (editor, fake, _) = Create(() => new ValidationResult { Errors = [Issue("Destination", ValidationIssueCode.DestinationNotUnc)] });
        editor.Destination = "C:\\copias";
        editor.UserName = "ana";
        editor.Password = "CENTINELA-123";

        await editor.SaveAsync();

        Assert.DoesNotContain("SaveCredentialAsync", fake.Calls);
        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
        Assert.DoesNotContain("CENTINELA", editor.GeneralError + editor.DestinationError + editor.NameError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_CredentialFailure_DoesNotSaveJobAndShowsError()
    {
        var (editor, fake, _) = Create(() => new ValidationResult());
        fake.On("SaveCredentialAsync", _ => throw new ServiceUnavailableException());
        editor.Destination = @"\\nas\b\f";
        editor.UserName = "ana";
        editor.Password = "CENTINELA-123";

        await editor.SaveAsync();

        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
        Assert.Equal("Error_ServiceUnavailable", editor.GeneralError);
        Assert.DoesNotContain("CENTINELA", editor.GeneralError);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Save_ValidationFailedRpcErrorIsSpreadNextToFields()
    {
        var (service, fake) = FakeBackboneService.Create();
        fake.On("ValidateJobAsync", _ => Task.FromResult(new ValidationResult()));
        var data = new BackboneErrorData
        {
            Code = BackboneErrorCode.ValidationFailed,
            Validation = new ValidationResult { Errors = [Issue("Sources[0].Path", ValidationIssueCode.SourceNotAllowed)] },
        };
        fake.On("SaveJobAsync", _ => throw new RemoteInvocationException("x", (int)BackboneErrorCode.ValidationFailed, null, data));
        var picker = new FakePicker();
        picker.Picks.Enqueue(@"C:\ProgramData\Backbone");
        var editor = new JobEditorViewModel(service, picker, Key);
        await editor.AddFolderCommand.ExecuteAsync(null);
        var savedRaised = false;
        editor.Saved += (_, _) => savedRaised = true;

        await editor.SaveAsync();

        Assert.False(savedRaised);
        Assert.Equal("Validation_SourceNotAllowed", editor.Sources[0].PathError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Validate_CorrectingTheProblemClearsPreviousErrors()
    {
        var result = new ValidationResult
        {
            Errors = [Issue("Name", ValidationIssueCode.NameRequired), Issue("Destination", ValidationIssueCode.DestinationNotUnc), Issue("Sources[0].Alias", ValidationIssueCode.AliasDuplicate)],
            Warnings = [Issue("Sources[0].Path", ValidationIssueCode.SourceContainsDataFolder)],
        };
        var (editor, _, picker) = Create(() => result);
        picker.Picks.Enqueue(@"C:\");
        await editor.AddFolderCommand.ExecuteAsync(null);
        Assert.True(editor.HasNameError);
        Assert.True(editor.HasDestinationError);
        Assert.True(editor.Sources[0].HasAliasError);
        Assert.True(editor.Sources[0].HasPathWarning);

        result = new ValidationResult();
        await editor.ValidateAsync();

        Assert.False(editor.HasNameError);
        Assert.False(editor.HasDestinationError);
        Assert.False(editor.Sources[0].HasAliasError);
        Assert.False(editor.Sources[0].HasPathWarning);
        Assert.False(editor.ShowDataFolderNote);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Validate_SeveralErrorsOnSameFieldAreAllShown_AndUnknownFieldsGoToGeneralError()
    {
        var (editor, _, picker) = Create(() => new ValidationResult
        {
            Errors =
            [
                Issue("Sources[0].Path", ValidationIssueCode.SourceNotFound),
                Issue("Sources[0].Path", ValidationIssueCode.SourceNotNtfs),
                Issue("Sources[7].Path", ValidationIssueCode.SourceNotLocal),
                Issue("Sources[x].Path", ValidationIssueCode.SourceNotLocal),
                Issue("Schedule", ValidationIssueCode.Other),
            ],
            Warnings = [Issue("", ValidationIssueCode.Other)],
        });
        picker.Picks.Enqueue(@"C:\a");
        await editor.AddFolderCommand.ExecuteAsync(null);

        Assert.Contains("Validation_SourceNotFound", editor.Sources[0].PathError);
        Assert.Contains("Validation_SourceNotNtfs", editor.Sources[0].PathError);
        Assert.Equal(3, editor.GeneralError.Split(Environment.NewLine).Length);
        Assert.Single(editor.Warnings);
        Assert.True(editor.HasWarnings);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Validate_SourceNotAllowedAndWarningAreShownPerSourceAndBlockOnlyTheError()
    {
        var (editor, fake, picker) = Create(() => new ValidationResult
        {
            Errors = [Issue("Sources[1].Path", ValidationIssueCode.SourceNotAllowed), Issue("Sources[2].Path", ValidationIssueCode.SourceNotAllowed)],
            Warnings = [Issue("Sources[0].Path", ValidationIssueCode.SourceContainsDataFolder)],
        });
        foreach (var p in new[] { @"C:\ProgramData", @"D:\junction", @"C:\ProgramData\Backbone.untrusted-3f2c" })
        {
            picker.Picks.Enqueue(p);
            await editor.AddFolderCommand.ExecuteAsync(null);
        }

        Assert.Equal(string.Empty, editor.Sources[0].PathError);
        Assert.Equal("Validation_SourceContainsDataFolder", editor.Sources[0].PathWarning);
        Assert.Equal("Validation_SourceNotAllowed", editor.Sources[1].PathError);
        Assert.Equal("Validation_SourceNotAllowed", editor.Sources[2].PathError);

        await editor.SaveAsync();
        Assert.DoesNotContain("SaveJobAsync", fake.Calls);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task RemoveSource_ClearsStaleMessagesAndRemovesFromJobSent()
    {
        var (editor, _, picker) = Create(() => new ValidationResult { Errors = [Issue("Sources[0].Path", ValidationIssueCode.SourceNotFound)] });
        picker.Picks.Enqueue(@"C:\a");
        picker.Picks.Enqueue(@"C:\b");
        await editor.AddFolderCommand.ExecuteAsync(null);
        await editor.AddFolderCommand.ExecuteAsync(null);

        editor.RemoveSourceCommand.Execute(editor.Sources[0]);

        Assert.Equal(@"C:\b", Assert.Single(editor.BuildJob().Sources).Path);
        Assert.Equal(string.Empty, editor.Sources[0].PathError);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task PickerCancelledOrFailing_AddsNothingAndDoesNotValidate()
    {
        var (service, fake) = FakeBackboneService.Create();
        var editor = new JobEditorViewModel(service, new ThrowingPicker(), Key);

        await editor.AddFolderCommand.ExecuteAsync(null);

        Assert.Empty(editor.Sources);
        Assert.Empty(fake.Calls);
        Assert.Equal("Error_Internal", editor.GeneralError);

        var (editor2, fake2, _) = Create(() => new ValidationResult());
        await editor2.AddFolderCommand.ExecuteAsync(null);
        Assert.Empty(editor2.Sources);
        Assert.Empty(fake2.Calls);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task EditingExistingJob_KeepsIdInSentJob()
    {
        var (service, fake) = FakeBackboneService.Create();
        JobDefinition? sent = null;
        fake.On("ValidateJobAsync", a =>
        {
            sent = (JobDefinition)a[0]!;
            return Task.FromResult(new ValidationResult());
        });
        var existing = new JobDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Viejo",
            Destination = @"\\nas\b\f",
            Sources = [new JobSource { Path = @"C:\Datos", Alias = "Datos" }],
        };
        var editor = new JobEditorViewModel(service, new FakePicker(), Key, existing) { Name = "Nuevo" };

        await editor.ValidateAsync();

        Assert.False(editor.IsNew);
        Assert.Equal(existing.Id, sent!.Id);
        Assert.Equal("Nuevo", sent.Name);
        Assert.Equal("Datos", Assert.Single(sent.Sources).Alias);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public async Task Dispose_CancelsPendingValidation()
    {
        var (service, fake) = FakeBackboneService.Create();
        var started = new TaskCompletionSource<CancellationToken>();
        fake.On("ValidateJobAsync", a =>
        {
            started.SetResult((CancellationToken)a[1]!);
            return HangAsync(new TaskCompletionSource(), (CancellationToken)a[1]!);
        });
        var editor = new JobEditorViewModel(service, new FakePicker(), Key);

        var run = editor.ValidateAsync();
        var token = await started.Task;
        editor.Dispose();

        Assert.True(token.IsCancellationRequested);
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public void ServiceCodes26To29_HaveDistinctNonEmptyTextsInBothLanguages()
    {
        var es = ResourceCoverageTests.LoadResources("es-ES");
        var en = ResourceCoverageTests.LoadResources("en-US");
        foreach (var code in new[] { ValidationIssueCode.SourceNotAllowed, ValidationIssueCode.SourceContainsDataFolder, ValidationIssueCode.SourceUnreadable, ValidationIssueCode.SourcePathInvalid })
        {
            var key = "Validation_" + code;
            Assert.False(string.IsNullOrWhiteSpace(es[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.NotEqual(es[key], en[key]);
        }
    }

    [Fact]
    [Trait("Story", "HU-07")]
    public void ResourceFiles_HaveTheSameKeysInBothLanguages()
    {
        var es = ResourceCoverageTests.LoadResources("es-ES").Keys.ToHashSet();
        var en = ResourceCoverageTests.LoadResources("en-US").Keys.ToHashSet();

        Assert.Empty(es.Except(en));
        Assert.Empty(en.Except(es));
    }

    private static async Task<ValidationResult> HangAsync(TaskCompletionSource started, CancellationToken ct)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return new ValidationResult();
    }

    private sealed class ThrowingPicker : Backbone.App.ViewModels.IFolderPicker
    {
        public Task<string?> PickFolderAsync(CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }
}
