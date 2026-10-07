using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Exceptions;
using Domain.Money;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>withdraw</c> (ClientCommand 25) over IPC: the request reaches <see cref="IWalletSpendService"/> with the fee rate
/// in sat/kw, the result comes back, and each refusal carries the error code the CLI shows.
/// </summary>
public class WithdrawIpcHandlerTests
{
    private const string Address = "bcrt1qw508d6qejxtdg4y5r3zarvary0c5xw7kygt080";

    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly TxId s_txId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private readonly Mock<IWalletSpendService> _spendService = new();
    private WalletWithdrawRequest? _request;

    public WithdrawIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _spendService.Setup(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
                     .Callback<WalletWithdrawRequest, CancellationToken>((r, _) => _request = r)
                     .ReturnsAsync(new WalletWithdrawResult(s_txId, LightningMoney.Satoshis(40_000),
                                                            LightningMoney.Satoshis(566),
                                                            LightningMoney.Satoshis(59_434),
                                                            LightningMoney.Satoshis(2_500), 561, 1,
                                                            LightningMoney.Satoshis(10_000), true));
    }

    [Fact]
    public async Task Given_AnAmountAndAFeeRate_When_Withdrawing_Then_TheServiceGetsSatPerKwAndTheResultComesBack()
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest
                           {
                               Address = Address,
                               AmountSat = 40_000,
                               SatPerVbyte = 10
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<WithdrawIpcResponse>(response.Payload, s_options,
                                                                             TestContext.Current.CancellationToken);
        Assert.Equal(s_txId, payload.TxId);
        Assert.Equal(40_000, payload.AmountSat);
        Assert.Equal(566, payload.FeeSat);
        Assert.Equal(59_434, payload.ChangeSat);
        Assert.Equal(2_500, payload.FeeRatePerKw);
        Assert.Equal(561, payload.Weight);
        Assert.Equal(1, payload.InputCount);
        Assert.Equal(10_000, payload.AnchorReserveSat);
        Assert.True(payload.Published);
        Assert.NotNull(_request);
        Assert.Equal(Address, _request.Address);
        Assert.Equal(40_000, _request.Amount!.Satoshi);
        Assert.Equal(2_500, _request.FeeRatePerKw!.Satoshi);
    }

    [Fact]
    public async Task Given_OneSatPerVbyte_When_Withdrawing_Then_TheServiceGetsTheRelayFloorOf253SatPerKw()
    {
        // Arrange: 1 sat/vB x 250 is 250 sat/kw, below the 253 sat/kw the service accepts
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest
                           {
                               Address = Address,
                               AmountSat = 40_000,
                               SatPerVbyte = 1
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.NotNull(_request);
        Assert.Equal(253, _request.FeeRatePerKw!.Satoshi);
    }

    [Fact]
    public async Task Given_AllWithoutAFeeRate_When_Withdrawing_Then_TheServiceSendsAllAtTheEstimate()
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(CreateEnvelope(new WithdrawIpcRequest { Address = Address }),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.NotNull(_request);
        Assert.True(_request.SendAll);
        Assert.Null(_request.FeeRatePerKw);
    }

    [Theory]
    [InlineData(WalletSpendError.InvalidAddress, ErrorCodes.InvalidAddress)]
    [InlineData(WalletSpendError.WrongNetwork, ErrorCodes.InvalidAddress)]
    [InlineData(WalletSpendError.DustAmount, ErrorCodes.InvalidOperation)]
    [InlineData(WalletSpendError.FeeRateTooLow, ErrorCodes.InvalidOperation)]
    [InlineData(WalletSpendError.ChainProcessingHalted, ErrorCodes.InvalidOperation)]
    [InlineData(WalletSpendError.InputUnavailable, ErrorCodes.InvalidOperation)]
    public async Task Given_ARefusedRequest_When_Withdrawing_Then_TheErrorCodeMatches(WalletSpendError error,
                                                                                     string expectedCode)
    {
        // Arrange
        _spendService.Setup(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new WalletSpendException(error, "refused for a reason"));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest { Address = Address, AmountSat = 1_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var ipcError = AssertError(response);
        Assert.Equal(expectedCode, ipcError.Code);
        Assert.Equal("refused for a reason", ipcError.Message);
    }

    [Fact]
    public async Task Given_TheAnchorsReserve_When_Withdrawing_Then_NotEnoughBalanceWithTheServicesExplanation()
    {
        // Arrange
        _spendService.Setup(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new AnchorReserveException("would leave the wallet below the anchors reserve",
                                                             LightningMoney.Satoshis(1),
                                                             LightningMoney.Satoshis(0),
                                                             LightningMoney.Satoshis(10_000)));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest { Address = Address, AmountSat = 1_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var ipcError = AssertError(response);
        Assert.Equal(ErrorCodes.NotEnoughBalance, ipcError.Code);
        Assert.Contains("anchors reserve", ipcError.Message);
    }

    [Fact]
    public async Task Given_TooLittleMoney_When_Withdrawing_Then_NotEnoughBalanceWithTheAmounts()
    {
        // Arrange
        _spendService.Setup(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()))
                     .ThrowsAsync(new InsufficientFundsException(LightningMoney.Satoshis(50_300),
                                                                 LightningMoney.Satoshis(20_000)));
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest { Address = Address, AmountSat = 50_000 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var ipcError = AssertError(response);
        Assert.Equal(ErrorCodes.NotEnoughBalance, ipcError.Code);
        Assert.Contains("50300 sat needed", ipcError.Message);
        Assert.Contains("20000 sat spendable", ipcError.Message);
    }

    [Theory]
    [InlineData(0UL, null)]
    [InlineData(2_100_000_000_000_001UL, null)]
    [InlineData(1_000UL, 0UL)]
    [InlineData(1_000UL, 1_001UL)]
    public async Task Given_ArgumentsOutOfBounds_When_Withdrawing_Then_RefusedBeforeTheService(ulong amountSat,
        ulong? satPerVbyte)
    {
        // Arrange
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest
                           {
                               Address = Address,
                               AmountSat = amountSat,
                               SatPerVbyte = satPerVbyte
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        _spendService.Verify(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()),
                             Times.Never);
    }

    [Fact]
    public async Task Given_ChosenOutputs_When_Withdrawing_Then_TheServiceGetsThemInInternalByteOrder()
    {
        // Arrange (NL-1296): the txid as bitcoind and explorers print it, as the CLI's --utxo takes it
        const string display = "154499a7c742719609d35ae6021fb39a4c0f34ce8863ad06ed6988ad861e6fb0";
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest
                           {
                               Address = Address,
                               Utxos = [$"{display}:0", $"{new string('a', 64)}:7"]
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.NotNull(_request?.Inputs);
        Assert.Equal(2, _request.Inputs.Count);
        Assert.Equal(display, _request.Inputs[0].TxId.ToString());
        Assert.Equal(0u, _request.Inputs[0].Index);
        Assert.Equal(7u, _request.Inputs[1].Index);
        Assert.True(_request.SendAll);
    }

    [Theory]
    [InlineData("154499a7:0", false)]
    [InlineData("zz4499a7c742719609d35ae6021fb39a4c0f34ce8863ad06ed6988ad861e6fb0:0", false)]
    [InlineData("154499a7c742719609d35ae6021fb39a4c0f34ce8863ad06ed6988ad861e6fb0", false)]
    [InlineData("154499a7c742719609d35ae6021fb39a4c0f34ce8863ad06ed6988ad861e6fb0:-1", false)]
    [InlineData("154499a7c742719609d35ae6021fb39a4c0f34ce8863ad06ed6988ad861e6fb0:0", true)]
    public async Task Given_ABadOrRepeatedOutput_When_Withdrawing_Then_RefusedBeforeTheService(string utxo,
        bool twice)
    {
        // Arrange
        List<string> list = twice ? [utxo, utxo] : [utxo];
        var handler = GetHandler();

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new WithdrawIpcRequest { Address = Address, Utxos = list }),
                           TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        _spendService.Verify(s => s.WithdrawAsync(It.IsAny<WalletWithdrawRequest>(), It.IsAny<CancellationToken>()),
                             Times.Never);
    }

    [Fact]
    public void Given_TheRegistrationCalledTwice_When_Composed_Then_OneWithdrawCommand()
    {
        // Arrange
        var services = BuildServices();
        services.AddWithdrawIpcServices();

        // Act
        var provider = services.BuildServiceProvider();

        // Assert
        Assert.Single(provider.GetServices<IIpcCommandHandler>(), h => h.Command == ClientCommand.Withdraw);
        Assert.Same(_spendService.Object, provider.GetRequiredService<IWalletSpendService>());
    }

    private static IpcError AssertError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler()
    {
        var provider = BuildServices().BuildServiceProvider();
        return provider.GetServices<IIpcCommandHandler>().Single(h => h.Command == ClientCommand.Withdraw);
    }

    private ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_spendService.Object);
        services.AddWithdrawIpcServices();
        return services;
    }

    private static IpcEnvelope CreateEnvelope(WithdrawIpcRequest request)
    {
        return new IpcEnvelope
        {
            Version = 1,
            Command = ClientCommand.Withdraw,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken)
        };
    }
}