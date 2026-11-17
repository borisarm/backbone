using System.Diagnostics;
using Backbone.Contracts;
using Backbone.Service.Nas;
using Backbone.Service.Os;
using Backbone.Service.Persistence;
using Backbone.Service.Tests.TestSupport;
using Microsoft.Extensions.Logging;

namespace Backbone.IntegrationTests.Nas;

/// <summary>
/// HU-05 con <c>WNetAddConnection2</c> real (<see cref="WindowsShareConnector"/>). No requieren elevación.
/// </summary>
/// <remarks>
/// Las pruebas contra un recurso real necesitan un servidor SMB que no sea este equipo (el servicio rechaza el propio
/// equipo, §7.5) y se configuran con variables de entorno; sin ellas se omiten:
/// <list type="bullet">
/// <item><c>BACKBONE_IT_SMB_SHARE</c>: <c>\\servidor\recurso</c> con permiso de lectura y escritura (p. ej. el NAS OMV de
/// pruebas).</item>
/// <item><c>BACKBONE_IT_SMB_USER</c> y <c>BACKBONE_IT_SMB_PASSWORD</c>: credencial de ese recurso.</item>
/// <item><c>BACKBONE_IT_SMB_READONLY_SHARE</c> (opcional): recurso del mismo servidor en el que ese usuario solo puede leer.</item>
/// </list>
/// </remarks>
[Trait("Category", "Integration")]
[Trait("Story", "HU-05")]
public sealed class WindowsShareConnectorTests : IDisposable
{
    private const string NoShare = "Define BACKBONE_IT_SMB_SHARE, BACKBONE_IT_SMB_USER y BACKBONE_IT_SMB_PASSWORD para probar contra un recurso SMB real.";

    private readonly TempDirectory _temp = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly CredentialStore _store;
    private readonly ConnectionTester _tester;

    public WindowsShareConnectorTests()
    {
        _loggerFactory = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        Directory.CreateDirectory(_temp.Combine("credentials"));
        _store = new CredentialStore(_temp.Combine("credentials"), TestFileTrust.CurrentUser, new DpapiMachineProtector(), TimeProvider.System, _loggerFactory.CreateLogger<CredentialStore>());
        _tester = new ConnectionTester(
            new WindowsShareConnector(_loggerFactory.CreateLogger<WindowsShareConnector>()),
            new ShareCredentialProvider(_store, _loggerFactory.CreateLogger<ShareCredentialProvider>()),
            TimeProvider.System,
            new ConnectionTestOptions(),
            _loggerFactory.CreateLogger<ConnectionTester>());
    }

    public static bool HasShare => Share is not null && User is not null && Password is not null;

    private static string? Share => Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_SHARE");

    private static string? User => Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_USER");

    private static string? Password => Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_PASSWORD");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _store.Dispose();
        _loggerFactory.Dispose();
        _temp.Dispose();
    }

    /// <summary>Criterio 3: 192.0.2.1 (TEST-NET-1, RFC 5737) no responde nunca.</summary>
    [Fact]
    public async Task A_server_that_does_not_exist_is_NasUnreachable_in_less_than_15_seconds()
    {
        var watch = Stopwatch.StartNew();

        var result = await _tester.TestAsync(@"\\192.0.2.1\backups", new CredentialInput { UserName = "nadie", Password = "x" }, Ct);

        watch.Stop();
        Assert.Equal(BackboneErrorCode.NasUnreachable, result.ErrorCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"Tardó {watch.Elapsed}.");
    }

    /// <summary>Criterios 1 y 5.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task A_real_share_succeeds_with_space_and_dialect_and_leaves_no_file()
    {
        var result = await _tester.TestAsync(Share!, new CredentialInput { UserName = User!, Password = Password! }, Ct);
        await _tester.WhenCleanupsCompleteAsync();

        Assert.True(result.Success, $"ErrorCode = {result.ErrorCode}");
        Assert.True(result.FreeBytes > 0 && result.TotalBytes >= result.FreeBytes);
        Assert.Matches(@"^[23]\.\d+(\.\d+)?$", result.SmbDialect);
        await AssertNoProbeFilesAsync();
        Assert.DoesNotContain(Password!, _logs.AllText(), StringComparison.Ordinal);
    }

    /// <summary>Criterio 2.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task A_wrong_password_is_CredentialsRejected()
    {
        var result = await _tester.TestAsync(Share!, new CredentialInput { UserName = User!, Password = Password + "-mala" }, Ct);

        Assert.Equal(BackboneErrorCode.CredentialsRejected, result.ErrorCode);
    }

    /// <summary>Criterio 4: recurso inexistente.</summary>
    [Fact(Skip = NoShare, SkipUnless = nameof(HasShare))]
    public async Task A_missing_share_is_ShareNotFound()
    {
        Assert.True(UncPaths.TryParse(Share, out var root, out _, out _));
        var server = root[..root.LastIndexOf('\\')];

        var result = await _tester.TestAsync(server + @"\no-existe-" + Guid.NewGuid().ToString("N")[..8], new CredentialInput { UserName = User!, Password = Password! }, Ct);

        Assert.Equal(BackboneErrorCode.ShareNotFound, result.ErrorCode);
    }

    /// <summary>Criterio 4: sin permiso de escritura.</summary>
    [Fact(Skip = "Define también BACKBONE_IT_SMB_READONLY_SHARE.", SkipUnless = nameof(HasReadOnlyShare))]
    public async Task A_read_only_share_is_AccessDenied_after_reading()
    {
        var readOnly = Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_READONLY_SHARE")!;

        var result = await _tester.TestAsync(readOnly, new CredentialInput { UserName = User!, Password = Password! }, Ct);

        Assert.Equal(BackboneErrorCode.AccessDenied, result.ErrorCode);
        Assert.True(result.CanRead);
        Assert.False(result.CanWrite);
    }

    public static bool HasReadOnlyShare => HasShare && Environment.GetEnvironmentVariable("BACKBONE_IT_SMB_READONLY_SHARE") is not null;

    private async Task AssertNoProbeFilesAsync()
    {
        var connector = new WindowsShareConnector(_loggerFactory.CreateLogger<WindowsShareConnector>());
        Assert.True(UncPaths.TryParse(Share, out var root, out var destination, out _));
        await _store.SaveAsync(root, new CredentialInput { UserName = User!, Password = Password! }, Ct);
        using var credential = (await _store.GetAsync(root, Ct))!;
        await using var connection = await connector.ConnectAsync(credential, Ct);
        Assert.Empty(Directory.EnumerateFiles(Directory.Exists(destination) ? destination : root, ConnectionTester.ProbeFilePrefix + "*"));
    }
}
