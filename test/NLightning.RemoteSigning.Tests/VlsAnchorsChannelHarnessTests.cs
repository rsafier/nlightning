using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Application.Channels.Services;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Constants;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Enums;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Signing.Recovery;
using NLightning.Domain.Signing.Vls;
using NLightning.Infrastructure.VlsSigning;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.RemoteSigning.Tests;

/// <summary>
/// Zero-fee-HTLC anchors channels in VLS mode: the real policy gateway (VLS <c>AnchorsZeroFeeHtlc</c>) signs Alice's
/// side, Bob runs the native signer and verifies every commitment and HTLC signature (SIGHASH_SINGLE|ANYONECANPAY).
/// </summary>
public sealed class VlsAnchorsChannelHarnessTests
{
    [Fact(Explicit = true)]
    public async Task Given_AnAnchorsChannel_When_FractionalPaymentsFlowBothWays_Then_PeersVerifyAndExactMsatBalancesConverge()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var approval = new VlsPaymentApprovalClient(gateway.ApprovalSocketPath, gateway.ApprovalTokenFile);
        await using var harness = await CreateAnchorsHarnessAsync(gateway);
        var (id, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000));
        await harness.ConfirmFundingAsync(id, funding.TransactionId);
        Assert.True(harness.Alice.Channel(id).ChannelParams.OptionAnchorOutputs);
        Assert.True(harness.Bob.Channel(id).ChannelParams.OptionAnchorOutputs);
        var start = harness.Alice.Channel(id).Commitments!.LocalBalanceMsat;

        // Act: sequential fractional payments, then fractional HTLCs in flight both ways in one commitment (above the
        // trimming threshold: the zero-dust restriction stays)
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(300_000_123),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.PumpAsync();
        await harness.Bob.PayAsync(harness.Alice, id, LightningMoney.MilliSatoshis(12_345_567));
        await harness.PumpAsync();
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(20_000_999),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.Alice.PayAsync(harness.Bob, id, LightningMoney.MilliSatoshis(30_000_333),
            invoice => approval.AuthorizeInvoice(Guid.NewGuid(), invoice.Bolt11!));
        await harness.Bob.PayAsync(harness.Alice, id, LightningMoney.MilliSatoshis(40_000_777));
        await harness.PumpAsync();

        // Assert: exact msat accounting on both sides, every commitment (with HTLC signatures) verified by the peer
        var alice = harness.Alice.Channel(id).Commitments!;
        var bob = harness.Bob.Channel(id).Commitments!;
        Assert.True(alice.Params.OptionAnchors && bob.Params.OptionAnchors);
        Assert.True(alice.Htlcs.IsEmpty && bob.Htlcs.IsEmpty);
        Assert.Equal(start - 300_000_123 + 12_345_567 - 20_000_999 - 30_000_333 + 40_000_777, alice.LocalBalanceMsat);
        Assert.Equal(alice.LocalBalanceMsat, bob.RemoteBalanceMsat);
        Assert.Equal(alice.RemoteBalanceMsat, bob.LocalBalanceMsat);
        Assert.NotEqual(0UL, alice.LocalBalanceMsat % 1_000);
        Assert.NotEmpty(harness.Alice.Verified);
        Assert.NotEmpty(harness.Bob.Verified);
        // Alice's VLS-signed commitment_signed carries HTLC signatures Bob checked as SINGLE|ANYONECANPAY
        Assert.Contains(harness.Sent.Where(x => x.From == "Alice").Select(x => x.Message).OfType<CommitmentSignedMessage>(),
            message => message.Payload.HtlcSignatures.Count() >= 3);
        foreach (var node in harness.Nodes)
            foreach (var commitment in node.Verified.GroupBy(x => x.Number))
                Assert.Single(commitment.Select(x => x.TxId).Distinct());
    }

    [Fact(Explicit = true)]
    public async Task Given_AnAnchorsChannel_When_TheCpfpChildIsSigned_Then_TheAnchorAndTheReservedFeeInputVerify()
    {
        // Arrange: an activated anchors channel, and a confirmed VLS wallet output reserved for the anchor child
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        await using var harness = await CreateAnchorsHarnessAsync(gateway);
        var (id, funding) = await harness.OpenAsync(LightningMoney.Satoshis(1_000_000));
        await harness.ConfirmFundingAsync(id, funding.TransactionId);
        var services = harness.Alice.Services;
        var signer = services.GetRequiredService<ILightningSigner>();
        var keys = harness.Alice.PublicKeyManager;
        var wallet = services.GetRequiredService<IUtxoMemoryRepository>();
        var feeKey = new PubKey((byte[])keys.GetWalletPublicKey(7, false, AddressType.P2Wpkh));
        var fee = new UtxoModel(new TxId(Enumerable.Repeat((byte)0x5E, 32).ToArray()), 1, LightningMoney.Satoshis(50_000),
            200, new WalletAddressModel(AddressType.P2Wpkh, 7, false, feeKey.WitHash.GetAddress(Network.RegTest).ToString()));
        wallet.Add(fee);
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee([(fee.TxId, fee.Index)], reservation));
        var fundingKey = new PubKey((byte[])harness.Alice.Channel(id).LocalFundingPubKey);
        var anchorScript = new Script(Op.GetPushOp(fundingKey.ToBytes()), OpcodeType.OP_CHECKSIG, OpcodeType.OP_IFDUP,
            OpcodeType.OP_NOTIF, OpcodeType.OP_16, OpcodeType.OP_CHECKSEQUENCEVERIFY, OpcodeType.OP_ENDIF);
        var anchor = new TxOut(Money.Satoshis(330), anchorScript.WitHash.ScriptPubKey);
        var anchorOutpoint = new OutPoint(uint256.Parse(new string('c', 64)), 2);
        var change = new PubKey((byte[])keys.GetWalletPublicKey(8, true, AddressType.P2Wpkh)).WitHash.ScriptPubKey;
        var child = Network.RegTest.CreateTransaction();
        child.Version = 2;
        child.Inputs.Add(new TxIn(anchorOutpoint) { Sequence = new Sequence(0xFFFFFFFD) });
        child.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])fee.TxId), fee.Index)) { Sequence = new Sequence(0xFFFFFFFD) });
        child.Outputs.Add(Money.Satoshis(50_330 - 5_000), change);
        var unsigned = new SignedTransaction(new TxId(child.GetHash().ToBytes()), child.ToBytes());
        var anchorSpent = new SpentOutput(new TxId(anchorOutpoint.Hash.ToBytes()), anchorOutpoint.N,
            TransactionConstants.AnchorOutputAmount, new BitcoinScript(anchor.ScriptPubKey.ToBytes()));

        // Act
        var anchorSignature = signer.SignAnchorInput(id, unsigned, 0, TransactionConstants.AnchorOutputAmount);
        Assert.True(signer.SignWalletTransaction(unsigned, reservation, [anchorSpent]));

        // Assert: both inputs verify under consensus script rules, and the txid did not change
        var signed = Transaction.Load(unsigned.RawTxBytes, Network.RegTest);
        Assert.Equal(child.GetHash(), signed.GetHash());
        Assert.Equal(WitScript.Empty, signed.Inputs[0].WitScript);
        NBitcoin.Crypto.ECDSASignature.TryParseFromCompact(anchorSignature.Value, out var parsed);
        signed.Inputs[0].WitScript = new WitScript([new TransactionSignature(parsed, SigHash.All).ToBytes(),
                                                    anchorScript.ToBytes()]);
        var validator = signed.CreateValidator([anchor, new TxOut(Money.Satoshis(50_000), feeKey.WitHash.ScriptPubKey)]);
        Assert.Null(validator.ValidateInput(0).Error);
        Assert.Null(validator.ValidateInput(1).Error);

        // Assert: the wrong amount, another reservation and an unknown destination are refused
        Assert.Throws<SignerException>(() =>
            signer.SignAnchorInput(id, unsigned, 0, LightningMoney.Satoshis(331)));
        Assert.Throws<SignerException>(() =>
            signer.SignWalletTransaction(new SignedTransaction(unsigned.TxId, child.ToBytes()), Guid.NewGuid(), [anchorSpent]));
        var foreign = child.Clone();
        foreign.Outputs[0].ScriptPubKey = new Key().PubKey.WitHash.ScriptPubKey;
        var refusal = Assert.Throws<SignerException>(() => signer.SignWalletTransaction(
            new SignedTransaction(new TxId(foreign.GetHash().ToBytes()), foreign.ToBytes()), reservation, [anchorSpent]));
        Assert.Contains("unknown", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // Act / Assert: the reclaim of the reserved fee input alone (an abandoned child's inputs back to the wallet)
        var reclaim = Network.RegTest.CreateTransaction();
        reclaim.Version = 2;
        reclaim.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])fee.TxId), fee.Index)) { Sequence = new Sequence(0xFFFFFFFD) });
        reclaim.Outputs.Add(Money.Satoshis(49_000), change);
        var reclaimed = new SignedTransaction(new TxId(reclaim.GetHash().ToBytes()), reclaim.ToBytes());
        Assert.True(signer.SignWalletTransaction(reclaimed));
        var signedReclaim = Transaction.Load(reclaimed.RawTxBytes, Network.RegTest);
        Assert.Null(signedReclaim.CreateValidator([new TxOut(Money.Satoshis(50_000), feeKey.WitHash.ScriptPubKey)])
                                 .ValidateInput(0).Error);
    }

    private static async Task<TaprootOpenHarness> CreateAnchorsHarnessAsync(VlsGatewayFixture gateway)
    {
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            services.Configure<CommitSchedulerOptions>(o => o.Debounce = TimeSpan.FromSeconds(1));
            node.Options.Features.OptionAnchors = FeatureSupport.Optional;
            if (node.Name != "Alice") return;
            node.Options.MaxDustHtlcExposureMsat = 0;
            node.Options.HtlcMinimumAmount = LightningMoney.Satoshis(1_000);
            node.Options.Routing.FeeBaseMsat = 1_001;
            node.Options.Routing.FeeProportionalMillionths = 1_234;
            node.PublicKeyManager = new VlsSecureKeyManager(connection);
            services.AddSingleton<ISecureKeyManager>(node.PublicKeyManager);
            services.AddSingleton(connection);
            services.AddSingleton<VlsChannelMappingRegistry>();
            services.AddSingleton<ILightningSigner>(sp => new VlsLightningSigner(connection,
                sp.GetRequiredService<VlsChannelMappingRegistry>(), sp.GetRequiredService<IChannelSigningInfoSource>(),
                sp.GetRequiredService<IUtxoMemoryRepository>()));
            services.AddSingleton<IVlsChannelSigner>(sp => (IVlsChannelSigner)sp.GetRequiredService<ILightningSigner>());
            services.AddSingleton<IVlsGossipSigner>(sp => (IVlsGossipSigner)sp.GetRequiredService<ILightningSigner>());
            services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp => new VlsSigningWorkflowCoordinator(connection,
                sp.GetRequiredService<IServiceScopeFactory>()));
        }, simpleTaproot: false);
        harness.NegotiatedFeatures.OptionAnchors = FeatureSupport.Optional;
        return harness;
    }
}