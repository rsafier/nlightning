namespace NLightning.Application.Tests.Channels.DualFunding;

using Domain.Channels.DualFunding.Models;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Tlv;
using InteractiveTx.TestDoubles;

/// <summary>
/// The peer's <c>upfront_shutdown_script</c> in <c>open_channel2</c> and <c>accept_channel2</c> (NL-776): a non-empty
/// script must be a <c>shutdown</c> form the negotiated features allow (BOLT 2), so CLN v26.06.8's P2TR script is
/// refused without <c>option_shutdown_anysegwit</c> and kept with it.
/// </summary>
public class DualFundUpfrontShutdownScriptTests
{
    private static readonly LightningMoney s_aliceShare = LightningMoney.Satoshis(600_000);
    private static readonly byte[] s_p2Tr = Convert.FromHexString("5120" + new string('e', 64));

    [Fact]
    public async Task Given_AP2TrScriptWithoutAnySegwit_When_OpenChannel2Arrives_Then_BobRefusesTheOpen()
    {
        // Arrange
        await using var harness = await CreateAsync(FeatureSupport.No);
        var open = StartOpen(harness);
        var message = await TakeAsync<OpenChannel2Message>(harness, harness.Alice);

        // Act
        await harness.DeliverAsync(harness.Alice, WithScript(message));
        var result = await harness.RunAsync(open);

        // Assert
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Bob.Errors));
        Assert.Contains("upfront_shutdown_script", error.Message);
        Assert.NotNull(result.FailureReason);
        Assert.Empty(harness.Bob.Memory.FindChannels(_ => true));
    }

    [Fact]
    public async Task Given_AP2TrScriptWithAnySegwit_When_OpenChannel2Arrives_Then_BobKeepsIt()
    {
        // Arrange
        await using var harness = await CreateAsync(FeatureSupport.Optional);
        var open = StartOpen(harness);
        var message = await TakeAsync<OpenChannel2Message>(harness, harness.Alice);

        // Act
        await harness.DeliverAsync(harness.Alice, WithScript(message));
        var result = await harness.RunAsync(open);

        // Assert
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        Assert.Equal(s_p2Tr, (byte[])harness.Bob.Channel(result.ChannelId).RemoteUpfrontShutdownScript!.Value);
    }

    [Fact]
    public async Task Given_AP2TrScriptWithoutAnySegwit_When_AcceptChannel2Arrives_Then_AliceFailsTheOpen()
    {
        // Arrange
        await using var harness = await CreateAsync(FeatureSupport.No);
        var open = StartOpen(harness);
        var accept = await TakeAsync<AcceptChannel2Message>(harness, harness.Bob);

        // Act
        await harness.DeliverAsync(harness.Bob, WithScript(accept));
        var result = await harness.RunAsync(open);

        // Assert: Alice failed the open before she funded anything
        var error = Assert.IsType<ChannelErrorException>(Assert.Single(harness.Alice.Errors));
        Assert.Contains("upfront_shutdown_script", error.Message);
        Assert.NotNull(result.FailureReason);
        Assert.DoesNotContain(harness.Transcript, t => t is { From: "Alice", Message: TxAddInputMessage });
        Assert.Empty(harness.Alice.Memory.FindChannels(_ => true));
    }

    [Fact]
    public async Task Given_AP2TrScriptWithAnySegwit_When_AcceptChannel2Arrives_Then_AliceKeepsIt()
    {
        // Arrange
        await using var harness = await CreateAsync(FeatureSupport.Optional);
        var open = StartOpen(harness);
        var accept = await TakeAsync<AcceptChannel2Message>(harness, harness.Bob);

        // Act
        await harness.DeliverAsync(harness.Bob, WithScript(accept));
        var result = await harness.RunAsync(open);

        // Assert
        Assert.True(result.FailureReason is null, $"{result.FailureReason}\n{harness.Describe()}");
        Assert.Equal(s_p2Tr, (byte[])harness.Alice.Channel(result.ChannelId).RemoteUpfrontShutdownScript!.Value);
    }

    private static async Task<DualFundHarness> CreateAsync(FeatureSupport anySegwit)
    {
        var harness = await DualFundHarness.CreateAsync(0, TimeSpan.FromSeconds(1));
        harness.Alice.Wallet.Utxos.Add(WalletUtxo.Create(1_000_000));
        harness.NegotiatedFeatures = new FeatureOptions
        {
            DualFund = FeatureSupport.Optional,
            BeyondSegwitShutdown = anySegwit
        };

        return harness;
    }

    private static Task<DualFundedOpenResult> StartOpen(DualFundHarness harness) =>
        harness.Alice.DualFund.OpenAsync(new DualFundedOpenRequest(harness.Bob.NodeId, s_aliceShare),
                                         TestContext.Current.CancellationToken);

    private static OpenChannel2Message WithScript(OpenChannel2Message message) =>
        new(message.Payload, new UpfrontShutdownScriptTlv(s_p2Tr), message.ChannelTypeTlv,
            message.RequireConfirmedInputsTlv, message.RequestFundingTlv);

    private static AcceptChannel2Message WithScript(AcceptChannel2Message message) =>
        new(message.Payload, new UpfrontShutdownScriptTlv(s_p2Tr), message.ChannelTypeTlv,
            message.RequireConfirmedInputsTlv, message.ProvideFundingTlv);

    /// <summary>Pumps until <paramref name="from"/> queued a <typeparamref name="T"/>, then takes it undelivered.</summary>
    private static async Task<T> TakeAsync<T>(DualFundHarness harness, DualFundNode from) where T : IChannelMessage
    {
        for (var i = 0; i < 500; i++)
        {
            await harness.PumpAsync((sender, message) => sender == from.Name && message is T);
            if (harness.TakeNext(from) is { } next)
                return Assert.IsType<T>(next);

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException($"{from.Name} sent no {typeof(T).Name}\n{harness.Describe()}");
    }
}