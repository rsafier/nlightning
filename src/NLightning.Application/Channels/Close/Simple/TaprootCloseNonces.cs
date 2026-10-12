using NBitcoin;

namespace NLightning.Application.Channels.Close.Simple;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Models;
using Domain.Crypto.Constants;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;

/// <summary>
/// The closee nonces of a simple taproot channel's cooperative close (bolt-simple-taproot.md §RBF Cooperative Close,
/// "JIT Nonce Pattern"): every <c>shutdown</c> of ours carries a fresh closee nonce (<c>shutdown_nonce</c>), whose
/// secret half lives in the signer's memory until a <c>closing_sig</c> consumes it. Nothing is persisted, so a
/// reconnection forgets the old secrets and our re-sent <c>shutdown</c> carries a new nonce.
/// </summary>
public static class TaprootCloseNonces
{
    /// <summary>True for a simple taproot channel (its close is MuSig2 over <c>option_simple_close</c> only).</summary>
    public static bool IsTaproot(ChannelModel channel) => channel.ChannelParams.OptionSimpleTaproot;

    /// <summary>
    /// Our <c>shutdown</c> to <paramref name="script"/>: on a simple taproot channel the signer's earlier closee
    /// nonces of the channel are forgotten and a fresh one goes out as <c>shutdown_nonce</c> (and becomes
    /// <paramref name="entry"/>'s <see cref="ClosingNegotiationRegistry.Entry.LocalCloseeNonce"/>); other channels get
    /// the plain message.
    /// </summary>
    public static ShutdownMessage CreateShutdown(ChannelModel channel, BitcoinScript script,
                                                 IMessageFactory messageFactory, ILightningSigner signer,
                                                 ClosingNegotiationRegistry.Entry? entry)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (!IsTaproot(channel))
            return messageFactory.CreateShutdownMessage(channel.ChannelId, script);

        signer.ForgetClosingNonces(channel.ChannelId);
        var nonce = signer.CreateClosingNonce(channel.ChannelId);
        entry?.LocalCloseeNonce = nonce;
        return messageFactory.CreateShutdownMessage(channel.ChannelId, script, nonce);
    }

    /// <summary>
    /// A valid MuSig2 public nonce (BIP 327: two compressed points, neither the point at infinity), as the spec asks
    /// of a received <c>shutdown_nonce</c> and <c>next_closee_nonce</c>.
    /// </summary>
    public static bool IsValidPublicNonce(MusigPublicNonce nonce)
    {
        var bytes = (byte[])nonce;
        if (bytes is not { Length: MusigConstants.PublicNonceLen })
            return false;

        const int half = MusigConstants.PublicNonceLen / 2;
        return PubKey.TryCreatePubKey(bytes.AsSpan(0, half).ToArray(), out var r1) && r1.IsCompressed
            && PubKey.TryCreatePubKey(bytes.AsSpan(half).ToArray(), out var r2) && r2.IsCompressed;
    }
}