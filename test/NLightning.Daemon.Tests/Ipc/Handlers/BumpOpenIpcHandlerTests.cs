using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.DualFunding.Interfaces;
using Domain.Channels.DualFunding.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.LiquidityAds.Models;
using Domain.Money;
using NLightning.Client;
using NLightning.Client.Handlers;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>bumpopen</c> (ClientCommand 38, lane dfrbf) over IPC: the request reaches
/// <see cref="IDualFundedOpenService.BumpAsync(ChannelId, uint, LightningMoney?, LiquidityRequest?, CancellationToken)"/> with its
/// feerate and contribution, the new attempt's txid comes back, the service's refusals and a failed RBF carry the error
/// code the CLI shows, and the CLI parses its arguments before any IPC. The dual-funding service is a mock.
/// </summary>
public class BumpOpenIpcHandlerTests
{
    private const string ChannelIdHex = "0101010101010101010101010101010101010101010101010101010101010101";

    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly ChannelId s_channelId = new(Convert.FromHexString(ChannelIdHex));
    private static readonly TxId s_txId = new(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

    private readonly Mock<IDualFundedOpenService> _service = new();
    private (uint Feerate, LightningMoney? Contribution)? _call;
    private LiquidityRequest? _liquidity;
    private LiquidityPurchaseModel? _purchase;

    public BumpOpenIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _service.Setup(s => s.BumpAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(), It.IsAny<LightningMoney?>(),
                                        It.IsAny<LiquidityRequest?>(), It.IsAny<CancellationToken>()))
                .Callback<ChannelId, uint, LightningMoney?, LiquidityRequest?, CancellationToken>((_, f, c, l, _) =>
                {
                    _call = (f, c);
                    _liquidity = l;
                })
                .ReturnsAsync((ChannelId id, uint _, LightningMoney? _, LiquidityRequest? _, CancellationToken _) =>
                                  new DualFundedOpenResult(id, s_txId) { Purchase = _purchase });
    }

    [Fact]
    public async Task Given_ABump_When_Handled_Then_TheServiceGetsTheFeerateAndContributionAndTheTxIdComesBack()
    {
        // Act
        var response = await GetHandler().HandleAsync(
                           CreateEnvelope(new BumpOpenIpcRequest
                           {
                               ChannelId = s_channelId,
                               FeeRatePerKw = 2_604,
                               ContributionSat = 650_000
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        var payload = MessagePackSerializer.Deserialize<BumpOpenIpcResponse>(response.Payload, s_options,
                                                                            TestContext.Current.CancellationToken);
        Assert.Equal(s_channelId, payload.ChannelId);
        Assert.Equal("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100", payload.FundingTxId);
        Assert.Equal((2_604U, LightningMoney.Satoshis(650_000)), _call);
    }

    [Fact]
    public async Task Given_ABumpWithoutAContribution_When_Handled_Then_OursIsKept()
    {
        // Act
        await GetHandler().HandleAsync(CreateEnvelope(new BumpOpenIpcRequest
        {
            ChannelId = s_channelId,
            FeeRatePerKw = 3_000
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((3_000U, (LightningMoney?)null), _call);
    }

    [Fact]
    public async Task Given_AZeroContribution_When_Handled_Then_TheServiceDecides()
    {
        // Act: NL-530, an accepter that stops contributing (the service refuses 0 for the opener)
        await GetHandler().HandleAsync(CreateEnvelope(new BumpOpenIpcRequest
        {
            ChannelId = s_channelId,
            FeeRatePerKw = 3_000,
            ContributionSat = 0
        }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal((3_000U, LightningMoney.Zero), _call);
    }

    [Theory]
    [InlineData("RBF of a dual-funded open is not enabled")]
    [InlineData("[IT-RBF-01] 2550 sat/kw is below the minimum 2604 sat/kw")]
    [InlineData("channel_ready was already sent or received")]
    public async Task Given_TheServiceRefuses_When_Handled_Then_InvalidOperationWithItsReason(string reason)
    {
        // Arrange
        _service.Setup(s => s.BumpAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(), It.IsAny<LightningMoney?>(),
                                        It.IsAny<LiquidityRequest?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(reason));

        // Act
        var response = await GetHandler().HandleAsync(
                           CreateEnvelope(new BumpOpenIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Equal(reason, error.Message);
    }

    [Fact]
    public async Task Given_ThePeerAbortsTheRbf_When_Handled_Then_InvalidOperationWithThePeersReason()
    {
        // Arrange
        _service.Setup(s => s.BumpAsync(It.IsAny<ChannelId>(), It.IsAny<uint>(), It.IsAny<LightningMoney?>(),
                                        It.IsAny<LiquidityRequest?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DualFundedOpenResult(s_channelId, null, "peer sent tx_abort: not today"));

        // Act
        var response = await GetHandler().HandleAsync(
                           CreateEnvelope(new BumpOpenIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not today", error.Message);
    }

    [Theory]
    [InlineData(252U, null)]
    [InlineData(250_001U, null)]
    [InlineData(2_604U, 2_100_000_000_000_001UL)]
    public async Task Given_ArgumentsOutOfBounds_When_Handled_Then_RefusedBeforeTheService(uint feeRatePerKw,
                                                                                          ulong? contributionSat)
    {
        // Act
        var response = await GetHandler().HandleAsync(
                           CreateEnvelope(new BumpOpenIpcRequest
                           {
                               ChannelId = s_channelId,
                               FeeRatePerKw = feeRatePerKw,
                               ContributionSat = contributionSat
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, AssertError(response).Code);
        Assert.Null(_call);
    }

    [Fact]
    public async Task Given_ANodeWithoutDualFunding_When_Handled_Then_NotAvailable()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDualFundIpcServices();
        var handler = services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                              .Single(h => h.Command == ClientCommand.BumpOpen);

        // Act
        var response = await handler.HandleAsync(
                           CreateEnvelope(new BumpOpenIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604 }),
                           TestContext.Current.CancellationToken);

        // Assert
        var error = AssertError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("not available", error.Message);
    }

    [Theory]
    [InlineData(new[] { ChannelIdHex, "2604" }, 2604U, null)]
    [InlineData(new[] { ChannelIdHex, "2604", "--contribution-sat", "650000" }, 2604U, 650000UL)]
    [InlineData(new[] { "--contribution-sat=1", ChannelIdHex, "253" }, 253U, 1UL)]
    [InlineData(new[] { ChannelIdHex, "2604", "--contribution-sat", "0" }, 2604U, 0UL)]
    public void Given_ValidArguments_When_Parsed_Then_ChannelFeerateAndContribution(string[] args, uint feeRate,
                                                                                    ulong? contributionSat)
    {
        // Act
        var parsed = BumpOpenCommands.Parse(args, out var error);

        // Assert
        Assert.Null(error);
        Assert.Equal(new BumpOpenArguments(s_channelId, feeRate, contributionSat), parsed);
        Assert.Null(ClientApp.ValidateArguments("bumpopen", args));
        Assert.Null(ClientApp.ValidateArguments("bump-open", args));
    }

    [Theory]
    [InlineData(new string[0], "Missing arguments.")]
    [InlineData(new[] { "abcd", "2604" }, "Invalid channel id")]
    [InlineData(new[] { ChannelIdHex, "252" }, "Invalid feerate '252'")]
    [InlineData(new[] { ChannelIdHex, "2604", "extra" }, "Unexpected argument 'extra'")]
    [InlineData(new[] { ChannelIdHex, "2604", "--contribution-sat", "-1" }, "Invalid contribution '-1'")]
    [InlineData(new[] { ChannelIdHex, "2604", "--contribution-sat" }, "Missing value for --contribution-sat")]
    [InlineData(new[] { ChannelIdHex, "2604", "--max-fee-sat", "1" }, "Unknown option '--max-fee-sat'")]
    public void Given_BadArguments_When_Validated_Then_UsageError(string[] args, string expected)
    {
        // Act
        var error = ClientApp.ValidateArguments("bumpopen", args);

        // Assert
        Assert.NotNull(error);
        Assert.Contains(expected, error);
        Assert.Contains(BumpOpenCommands.Usage, error);
    }

    [Fact]
    public void Given_ABumpedOpen_When_Printed_Then_TheNewFundingAndTheFollowRule()
    {
        // Arrange
        using var output = new StringWriter();

        // Act
        BumpOpenCommands.Print(new BumpOpenIpcResponse { ChannelId = s_channelId, FundingTxId = "c2" }, output);

        // Assert
        var printed = output.ToString();
        Assert.StartsWith("Dual-funded open RBF signed", printed);
        Assert.Contains("Funding TxId:    c2", printed);
        Assert.Contains("follows the one that does", printed);
    }

    [Fact]
    public async Task Given_RequestInbound_When_Handled_Then_TheServiceBuysAndThePurchaseComesBack()
    {
        // Arrange (liquidity ads, NL-771: keys 3/4 of the request, key 2 of the response)
        _purchase = LiquidityAdsTestData.Purchase(s_channelId, kind: Domain.LiquidityAds.Enums.LiquidityPurchaseKind.OpenRbf);

        // Act
        var response = await GetHandler().HandleAsync(
                           CreateEnvelope(new BumpOpenIpcRequest
                           {
                               ChannelId = s_channelId,
                               FeeRatePerKw = 2_604,
                               RequestInboundSat = 400_000,
                               MaxLiquidityFeeSat = 9_000
                           }), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        Assert.Equal(new LiquidityRequest(400_000, null, 9_000), _liquidity);
        var payload = MessagePackSerializer.Deserialize<BumpOpenIpcResponse>(response.Payload, s_options,
                                                                            TestContext.Current.CancellationToken);
        Assert.NotNull(payload.Purchase);
        Assert.Equal(410_000UL, payload.Purchase.ContributedSat);
    }

    [Fact]
    public void Given_ARequest_When_RoundTripped_Then_Keys0To2Kept()
    {
        // Arrange
        var request = new BumpOpenIpcRequest { ChannelId = s_channelId, FeeRatePerKw = 2_604, ContributionSat = 5 };

        // Act
        var bytes = MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken);
        var read = MessagePackSerializer.Deserialize<BumpOpenIpcRequest>(bytes, s_options,
                                                                         TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(s_channelId, read.ChannelId);
        Assert.Equal(2_604U, read.FeeRatePerKw);
        Assert.Equal(5UL, read.ToClientRequest().ContributionSat);
    }

    private IIpcCommandHandler GetHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_service.Object);
        services.AddDualFundIpcServices();
        return services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                       .Single(h => h.Command == ClientCommand.BumpOpen);
    }

    private static IpcEnvelope CreateEnvelope(BumpOpenIpcRequest request) =>
        new()
        {
            Version = 1,
            Command = ClientCommand.BumpOpen,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request, s_options)
        };

    private static IpcError AssertError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options);
    }
}