using Backbone.Service.Hosting;
using Backbone.Service.Ipc;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backbone.Service.Tests.Hosting;

/// <summary>
/// HT-03 I-1, §8.2, §8.3 y §8.5: eventos del arranque. 1003 cuando la carpeta de datos se ha apartado y sustituido; 1002
/// (y el servicio no arranca) cuando <c>settings.json</c> se rechaza.
/// </summary>
[Trait("Story", "HT-03")]
public sealed class StartupServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeEventLogWriter _eventLog = new();

    public StartupServiceTests()
    {
        // Con el guardián real la crea el arranque (§8.3); aquí el guardián es un doble.
        Directory.CreateDirectory(_temp.Combine("config"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task A_replaced_data_directory_writes_warning_1003_with_the_moved_aside_path_and_the_service_starts()
    {
        var movedAside = _temp.Combine("Backbone.untrusted-0123456789abcdef0123456789abcdef");
        var guard = new FakeGuard(new DataDirectoryCheck(DataDirectoryCheckResult.Replaced, [movedAside]));
        using var settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        await NewStartup(guard, settings).StartingAsync(Ct);

        var entry = Assert.Single(_eventLog.Entries);
        Assert.Equal(EventLogLevel.Warning, entry.Level);
        Assert.Equal(BackboneEventIds.DataDirectoryReplaced, entry.EventId);
        Assert.Equal(1003, entry.EventId);
        Assert.Contains(movedAside, entry.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData((int)DataDirectoryCheckResult.Secure)]
    [InlineData((int)DataDirectoryCheckResult.Corrected)]
    public async Task A_secure_or_corrected_data_directory_writes_no_event(int value)
    {
        var result = (DataDirectoryCheckResult)value;
        using var settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        await NewStartup(new FakeGuard(new DataDirectoryCheck(result, [])), settings).StartingAsync(Ct);

        Assert.Empty(_eventLog.Entries);
    }

    [Fact]
    public async Task A_settings_file_owned_by_a_user_stops_the_start_with_error_1002()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        var path = TrustedFileTests.CreateUserOwnedFile(_temp, Path.Combine("config", "settings.json"), "{ \"authorizedUsers\": [] }");
        using var settings = new SettingsStore(path, new TrustedFile(FileTrustPolicy.Default), NullLogger<SettingsStore>.Instance);

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => NewStartup(new FakeGuard(DataDirectoryCheck.Secure), settings).StartingAsync(Ct));

        var rejected = Assert.IsType<UntrustedFileException>(ex.InnerException);
        Assert.Equal(UntrustedFileReason.UntrustedOwner, rejected.Reason);
        var entry = Assert.Single(_eventLog.Entries);
        Assert.Equal(EventLogLevel.Error, entry.Level);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, entry.EventId);
        Assert.Contains("settings.json", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_junction_in_place_of_settings_stops_the_start_with_error_1002()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");
        var link = _temp.Combine("config", "settings.json");
        TrustedFileTests.RunCmd("mklink", "/J", link, target);
        using var settings = new SettingsStore(link, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => NewStartup(new FakeGuard(DataDirectoryCheck.Secure), settings).StartingAsync(Ct));

        Assert.Equal(UntrustedFileReason.ReparsePoint, Assert.IsType<UntrustedFileException>(ex.InnerException).Reason);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, Assert.Single(_eventLog.Entries).EventId);
    }

    [Fact]
    public async Task A_hard_linked_settings_file_stops_the_start_with_error_1002()
    {
        Directory.CreateDirectory(_temp.Combine("config"));
        var other = _temp.Combine("otro.json");
        File.WriteAllText(other, "{}");
        var link = _temp.Combine("config", "settings.json");
        TrustedFileTests.RunCmd("mklink", "/H", link, other);
        using var settings = new SettingsStore(link, TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => NewStartup(new FakeGuard(DataDirectoryCheck.Secure), settings).StartingAsync(Ct));

        Assert.Equal(UntrustedFileReason.HardLinked, Assert.IsType<UntrustedFileException>(ex.InnerException).Reason);
        var entry = Assert.Single(_eventLog.Entries);
        Assert.Equal(EventLogLevel.Error, entry.Level);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, entry.EventId);
    }

    [Fact]
    public async Task A_junction_in_place_of_the_config_folder_stops_the_start_with_error_1002_without_reading_the_target()
    {
        // §8.2 "Carpeta padre": config\ rechazada = settings.json rechazado.
        Directory.Delete(_temp.Combine("config"));
        var target = Directory.CreateDirectory(_temp.Combine("destino")).FullName;
        File.WriteAllText(Path.Combine(target, "settings.json"), $$"""{ "authorizedUsers": ["{{TestSids.Ana}}"] }""");
        var config = _temp.Combine("config");
        TrustedFileTests.RunCmd("mklink", "/J", config, target);
        using var settings = new SettingsStore(Path.Combine(config, "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => NewStartup(new FakeGuard(DataDirectoryCheck.Secure), settings).StartingAsync(Ct));

        var rejected = Assert.IsType<UntrustedFileException>(ex.InnerException);
        Assert.Equal(UntrustedFileReason.ReparsePoint, rejected.Reason);
        Assert.Equal(config, rejected.FilePath);
        Assert.Empty(settings.Sids);
        var entry = Assert.Single(_eventLog.Entries);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, entry.EventId);
        Assert.Contains(config, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_config_folder_stops_the_start_with_error_1002()
    {
        Directory.Delete(_temp.Combine("config"));
        using var settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        var ex = await Assert.ThrowsAsync<ServiceStartupException>(() => NewStartup(new FakeGuard(DataDirectoryCheck.Secure), settings).StartingAsync(Ct));

        Assert.IsType<DirectoryNotFoundException>(ex.InnerException);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, Assert.Single(_eventLog.Entries).EventId);
        Assert.False(Directory.Exists(_temp.Combine("config"))); // los almacenes no crean carpetas
    }

    [Fact]
    public async Task A_data_directory_that_cannot_be_secured_stops_the_start_with_error_1002_before_reading_settings()
    {
        var guard = new ThrowingGuard(new DataDirectoryInsecureException("no se pudo apartar"));
        using var settings = new SettingsStore(_temp.Combine("config", "settings.json"), TestFileTrust.CurrentUser, NullLogger<SettingsStore>.Instance);

        await Assert.ThrowsAsync<ServiceStartupException>(() => NewStartup(guard, settings).StartingAsync(Ct));

        var entry = Assert.Single(_eventLog.Entries);
        Assert.Equal(BackboneEventIds.ServiceStartFailed, entry.EventId);
        Assert.Equal(EventLogLevel.Error, entry.Level);
        Assert.False(File.Exists(_temp.Combine("config", "settings.json")));
    }

    private StartupService NewStartup(IDataDirectoryGuard guard, SettingsStore settings) =>
        new(new ServicePaths(_temp.Path), guard, settings, _eventLog, new ServiceRuntimeInfo(TimeProvider.System), NullLogger<StartupService>.Instance);

    private sealed class ThrowingGuard(Exception exception) : IDataDirectoryGuard
    {
        public DataDirectoryCheck EnsureSecure(string root) => throw exception;
    }

    private sealed class FakeGuard(DataDirectoryCheck result) : IDataDirectoryGuard
    {
        public DataDirectoryCheck EnsureSecure(string root) => result;
    }
}
