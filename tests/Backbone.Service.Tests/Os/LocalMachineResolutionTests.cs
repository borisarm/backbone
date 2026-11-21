using System.Net;
using System.Net.Sockets;
using Backbone.Service.Os;
using Backbone.Service.Tests.TestSupport;

namespace Backbone.Service.Tests.Os;

/// <summary>
/// §7.5 (revisión de seguridad de HU-05, ALTA-1): <see cref="LocalMachine.IsSelf"/> y <see cref="LocalMachine.ResolvesToSelfAsync"/>.
/// </summary>
[Trait("Story", "HU-05")]
public sealed class LocalMachineResolutionTests
{
    private static readonly IPAddress[] Local = [IPAddress.Parse("192.168.1.20"), IPAddress.Parse("fe80::1234%7")];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.20.30")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:127.0.0.1")] // IPv4 mapeada en IPv6
    [InlineData("::ffff:192.168.1.20")]
    [InlineData("192.168.1.20")]
    [InlineData("fe80::1234")] // mismo enlace local con otro ámbito (o sin él)
    [InlineData("fe80::1234%3")]
    public void Loopback_any_and_local_addresses_are_the_machine_itself(string address)
    {
        Assert.True(LocalMachine.IsSelf(IPAddress.Parse(address), Local));
    }

    [Theory]
    [InlineData("192.168.1.163")]
    [InlineData("::ffff:192.168.1.163")]
    [InlineData("fe80::99")]
    [InlineData("2001:db8::1")]
    public void Other_addresses_are_not(string address)
    {
        Assert.False(LocalMachine.IsSelf(IPAddress.Parse(address), Local));
    }

    [Fact]
    public async Task A_name_with_any_address_of_the_machine_resolves_to_self()
    {
        var resolver = new FakeHostResolver();
        resolver.Addresses["nas"] = [IPAddress.Parse("192.168.1.163"), IPAddress.Parse("192.168.1.20")];

        Assert.True(await LocalMachine.ResolvesToSelfAsync("nas", resolver, Local, Ct));
    }

    [Fact]
    public async Task A_name_that_does_not_resolve_is_not_rejected()
    {
        var resolver = new FakeHostResolver();
        resolver.Errors["nas"] = FakeHostResolver.HostNotFound();

        Assert.False(await LocalMachine.ResolvesToSelfAsync("nas", resolver, Local, Ct));
    }

    [Fact]
    public async Task The_real_resolver_sees_localhost_as_the_machine_itself()
    {
        Assert.True(await LocalMachine.ResolvesToSelfAsync("localhost", new DnsHostResolver(), LocalMachine.Addresses(), Ct));
    }

    [Fact]
    public async Task The_real_resolver_sees_the_machine_name_as_the_machine_itself()
    {
        Assert.True(await LocalMachine.ResolvesToSelfAsync(Environment.MachineName, new DnsHostResolver(), LocalMachine.Addresses(), Ct));
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var resolver = new FakeHostResolver { Gate = new TaskCompletionSource() };
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalMachine.ResolvesToSelfAsync("nas", resolver, Local, cancel.Token));
    }

    [Fact]
    public void SocketException_is_what_the_fake_uses_for_unknown_names() =>
        Assert.Equal(SocketError.HostNotFound, FakeHostResolver.HostNotFound().SocketErrorCode);
}
