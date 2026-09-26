using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace NLightning.Daemon.Tests.Handlers;

using Application.Onchain.Mempool;
using Daemon.Extensions;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Constants;
using Domain.Bitcoin.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using NLightning.Client;
using NLightning.Client.Printers;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// NL-216 over IPC: <c>chainstatus</c> (ClientCommand 16) reports the chain monitor's halt, and the IPC commands that
/// would take new risk (<c>openchannel</c>, <c>payinvoice</c>) are refused while it is set.
/// </summary>
public class ChainStatusClientHandlerTests
{
    private readonly Mock<IBitcoinChainService> _chain = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();

    public ChainStatusClientHandlerTests()
    {
        _chain.Setup(c => c.GetCurrentBlockHeightAsync()).ReturnsAsync(812u);
        _monitor.SetupGet(m => m.LastProcessedBlockHeight).Returns(800u);
    }

    [Fact]
    public async Task Given_HaltedMonitor_When_ChainStatus_Then_ReasonHeightsAndRefusedOperationsReported()
    {
        // Arrange
        _monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        _monitor.SetupGet(m => m.ChainProcessingHaltReason).Returns("block 801 failed 3 times in a row");
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new ChainStatusClientRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.IsChainProcessingHalted);
        Assert.Equal("block 801 failed 3 times in a row", response.HaltReason);
        Assert.Equal(800u, response.LastProcessedBlockHeight);
        Assert.Equal(812u, response.ChainTipHeight);
        Assert.Equal(ChainProcessingHalt.RefusedOperations, response.RefusedOperations);
    }

    [Fact]
    public async Task Given_RunningMonitor_When_ChainStatus_Then_NotHaltedAndNothingRefused()
    {
        // Arrange
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new ChainStatusClientRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(response.IsChainProcessingHalted);
        Assert.Null(response.HaltReason);
        Assert.Empty(response.RefusedOperations);
    }

    [Fact]
    public async Task Given_BitcoindUnreachable_When_ChainStatus_Then_TheTipIsUnknownInsteadOfAnError()
    {
        // Arrange
        _monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        _chain.Setup(c => c.GetCurrentBlockHeightAsync()).ThrowsAsync(new HttpRequestException("connection refused"));
        var handler = CreateHandler();

        // Act
        var response = await handler.HandleAsync(new ChainStatusClientRequest(), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.IsChainProcessingHalted);
        Assert.Equal("unknown", response.HaltReason);
        Assert.Null(response.ChainTipHeight);
    }

    [Fact]
    public void Given_IpcDtos_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var options = NLightningMessagePackOptions.Options;
        var ct = TestContext.Current.CancellationToken;
        var response = ChainStatusIpcResponse.FromClientResponse(
            new ChainStatusClientResponse(true, "a reorg", 700, null, ChainProcessingHalt.RefusedOperations));

        // Act
        var request2 = MessagePackSerializer.Deserialize<ChainStatusIpcRequest>(
            MessagePackSerializer.Serialize(new ChainStatusIpcRequest(), options, ct), options, ct);
        var response2 = MessagePackSerializer.Deserialize<ChainStatusIpcResponse>(
            MessagePackSerializer.Serialize(response, options, ct), options, ct);

        // Assert
        Assert.NotNull(request2.ToClientRequest());
        Assert.True(response2.IsChainProcessingHalted);
        Assert.Equal("a reorg", response2.HaltReason);
        Assert.Equal(700u, response2.LastProcessedBlockHeight);
        Assert.Null(response2.ChainTipHeight);
        Assert.Equal(ChainProcessingHalt.RefusedOperations, response2.RefusedOperations);
    }

    [Fact]
    public void Given_HaltedStatus_When_Printed_Then_TheHaltAndEveryRefusalAreShown()
    {
        // Arrange
        using var output = new StringWriter();
        var response = ChainStatusIpcResponse.FromClientResponse(
            new ChainStatusClientResponse(true, "a reorg", 700, 710, ChainProcessingHalt.RefusedOperations));

        // Act
        new ChainStatusPrinter(output).Print(response);

        // Assert
        var text = output.ToString();
        Assert.Contains("Processing: HALTED", text);
        Assert.Contains("Halt reason: a reorg", text);
        Assert.Contains("bitcoind tip: 710", text);
        Assert.All(ChainProcessingHalt.RefusedOperations, o => Assert.Contains(o, text));
        Assert.Null(ClientApp.ValidateArguments("chainstatus", []));
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_TheChainStatusCommandAndTheMempoolReactorResolve()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Network"] = "regtest",
                               ["Database:Provider"] = "Sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(_chain.Object);
        services.AddSingleton(_monitor.Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert
        Assert.Equal(16, (int)ClientCommand.ChainStatus);
        Assert.Contains(ClientCommand.ChainStatus, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());
        Assert.IsType<ChainStatusClientHandler>(
            scope.ServiceProvider
                 .GetRequiredService<IClientCommandHandler<ChainStatusClientRequest, ChainStatusClientResponse>>());

        // BOLT 5 O8: the hosted service starts the mempool reactor (one instance, with the penalty resolver)
        Assert.Same(provider.GetRequiredService<MempoolReactor>(), provider.GetRequiredService<IMempoolReactor>());
    }

    [Fact]
    public async Task Given_ChainProcessingHalted_When_PayInvoice_Then_InvalidOperationAndNothingSent()
    {
        // Arrange
        _monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        var paymentService = new Mock<IPaymentService>();
        var handler = new PayInvoiceClientHandler(paymentService.Object, _monitor.Object);

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(new PayInvoiceClientRequest("lnbcrt1pay"),
                                                      TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("chain processing is halted", exception.Message);
        paymentService.Verify(p => p.PayInvoiceAsync(It.IsAny<string>(), It.IsAny<LightningMoney?>(),
                                                     It.IsAny<PayInvoiceOptions>(), It.IsAny<CancellationToken>()),
                              Times.Never);
    }

    [Fact]
    public async Task Given_ChainProcessingHalted_When_OpenChannel_Then_InvalidOperationBeforeAnyPeerIsContacted()
    {
        // Arrange
        _monitor.SetupGet(m => m.IsChainProcessingHalted).Returns(true);
        var peerManager = new Mock<IPeerManager>();
        var channelManager = new Mock<IChannelManager>();
        var handler = new OpenChannelClientHandler(_monitor.Object, new Mock<IChannelFactory>().Object,
                                                   channelManager.Object, new Mock<IChannelMemoryRepository>().Object,
                                                   new Mock<ILogger<OpenChannelClientHandler>>().Object,
                                                   new Mock<IMessageFactory>().Object, peerManager.Object,
                                                   new Mock<IUtxoMemoryRepository>().Object);
        var request = new OpenChannelClientRequest(
            "02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619@127.0.0.1:9735",
            LightningMoney.Satoshis(100_000));

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => handler.HandleAsync(request, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, exception.ErrorCode);
        Assert.Contains("chain processing is halted", exception.Message);
        peerManager.VerifyNoOtherCalls();
        channelManager.VerifyNoOtherCalls();
    }

    private ChainStatusClientHandler CreateHandler() =>
        new(_chain.Object, _monitor.Object, NullLogger<ChainStatusClientHandler>.Instance);
}