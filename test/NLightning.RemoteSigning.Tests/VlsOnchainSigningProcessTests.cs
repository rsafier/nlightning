using System.Text.Json;
using NBitcoin;
using NBitcoin.Crypto;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Onchain.Enums;
using NLightning.Domain.Onchain.Models;
using NLightning.Infrastructure.Bitcoin.Outputs;
using NLightning.Infrastructure.Bitcoin.Services;
using NLightning.Infrastructure.VlsSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>
/// The pinned Rust gateway's BOLT 5 commands against a configured VLS channel: each signature is checked against the
/// key BOLT 3 derives from the channel's VLS basepoints, and VLS's sweep policy refuses what does not pay its wallet.
/// </summary>
public sealed class VlsOnchainSigningProcessTests
{
    private const string Peer = "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798";
    private const ushort ContestDelay = 144;
    private const long AmountSat = 100_000;
    private const long FeeSat = 2_000;

    private static readonly KeyDerivationService s_keys = new();

    [Fact(Explicit = true)]
    public async Task Given_APeerCommitmentHtlc_When_VlsSignsItsClaim_Then_TheSignatureIsOurHtlcKeyAndOnlyOurWalletIsPaid()
    {
        // Arrange: an HTLC the peer offered on its commitment (its point), claimed by preimage to our wallet
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var channel = await SetupChannelAsync(gateway, 1, "StaticRemoteKey", ct);
        using var peerPointKey = new Key();
        var point = new CompactPubKey(peerPointKey.PubKey.ToBytes());
        var ourHtlc = new PubKey(s_keys.DerivePublicKey(channel.Htlc, point));
        var htlc = new OfferedHtlcOutput(LightningMoney.Satoshis(AmountSat), 500, false, new Key().PubKey,
                                         RandomUtils.GetBytes(32), ourHtlc,
                                         new PubKey(s_keys.DeriveRevocationPubKey(channel.Revocation, point)));
        var redeem = new Script((byte[])htlc.RedeemBitcoinScript);
        var wallet = await WalletScriptAsync(gateway, 0, ct);
        var claim = Sweep(wallet, 0xFFFFFFFD);

        // Act
        var signed = await ExchangeAsync(gateway, new
        {
            op = "sign_counterparty_htlc_sweep",
            channel = channel.Id,
            transaction = claim.ToHex(),
            input = 0,
            point = point.ToString(),
            redeemscript = redeem.ToHex(),
            amount_sat = AmountSat,
            wallet_path = "m/0"
        }, ct);
        var elsewhere = await ExchangeAsync(gateway, new
        {
            op = "sign_counterparty_htlc_sweep",
            channel = channel.Id,
            transaction = Sweep(new Key().PubKey.WitHash.ScriptPubKey, 0xFFFFFFFD).ToHex(),
            input = 0,
            point = point.ToString(),
            redeemscript = redeem.ToHex(),
            amount_sat = AmountSat,
            wallet_path = "m/0"
        }, ct);

        // Assert
        Assert.True(Verifies(ourHtlc, claim, redeem, signed), signed.ToString());
        Assert.False(elsewhere.GetProperty("ok").GetBoolean());
        Assert.Contains("destination", elsewhere.GetProperty("error").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_ARevealedPeerSecret_When_VlsSignsThePenalty_Then_TheSignatureIsOurRevocationKey()
    {
        // Arrange: the peer's revoked to_local (its delayed key, our revocation key) and its revealed secret
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var channel = await SetupChannelAsync(gateway, 1, "StaticRemoteKey", ct);
        using var secret = new Key();
        var point = new CompactPubKey(secret.PubKey.ToBytes());
        var revocation = new PubKey(s_keys.DeriveRevocationPubKey(channel.Revocation, point));
        var toLocal = new ToLocalOutput(LightningMoney.Satoshis(AmountSat), new Key().PubKey, revocation, ContestDelay);
        var redeem = new Script((byte[])toLocal.RedeemBitcoinScript);
        var penalty = Sweep(await WalletScriptAsync(gateway, 2, ct), 0xFFFFFFFD);

        // Act
        var signed = await ExchangeAsync(gateway, new
        {
            op = "sign_justice_sweep",
            channel = channel.Id,
            transaction = penalty.ToHex(),
            input = 0,
            secret = Convert.ToHexStringLower(secret.ToBytes()),
            redeemscript = redeem.ToHex(),
            amount_sat = AmountSat,
            wallet_path = "m/2"
        }, ct);

        // Assert
        Assert.True(Verifies(revocation, penalty, redeem, signed), signed.ToString());
    }

    [Fact(Explicit = true)]
    public async Task Given_OurStaticToRemote_When_VlsSignsItsSweep_Then_ThePaymentBasepointSignsForOurWalletOnly()
    {
        // Arrange: our to_remote on a peer commitment is P2WPKH to the payment basepoint (option_static_remotekey)
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var channel = await SetupChannelAsync(gateway, 1, "StaticRemoteKey", ct);
        var payment = new PubKey(channel.Payment);
        var sweep = Sweep(await WalletScriptAsync(gateway, 1, ct), 0xFFFFFFFD);

        // Act
        var signed = await ExchangeAsync(gateway, new
        {
            op = "sign_to_remote_sweep",
            channel = channel.Id,
            transaction = sweep.ToHex(),
            input = 0,
            amount_sat = AmountSat,
            wallet_path = "m/1"
        }, ct);
        var elsewhere = await ExchangeAsync(gateway, new
        {
            op = "sign_to_remote_sweep",
            channel = channel.Id,
            transaction = Sweep(new Key().PubKey.WitHash.ScriptPubKey, 0xFFFFFFFD).ToHex(),
            input = 0,
            amount_sat = AmountSat,
            wallet_path = "m/1"
        }, ct);

        // Assert: BIP 143 P2WPKH sighash (script code = the P2PKH script of the key)
        var hash = sweep.GetSignatureHash(payment.Hash.ScriptPubKey, 0, SigHash.All,
                                          new TxOut(Money.Satoshis(AmountSat), payment.WitHash.ScriptPubKey),
                                          HashVersion.WitnessV0);
        Assert.True(signed.GetProperty("ok").GetBoolean(), signed.ToString());
        Assert.True(payment.Verify(hash, Signature(signed)));
        Assert.False(elsewhere.GetProperty("ok").GetBoolean());
        Assert.Contains("not in the VLS wallet", elsewhere.GetProperty("error").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_NoCommitmentSignedForBroadcast_When_HolderHtlcOrDelayedSweepIsAsked_Then_VlsRefuses()
    {
        // Arrange: a configured channel whose holder commitment was never signed for broadcast
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var channel = await SetupChannelAsync(gateway, 1, "StaticRemoteKey", ct);
        var point = await PointAsync(gateway, channel.Id, 0, ct);
        var transaction = Sweep(await WalletScriptAsync(gateway, 0, ct), ContestDelay).ToHex();

        // Act
        var htlc = await ExchangeAsync(gateway, new
        {
            op = "sign_holder_htlc",
            channel = channel.Id,
            transaction,
            point,
            redeemscript = "00",
            amount_sat = AmountSat
        }, ct);
        var delayed = await ExchangeAsync(gateway, new
        {
            op = "sign_delayed_sweep",
            channel = channel.Id,
            transaction,
            input = 0,
            point,
            redeemscript = "00",
            amount_sat = AmountSat,
            wallet_path = "m/0"
        }, ct);

        // Assert
        Assert.Contains("signed for broadcast", htlc.GetProperty("error").GetString());
        Assert.Contains("signed for broadcast", delayed.GetProperty("error").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_AnAnchorsToRemote_When_VlsSignsItsSweep_Then_ThePaymentKeySignsTheOneCsvScriptOnly()
    {
        // Arrange: with anchors our to_remote is P2WSH <payment_basepoint> OP_CHECKSIGVERIFY 1 OP_CSV, spent at nSequence 1
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var channel = await SetupChannelAsync(gateway, 1, "AnchorsZeroFeeHtlc", ct);
        var payment = new PubKey(channel.Payment);
        var toRemote = new Script(Op.GetPushOp(payment.ToBytes()), OpcodeType.OP_CHECKSIGVERIFY, OpcodeType.OP_1,
                                  OpcodeType.OP_CHECKSEQUENCEVERIFY);
        var sweep = Sweep(await WalletScriptAsync(gateway, 0, ct), 1);

        // Act
        var signed = await ExchangeAsync(gateway, new
        {
            op = "sign_to_remote_sweep",
            channel = channel.Id,
            transaction = sweep.ToHex(),
            input = 0,
            amount_sat = AmountSat,
            wallet_path = "m/0",
            redeemscript = toRemote.ToHex()
        }, ct);
        var otherScript = await ExchangeAsync(gateway, new
        {
            op = "sign_to_remote_sweep",
            channel = channel.Id,
            transaction = sweep.ToHex(),
            input = 0,
            amount_sat = AmountSat,
            wallet_path = "m/0",
            redeemscript = new Script(Op.GetPushOp(new Key().PubKey.ToBytes()), OpcodeType.OP_CHECKSIGVERIFY,
                                      OpcodeType.OP_1, OpcodeType.OP_CHECKSEQUENCEVERIFY).ToHex()
        }, ct);

        // Assert
        Assert.True(Verifies(payment, sweep, toRemote, signed), signed.ToString());
        Assert.False(otherScript.GetProperty("ok").GetBoolean());
        Assert.Contains("not VLS's", otherScript.GetProperty("error").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_AnAnchorsPeerCommitmentHtlc_When_VlsSignsItsClaim_Then_OnlyTheOneCsvSequenceIsAccepted()
    {
        // Arrange: an anchors HTLC the peer offered, claimed by preimage at nSequence 1 (the CSV of anchors HTLC scripts)
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var channel = await SetupChannelAsync(gateway, 1, "AnchorsZeroFeeHtlc", ct);
        using var peerPointKey = new Key();
        var point = new CompactPubKey(peerPointKey.PubKey.ToBytes());
        var ourHtlc = new PubKey(s_keys.DerivePublicKey(channel.Htlc, point));
        var htlc = new OfferedHtlcOutput(LightningMoney.Satoshis(AmountSat), 500, true, new Key().PubKey,
                                         RandomUtils.GetBytes(32), ourHtlc,
                                         new PubKey(s_keys.DeriveRevocationPubKey(channel.Revocation, point)));
        var redeem = new Script((byte[])htlc.RedeemBitcoinScript);
        var wallet = await WalletScriptAsync(gateway, 0, ct);
        object Claim(Transaction tx) => new
        {
            op = "sign_counterparty_htlc_sweep",
            channel = channel.Id,
            transaction = tx.ToHex(),
            input = 0,
            point = point.ToString(),
            redeemscript = redeem.ToHex(),
            amount_sat = AmountSat,
            wallet_path = "m/0"
        };
        var claim = Sweep(wallet, 1);

        // Act
        var signed = await ExchangeAsync(gateway, Claim(claim), ct);
        var rbfSequence = await ExchangeAsync(gateway, Claim(Sweep(wallet, 0xFFFFFFFD)), ct);

        // Assert
        Assert.True(Verifies(ourHtlc, claim, redeem, signed), signed.ToString());
        Assert.False(rbfSequence.GetProperty("ok").GetBoolean());
        Assert.Contains("sequence", rbfSequence.GetProperty("error").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_AnAnchorsChannelWithoutBroadcast_When_HolderHtlcFeeInputsAreAsked_Then_VlsRefuses()
    {
        // Arrange: the fee inputs of our anchors HTLC transaction need the commitment signed for broadcast
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var anchors = await SetupChannelAsync(gateway, 1, "AnchorsZeroFeeHtlc", ct);
        var legacy = await SetupChannelAsync(gateway, 2, "StaticRemoteKey", ct);
        var tx = Sweep(await WalletScriptAsync(gateway, 0, ct), 1);
        tx.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 1), null, null, Sequence.Final);
        tx.Outputs.Add(Money.Satoshis(10_000), await WalletScriptAsync(gateway, 1, ct));
        object FeeInputs(string channel) => new
        {
            op = "sign_holder_htlc_fee_inputs",
            channel,
            transaction = tx.ToHex(),
            point = new Key().PubKey.ToHex(),
            redeemscript = "00",
            amount_sat = AmountSat,
            input_paths = new[] { "m", "m/2" },
            prev_outputs = new[]
            {
                new { value = AmountSat, script_pubkey = "0020" + new string('0', 64) },
                new { value = 20_000L, script_pubkey = "0014" + new string('0', 40) }
            },
            output_paths = new[] { "m", "m/1" }
        };

        // Act
        var withoutBroadcast = await ExchangeAsync(gateway, FeeInputs(anchors.Id), ct);
        var notAnchors = await ExchangeAsync(gateway, FeeInputs(legacy.Id), ct);

        // Assert
        Assert.Contains("signed for broadcast", withoutBroadcast.GetProperty("error").GetString());
        Assert.Contains("only an anchors HTLC transaction", notAnchors.GetProperty("error").GetString());
    }

    [Fact(Explicit = true)]
    public async Task Given_AnUnsupportedOrForeignSweep_When_TheAdapterIsAsked_Then_ItRefusesBeforeVls()
    {
        // Arrange: no channel mapping is needed, the adapter refuses before it names a VLS channel
        var ct = TestContext.Current.CancellationToken;
        await using var gateway = new VlsGatewayFixture();
        await gateway.InitializeAsync(ct);
        var connection = new VlsSignerConnection(new VlsSignerOptions
        { SocketPath = gateway.SocketPath, TokenFile = gateway.TokenFile });
        var signer = new VlsLightningSigner(connection, new VlsChannelMappingRegistry(connection, null!));
        var channelId = new ChannelId(new byte[32]);
        var foreign = Sweep(new Key().PubKey.WitHash.ScriptPubKey, 0xFFFFFFFD).ToBytes();
        var point = new CompactPubKey(new Key().PubKey.ToBytes());
        var withFeeInput = Transaction.Load(foreign, Network.RegTest);
        withFeeInput.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0));
        var twoInputs = new HtlcTransactionBuildResult(
            new SignedTransaction(new TxId(new byte[32]), withFeeInput.ToBytes()), new BitcoinScript([0x51]),
            LightningMoney.Satoshis(AmountSat));

        // Act and assert
        Assert.Throws<SignerException>(() => signer.SignSweepInput(channelId,
            new SweepSigningContext(foreign, 0, [0x51], AmountSat, SweepKeyKind.HtlcRemotePoint, point)));
        Assert.Throws<NotSupportedException>(() => signer.SignSweepInput(channelId,
            new SweepSigningContext(foreign, 0, [0x51], AmountSat, SweepKeyKind.HtlcRemotePoint, point,
                                    TaprootSpentOutputs: [])));
        // Without anchors an HTLC transaction is the pre-signed one input, one output pair: nothing else is signed
        Assert.Throws<SignerException>(() => signer.SignLocalHtlcTransaction(channelId,
            new HtlcSigningContext(twoInputs, point, HasAnchors: false)));
    }

    private sealed record VlsChannel(string Id, CompactPubKey Revocation, CompactPubKey Payment, CompactPubKey Htlc);

    private static async Task<VlsChannel> SetupChannelAsync(VlsGatewayFixture gateway, ulong dbid, string type,
                                                            CancellationToken ct)
    {
        var allocated = await ResultAsync(gateway, new { op = "allocate", peer = Peer, dbid }, ct);
        var id = allocated.GetProperty("channel").GetString()!;
        var setup = new
        {
            op = "setup",
            channel = id,
            setup = new
            {
                is_outbound = true,
                channel_value_sat = 1_000_000UL,
                push_value_msat = 0UL,
                funding_outpoint = new { txid = Convert.ToHexStringLower(RandomUtils.GetBytes(32)), vout = 0 },
                holder_selected_contest_delay = ContestDelay,
                counterparty_selected_contest_delay = ContestDelay,
                holder_shutdown_script = (byte[]?)null,
                counterparty_shutdown_script = (byte[]?)null,
                commitment_type = type,
                counterparty_points = new
                {
                    funding_pubkey = new Key().PubKey.ToHex(),
                    revocation_basepoint = new Key().PubKey.ToHex(),
                    payment_point = new Key().PubKey.ToHex(),
                    delayed_payment_basepoint = new Key().PubKey.ToHex(),
                    htlc_basepoint = new Key().PubKey.ToHex()
                }
            }
        };
        await ResultAsync(gateway, setup, ct);
        return new VlsChannel(id, Point(allocated, "revocation"), Point(allocated, "payment"), Point(allocated, "htlc"));
    }

    private static CompactPubKey Point(JsonElement element, string name) =>
        new(Convert.FromHexString(element.GetProperty(name).GetString()!));

    private static async Task<string> PointAsync(VlsGatewayFixture gateway, string channel, ulong number,
                                                 CancellationToken ct) =>
        (await ResultAsync(gateway, new { op = "point", channel, number }, ct)).GetProperty("point").GetString()!;

    private static async Task<Script> WalletScriptAsync(VlsGatewayFixture gateway, uint index, CancellationToken ct)
    {
        var key = await ResultAsync(gateway, new { op = "wallet_public_key", index }, ct);
        return new PubKey(key.GetProperty("public_key").GetString()!).WitHash.ScriptPubKey;
    }

    /// <summary>A version 2 one-input sweep paying <paramref name="destination"/> at about 3,600 sat/kw.</summary>
    private static Transaction Sweep(Script destination, uint sequence)
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Version = 2;
        tx.LockTime = LockTime.Zero;
        tx.Inputs.Add(new OutPoint(new uint256(RandomUtils.GetBytes(32)), 0), null, null, new Sequence(sequence));
        tx.Outputs.Add(Money.Satoshis(AmountSat - FeeSat), destination);
        return tx;
    }

    private static bool Verifies(PubKey key, Transaction tx, Script redeem, JsonElement signed)
    {
        Assert.True(signed.GetProperty("ok").GetBoolean(), signed.ToString());
        var hash = tx.GetSignatureHash(redeem, 0, SigHash.All,
                                       new TxOut(Money.Satoshis(AmountSat), redeem.WitHash.ScriptPubKey),
                                       HashVersion.WitnessV0);
        return key.Verify(hash, Signature(signed));
    }

    private static ECDSASignature Signature(JsonElement signed) =>
        ECDSASignature.TryParseFromCompact(
            Convert.FromHexString(signed.GetProperty("result").GetProperty("signature").GetString()!), out var parsed)
            ? parsed
            : throw new InvalidOperationException("VLS returned an invalid compact signature.");

    private static Task<JsonElement> ExchangeAsync(VlsGatewayFixture gateway, object command, CancellationToken ct) =>
        gateway.ExchangeAsync(command, ct: ct);

    private static async Task<JsonElement> ResultAsync(VlsGatewayFixture gateway, object command, CancellationToken ct)
    {
        var reply = await gateway.ExchangeAsync(command, ct: ct);
        Assert.True(reply.GetProperty("ok").GetBoolean(), reply.ToString());
        return reply.GetProperty("result");
    }
}