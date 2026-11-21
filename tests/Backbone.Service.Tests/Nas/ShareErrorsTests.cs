using Backbone.Contracts;
using Backbone.Service.Nas;

namespace Backbone.Service.Tests.Nas;

/// <summary>
/// HU-05 criterios 2–4 (RF-61, §8.2 <c>ShareConnector</c>): traducción de los códigos Win32 de la conexión y de la E/S a
/// códigos de negocio diferenciados, y formato del dialecto SMB.
/// </summary>
[Trait("Story", "HU-05")]
public sealed class ShareErrorsTests
{
    [Theory]
    [InlineData(1326, BackboneErrorCode.CredentialsRejected)] // ERROR_LOGON_FAILURE: contraseña errónea
    [InlineData(86, BackboneErrorCode.CredentialsRejected)] // ERROR_INVALID_PASSWORD
    [InlineData(1331, BackboneErrorCode.CredentialsRejected)] // ERROR_ACCOUNT_DISABLED
    [InlineData(1909, BackboneErrorCode.CredentialsRejected)] // ERROR_ACCOUNT_LOCKED_OUT
    [InlineData(2202, BackboneErrorCode.CredentialsRejected)] // ERROR_BAD_USERNAME
    [InlineData(1219, BackboneErrorCode.CredentialsRejected)] // ERROR_SESSION_CREDENTIAL_CONFLICT
    [InlineData(1272, BackboneErrorCode.CredentialsRejected)] // ERROR_SMB_GUEST_LOGON_BLOCKED (BAJA-3)
    [InlineData(1265, BackboneErrorCode.CredentialsRejected)] // ERROR_DOWNGRADE_DETECTED (BAJA-3)
    [InlineData(1311, BackboneErrorCode.CredentialsRejected)] // ERROR_NO_LOGON_SERVERS (BAJA-3)
    [InlineData(53, BackboneErrorCode.NasUnreachable)] // ERROR_BAD_NETPATH: servidor apagado o nombre que no resuelve
    [InlineData(1231, BackboneErrorCode.NasUnreachable)] // ERROR_NETWORK_UNREACHABLE
    [InlineData(1232, BackboneErrorCode.NasUnreachable)] // ERROR_HOST_UNREACHABLE
    [InlineData(1222, BackboneErrorCode.NasUnreachable)] // ERROR_NO_NETWORK
    [InlineData(121, BackboneErrorCode.NasUnreachable)] // ERROR_SEM_TIMEOUT
    [InlineData(64, BackboneErrorCode.NasUnreachable)] // ERROR_NETNAME_DELETED
    [InlineData(67, BackboneErrorCode.ShareNotFound)] // ERROR_BAD_NET_NAME: recurso inexistente
    [InlineData(2310, BackboneErrorCode.ShareNotFound)] // NERR_NetNameNotFound
    [InlineData(5, BackboneErrorCode.AccessDenied)] // ERROR_ACCESS_DENIED
    [InlineData(65, BackboneErrorCode.AccessDenied)] // ERROR_NETWORK_ACCESS_DENIED
    [InlineData(19, BackboneErrorCode.AccessDenied)] // ERROR_WRITE_PROTECT
    public void Known_Win32_errors_map_to_their_business_code(int win32, BackboneErrorCode expected)
    {
        Assert.Equal(expected, ShareErrors.FromWin32(win32, ShareOperation.Connect));
        Assert.Equal(expected, ShareErrors.FromWin32(win32, ShareOperation.FileAccess));
    }

    [Fact]
    public void Unknown_errors_are_unreachable_when_connecting_and_access_denied_on_file_access()
    {
        Assert.Equal(BackboneErrorCode.NasUnreachable, ShareErrors.FromWin32(9999, ShareOperation.Connect));
        Assert.Equal(BackboneErrorCode.AccessDenied, ShareErrors.FromWin32(9999, ShareOperation.FileAccess));
    }

    [Fact]
    public void IO_exceptions_carry_their_Win32_code()
    {
        var io = new IOException("\\\\nas\\backups\\secreto.txt", unchecked((int)0x8007_0040)); // ERROR_NETNAME_DELETED

        var mapped = ShareErrors.FromIoException(io);

        Assert.NotNull(mapped);
        Assert.Equal((BackboneErrorCode.NasUnreachable, 64), (mapped.Code, mapped.NativeErrorCode));
        Assert.DoesNotContain("secreto", mapped.Message, StringComparison.Ordinal); // sin rutas en el mensaje
        Assert.Equal(BackboneErrorCode.AccessDenied, ShareErrors.FromIoException(new UnauthorizedAccessException())!.Code);
        Assert.Null(ShareErrors.FromIoException(new InvalidOperationException()));
    }

    [Theory]
    [InlineData(3, 1, 1, "3.1.1")]
    [InlineData(3, 0, 2, "3.0.2")]
    [InlineData(3, 0, 0, "3.0")]
    [InlineData(2, 1, 0, "2.1")]
    public void The_SMB_dialect_is_read_from_FILE_REMOTE_PROTOCOL_INFO(ushort major, ushort minor, ushort revision, string expected)
    {
        var info = new byte[256];
        BitConverter.TryWriteBytes(info.AsSpan(4), 0x0002_0000u); // WNNC_NET_SMB
        BitConverter.TryWriteBytes(info.AsSpan(8), major);
        BitConverter.TryWriteBytes(info.AsSpan(10), minor);
        BitConverter.TryWriteBytes(info.AsSpan(12), revision);

        Assert.Equal(expected, WindowsShareConnector.FormatDialect(info));
    }

    [Fact]
    public void A_protocol_other_than_SMB_has_no_dialect()
    {
        var info = new byte[256];
        BitConverter.TryWriteBytes(info.AsSpan(4), 0x002E_0000u); // WNNC_NET_DAV

        Assert.Null(WindowsShareConnector.FormatDialect(info));
    }
}
