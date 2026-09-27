namespace NLightning.Infrastructure.Bitcoin.Onion.OnionMessages;

using Domain.Crypto.ValueObjects;
using Domain.Protocol.Messages;

/// <summary>
/// The receive-side crypto of onion messages (BOLT 4 "Onion Messages", reader): peel the Sphinx layer with the
/// message's <c>path_key</c> and empty associated data, then decrypt this hop's <c>encrypted_recipient_data</c> and
/// decide whether to forward, deliver or ignore.
/// </summary>
/// <remarks>
/// <para>Applies the reader's structural rules: an undecryptable packet or data, an invalid <c>onionmsg_tlv</c> or
/// one with an unknown even type, any <c>allowed_features</c> bit (no feature is defined for onion messages), a
/// non-final hop with anything but <c>encrypted_recipient_data</c>, a <c>path_id</c> or no next hop, a final hop
/// without <c>encrypted_recipient_data</c> or with more than one payload field (types 64 and up): all
/// <see cref="OnionMessageUnwrapStatus.Ignored"/>. Reply matching (<c>path_id</c> against our reply paths), rate
/// limits and peer lookup are the caller's.</para>
/// <para>Never throws for a bad message; only local faults throw: a missing key manager
/// (<see cref="UnwrapAsLocalNode"/>) or an invalid node key (<see cref="Unwrap"/>). Stateless and thread-safe.</para>
/// </remarks>
public interface IOnionMessageUnwrapper
{
    /// <summary>
    /// Unwraps a message with the node key held by the key manager.
    /// </summary>
    /// <exception cref="InvalidOperationException">No key manager is available.</exception>
    OnionMessageUnwrapResult UnwrapAsLocalNode(OnionMessageMessage message);

    /// <summary>
    /// Unwraps a message with the given node key (tests and tools).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="nodeKey"/> is not a valid private key.</exception>
    OnionMessageUnwrapResult Unwrap(OnionMessageMessage message, PrivKey nodeKey);
}