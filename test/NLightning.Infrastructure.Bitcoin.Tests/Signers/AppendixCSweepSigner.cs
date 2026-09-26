using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Crypto.Functions;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// A <see cref="LocalLightningSigner"/> whose channel basepoint secrets are the BOLT 3 Appendix C secrets, registered
/// for <see cref="ChannelId.Zero"/>: node A's payment/HTLC (<c>local_payment_basepoint_secret</c>) and delayed payment
/// (<c>local_delayed_payment_basepoint_secret</c>) secrets, or node B's payment/HTLC
/// (<c>remote_payment_basepoint_secret</c>) and revocation (<c>remote_revocation_basepoint_secret</c>) secrets. The
/// vectors give no other basepoint secret.
/// </summary>
internal sealed class AppendixCSweepSigner : LocalLightningSigner
{
    /// <summary>Appendix C <c>local_payment_basepoint_secret</c> (node A's payment and HTLC basepoint secret).</summary>
    public static readonly byte[] LocalPaymentBasepointSecret = Filled(0x11);

    /// <summary>Appendix C <c>remote_revocation_basepoint_secret</c> (node B).</summary>
    public static readonly byte[] RemoteRevocationBasepointSecret = Filled(0x22);

    /// <summary>Appendix C <c>local_delayed_payment_basepoint_secret</c> (node A).</summary>
    public static readonly byte[] LocalDelayedPaymentBasepointSecret = Filled(0x33);

    /// <summary>Appendix C <c>remote_payment_basepoint_secret</c> (node B's payment and HTLC basepoint secret).</summary>
    public static readonly byte[] RemotePaymentBasepointSecret = Filled(0x44);

    /// <summary>Appendix C <c>x_local_per_commitment_secret</c>: node A's secret of commitment 42.</summary>
    public static readonly byte[] XLocalPerCommitmentSecret =
        Convert.FromHexString("1f1e1d1c1b1a191817161514131211100f0e0d0c0b0a09080706050403020100");

    /// <summary>Appendix C <c>local_delayed_privkey</c>: node A's delayed key at its commitment 42 point.</summary>
    public static readonly byte[] LocalDelayedPrivkey =
        Convert.FromHexString("adf3464ce9c2f230fd2582fda4c6965e4993ca5524e8c9580e3df0cf226981ad");

    /// <summary>Appendix C <c>remote_privkey</c>: node B's payment (= HTLC) key at node A's commitment 42 point.</summary>
    public static readonly byte[] RemotePrivkey =
        Convert.FromHexString("8deba327a7cc6d638ab0eb025770400a6184afcba6713c210d8d10e199ff2fda");

    private readonly bool _asNodeB;

    public AppendixCSweepSigner(bool asNodeB)
        : base(new FundingOutputBuilder(), new KeyDerivationService(new Secp256K1Math()),
               NullLogger<LocalLightningSigner>.Instance, new NodeOptions(), new Mock<ISecureKeyManager>().Object,
               new Mock<IUtxoMemoryRepository>().Object)
    {
        _asNodeB = asNodeB;
        RegisterChannel(ChannelId.Zero,
                        new ChannelSigningInfo(Bolt3AppendixBVectors.ExpectedTxId.ToBytes(), 0,
                                               Bolt3AppendixBVectors.FundingSatoshis,
                                               Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(),
                                               Bolt3AppendixCVectors.NodeBFundingPubkey.ToBytes(), 0));
    }

    protected override Key GetPaymentBasepointSecret(uint channelKeyIndex) =>
        new(_asNodeB ? RemotePaymentBasepointSecret : LocalPaymentBasepointSecret);

    protected override Key GetHtlcBasepointSecret(uint channelKeyIndex) => GetPaymentBasepointSecret(channelKeyIndex);

    protected override Key GetDelayedPaymentBasepointSecret(uint channelKeyIndex) =>
        _asNodeB
            ? throw new InvalidOperationException("Appendix C gives no delayed basepoint secret for node B")
            : new Key(LocalDelayedPaymentBasepointSecret);

    protected override Key GetRevocationBasepointSecret(uint channelKeyIndex) =>
        _asNodeB
            ? new Key(RemoteRevocationBasepointSecret)
            : throw new InvalidOperationException("Appendix C gives no revocation basepoint secret for node A");

    private static byte[] Filled(byte value) => Enumerable.Repeat(value, 32).ToArray();
}