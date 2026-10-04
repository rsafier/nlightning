using System.Collections.Concurrent;

namespace NLightning.Application.Onchain.Resolvers;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// The outputs of a simple taproot commitment the on-chain resolution does not spend yet (NL-877 T4 safety floor,
/// NL-966): HTLC outputs (claims, HTLC transactions with their wallet fee inputs, second-level outputs), the key-path
/// penalties of revoked HTLC outputs, and our anchor. Their rows stay recorded (the watcher wrote them) and unresolved,
/// so the channel stays <c>OnchainResolving</c> while funds may be at stake, and each is reported once per process at
/// critical level through an <see cref="AlertAction"/>; nothing is built, so nothing throws every block and the other
/// outputs and channels resolve as usual.
/// </summary>
public sealed class UnsupportedTaprootOutputs
{
    private readonly ConcurrentDictionary<(ChannelId, TxId, uint), byte> _reported = new();

    /// <summary>
    /// Adds the alert of output <paramref name="vout"/> of <paramref name="txId"/> once per process (marked when the
    /// executor logged it, after the round's save).
    /// </summary>
    public void Report(ChannelId channelId, TxId txId, uint vout, OutputDescriptorKind kind, ulong amountSat,
                       List<OutputResolverAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var key = (channelId, txId, vout);
        if (_reported.ContainsKey(key))
            return;

        actions.Add(new AlertAction("NL-966",
                                    $"Output {vout} ({kind}, {amountSat} sat) of {Display(txId)} on simple taproot "
                                  + $"channel {channelId} is not resolved: spending this simple taproot output is not "
                                  + "supported yet; it stays recorded and watched, and funds may be lost if it is not "
                                  + "spent by hand before the peer's path opens",
                                    () => _reported.TryAdd(key, 0)));
    }

    private static string Display(TxId txId) => new NBitcoin.uint256((byte[])txId).ToString();
}