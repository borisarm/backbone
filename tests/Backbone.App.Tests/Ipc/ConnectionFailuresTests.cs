using System.IO.Pipes;
using Backbone.App.Ipc;
using Backbone.Contracts;
using StreamJsonRpc;

namespace Backbone.App.Tests.Ipc;

/// <summary>HT-03 criterio 5: clasificación de errores de conexión (§7.3, §7.4, §9.3).</summary>
[Trait("Story", "HT-03")]
public sealed class ConnectionFailuresTests
{
    public static TheoryData<Exception, ConnectionFailure> Cases => new()
    {
        { new UnauthorizedAccessException(), ConnectionFailure.Unauthorized },
        { new RemoteInvocationException("x", (int)BackboneErrorCode.NotAuthorized, null, null), ConnectionFailure.Unauthorized },
        { new ContractIncompatibleException("2.0"), ConnectionFailure.IncompatibleContract },
        { new TimeoutException(), ConnectionFailure.ServiceUnavailable },
        { new FileNotFoundException(), ConnectionFailure.ServiceUnavailable },
        { new IOException("pipe roto"), ConnectionFailure.ServiceUnavailable },
        { new ServerVerificationException("falso"), ConnectionFailure.ServiceUnavailable },
        { new ConnectionLostException(), ConnectionFailure.ServiceUnavailable },
        { new EndOfStreamException(), ConnectionFailure.ServiceUnavailable },
        { new BadRpcHeaderException("cabecera"), ConnectionFailure.ServiceUnavailable },
        { new RemoteInvocationException("x", (int)BackboneErrorCode.Internal, null, null), ConnectionFailure.ServiceUnavailable },
        { new InvalidOperationException(), ConnectionFailure.ServiceUnavailable },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_exception_is_classified(Exception exception, ConnectionFailure expected) =>
        Assert.Equal(expected, ConnectionFailures.Classify(exception));

    [Theory]
    [InlineData(ConnectionFailure.ServiceUnavailable, false, 1, ConnectionState.ServiceStopped)]
    [InlineData(ConnectionFailure.ServiceUnavailable, true, 1, ConnectionState.Reconnecting)]
    [InlineData(ConnectionFailure.ServiceUnavailable, true, 3, ConnectionState.Reconnecting)]
    [InlineData(ConnectionFailure.ServiceUnavailable, true, 4, ConnectionState.ServiceStopped)]
    [InlineData(ConnectionFailure.Unauthorized, true, 1, ConnectionState.Unauthorized)]
    [InlineData(ConnectionFailure.Unauthorized, false, 1, ConnectionState.Unauthorized)]
    [InlineData(ConnectionFailure.IncompatibleContract, true, 2, ConnectionState.IncompatibleContract)]
    public void The_failure_maps_to_a_connection_state(ConnectionFailure failure, bool hadSession, int failures, ConnectionState expected) =>
        Assert.Equal(expected, ConnectionFailures.ToState(failure, hadSession, failures, reconnectingAttempts: 4));

    [Fact]
    public void A_business_error_gives_its_code_and_data()
    {
        var data = new BackboneErrorData { Code = BackboneErrorCode.CredentialsRejected };
        var ex = new RemoteInvocationException("x", (int)BackboneErrorCode.CredentialsRejected, null, data);

        Assert.True(ConnectionFailures.TryGetErrorCode(ex, out var code, out var received));

        Assert.Equal(BackboneErrorCode.CredentialsRejected, code);
        Assert.Same(data, received);
    }

    [Fact]
    public void A_foreign_json_rpc_error_is_not_a_business_error()
    {
        var ex = new RemoteInvocationException("x", -32601, null, null);

        Assert.False(ConnectionFailures.TryGetErrorCode(ex, out _, out _));
    }

    [Fact]
    public void The_client_opens_the_pipe_without_the_right_to_create_instances()
    {
        // La ACL del servidor no concede FILE_CREATE_PIPE_INSTANCE a los usuarios (§7.3): pedirlo (PipeDirection.InOut) la rechaza.
        Assert.False(NamedPipeBackboneConnector.ClientRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.True(NamedPipeBackboneConnector.ClientRights.HasFlag(PipeAccessRights.ReadData | PipeAccessRights.WriteData));
    }
}
