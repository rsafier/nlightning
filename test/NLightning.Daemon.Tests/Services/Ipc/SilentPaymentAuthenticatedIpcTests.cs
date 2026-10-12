using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Client.Ipc;

namespace NLightning.Daemon.Tests.Services.Ipc;

using Daemon.Contracts.Utilities;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Daemon.Services.Ipc;
using Domain.Bitcoin.SilentPayments.Interfaces;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Client.Enums;
using TestCollections;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;

[Collection(SerialTestCollection.Name)]
public class SilentPaymentAuthenticatedIpcTests : IAsyncLifetime
{
    private static readonly SilentPaymentStatus s_status = new(true, true, true, true, 10, 20, 30, 40, 50,
        100, "RpcVerbosity3", 7, 2, 5, 123.45, null);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sp" + Guid.NewGuid().ToString("N")[..8]);
    private readonly Mock<ISilentPaymentService> _silent = new();
    private readonly ServiceProvider _provider;
    private readonly NamedPipeIpcService _server;

    public SilentPaymentAuthenticatedIpcTests()
    {
        Directory.CreateDirectory(_directory);
        MessagePackSerializer.DefaultOptions = NLightningMessagePackOptions.Options;
        _silent.Setup(s => s.GetAddressAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string? name, CancellationToken _) => new SilentPaymentAddressResult("sprt1qaddress", 7, name, true));
        _silent.Setup(s => s.ListLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SilentPaymentLabelInfo(7, "store", 10, "sprt1qlabel")]);
        _silent.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(s_status);
        _silent.Setup(s => s.StartRescanAsync(10, 100, It.IsAny<CancellationToken>())).ReturnsAsync(s_status);
        _silent.Setup(s => s.CancelRescanAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(s_status with { RescanTargetHeight = null, RescanCursorHeight = null });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_silent.Object);
        services.AddSilentPaymentIpcServices();
        services.AddSilentPaymentIpcServices();
        _provider = services.BuildServiceProvider();
        var router = new IpcRequestRouter(_provider.GetServices<IIpcCommandHandler>(), NullLogger<IpcRequestRouter>.Instance);
        var authenticator = new CookieFileAuthenticator(NodeUtils.GetCookieFilePath(_directory), NullLogger<CookieFileAuthenticator>.Instance);
        _server = new NamedPipeIpcService(authenticator, _directory, new IpcFraming(), NullLogger<NamedPipeIpcService>.Instance, router);
    }

    public async ValueTask InitializeAsync() => await _server.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        await _provider.DisposeAsync();
        Directory.Delete(_directory, true);
    }

    private NamedPipeIpcClient Connect(string? cookieFile = null) =>
        new(NodeUtils.GetNamedPipeFilePath(_directory), cookieFile ?? NodeUtils.GetCookieFilePath(_directory));

    [Fact]
    public async Task Given_AuthenticatedClient_When_AddressLabelsRescanAndStatus_Then_AllFourRoutesReachService()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var client = Connect();
        // Act
        var address = await client.SilentPaymentAsync(ClientCommand.GetSilentPaymentAddress,
            new SilentPaymentIpcRequest { Label = "store" }, ct);
        var labels = await client.SilentPaymentAsync(ClientCommand.SilentPaymentLabels, new SilentPaymentIpcRequest(), ct);
        var scan = await client.SilentPaymentAsync(ClientCommand.SilentPaymentRescan,
            new SilentPaymentIpcRequest { FromHeight = 10, RecoveryLabels = 100 }, ct);
        var status = await client.SilentPaymentAsync(ClientCommand.SilentPaymentStatus, new SilentPaymentIpcRequest(), ct);
        var canceled = await client.SilentPaymentAsync(ClientCommand.SilentPaymentRescan, new SilentPaymentIpcRequest { Cancel = true }, ct);
        // Assert
        Assert.Equal(("sprt1qaddress", "store", (uint?)7), (address.Address, address.LabelName, address.Label));
        Assert.Equal("store", Assert.Single(labels.Labels!).Name);
        Assert.Equal((uint?)50, scan.Status!.RescanTargetHeight);
        Assert.Equal(123.45, status.Status!.LastScanMilliseconds);
        Assert.Null(canceled.Status!.RescanTargetHeight);
        _silent.Verify(s => s.GetAddressAsync("store", It.IsAny<CancellationToken>()), Times.Once);
        _silent.Verify(s => s.StartRescanAsync(10, 100, It.IsAny<CancellationToken>()), Times.Once);
        _silent.Verify(s => s.CancelRescanAsync(It.IsAny<CancellationToken>()), Times.Once);
        // The four silent payment handlers and the wallet history handler (NL-1289), each registered once
        Assert.Equal(5, _provider.GetServices<IIpcCommandHandler>().Count());
    }

    [Fact]
    public async Task Given_DisabledService_When_AuthenticatedClientRequestsAddressAndStatus_Then_ReasonAndDisabledStatusSurvive()
    {
        // Arrange
        _silent.Setup(s => s.GetAddressAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SilentPayments:Enabled is false."));
        _silent.Setup(s => s.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(s_status with { Enabled = false, Send = false, Receive = false });
        await using var client = Connect();
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SilentPaymentAsync(
            ClientCommand.GetSilentPaymentAddress, new SilentPaymentIpcRequest(), TestContext.Current.CancellationToken));
        var status = await client.SilentPaymentAsync(ClientCommand.SilentPaymentStatus,
            new SilentPaymentIpcRequest(), TestContext.Current.CancellationToken);
        // Assert
        Assert.Contains("invalid_operation", error.Message);
        Assert.Contains("SilentPayments:Enabled is false", error.Message);
        Assert.False(status.Status!.Enabled);
    }

    [Fact]
    public async Task Given_WrongCookie_When_SilentPaymentStatusRequested_Then_AuthenticationRefusesBeforeService()
    {
        // Arrange
        var badCookie = Path.Combine(_directory, "bad-cookie");
        await File.WriteAllTextAsync(badCookie, "incorrect-token", TestContext.Current.CancellationToken);
        await using var client = Connect(badCookie);
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SilentPaymentAsync(
            ClientCommand.SilentPaymentStatus, new SilentPaymentIpcRequest(), TestContext.Current.CancellationToken));
        // Assert
        Assert.Contains("auth", error.Message, StringComparison.OrdinalIgnoreCase);
        _silent.Verify(s => s.GetStatusAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Given_ContradictoryRescanRequest_When_Authenticated_Then_RefusedBeforeRescanService()
    {
        // Arrange
        await using var client = Connect();
        // Act
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SilentPaymentAsync(
            ClientCommand.SilentPaymentRescan, new SilentPaymentIpcRequest { Cancel = true, FromHeight = 10 },
            TestContext.Current.CancellationToken));
        // Assert
        Assert.Contains("invalid_operation", error.Message);
        _silent.Verify(s => s.StartRescanAsync(It.IsAny<uint>(), It.IsAny<uint?>(), It.IsAny<CancellationToken>()), Times.Never);
        _silent.Verify(s => s.CancelRescanAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}