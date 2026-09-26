namespace NLightning.Application.Onchain.Anchors;

using Domain.Channels.ValueObjects;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// The wallet side of an anchor CPFP (BOLT 5 plan O7-T2): the confirmed wallet outputs that pay a child's fee, reserved
/// for one channel so no other spend (a funding transaction, another child) takes them while the child may still
/// confirm.
/// </summary>
/// <remarks>
/// <para>This is the consumer port of <see cref="AnchorCpfpService"/>; the wallet's fee-input selector (O7-T1) backs
/// it through an adapter. Reservations are keyed by channel: every child of a channel (the first one and its RBF
/// replacements) draws from the same reservation, and <see cref="ReleaseAsync"/> gives all of it back once no child
/// can confirm any more (a child or the anchor's other spend confirmed, or the commitment can no longer confirm).</para>
/// <para>Reservations MUST be durable: persisted before <see cref="ReserveAsync"/> returns and restored at startup, so
/// <see cref="GetReservedAsync"/> returns them after a restart and no other spend (a funding transaction) takes the
/// inputs of a child that is still pending (the chain monitor rebroadcasts its row after a restart; a conflicting spend
/// would replace it and strip the commitment's fee bump). The service never re-reserves on its own. The O7-T1
/// <c>IFeeInputSelector</c> persists its reservations (<c>FeeInputReservations</c>) and restores them with the UTXO
/// set; an adapter keys them by channel through the reservation's purpose.</para>
/// <para>The inputs are signed by <c>ILightningSigner.SignWalletTransaction</c>, which must sign exactly the inputs
/// that spend wallet outputs and leave the anchor input alone. A P2TR input's BIP 341 signature commits to every spent
/// output, the anchor (330 sat, P2WSH) included, so a selector that returns P2TR outputs needs a wallet signer that
/// knows the anchor's prevout; P2WPKH inputs do not.</para>
/// </remarks>
public interface IAnchorFeeInputSource
{
    /// <summary>
    /// Reserves more confirmed wallet outputs for <paramref name="channelId"/>'s CPFP: together worth at least
    /// <paramref name="amountSat"/> plus their own input fee at <paramref name="feeratePerKw"/>, excluding what the
    /// channel already holds. Returns null (nothing reserved) when the wallet cannot cover it.
    /// </summary>
    /// <param name="channelId">The channel whose commitment the child pays for.</param>
    /// <param name="amountSat">The value still missing, before the new inputs' own weight.</param>
    /// <param name="feeratePerKw">The feerate the new inputs' weight is paid at.</param>
    /// <param name="cancellationToken">Cancels the selection.</param>
    /// <returns>The newly reserved inputs (not the ones held before), or null.</returns>
    Task<IReadOnlyList<AnchorWalletInput>?> ReserveAsync(ChannelId channelId, ulong amountSat, uint feeratePerKw,
                                                        CancellationToken cancellationToken);

    /// <summary>Every input currently reserved for <paramref name="channelId"/> (empty when none).</summary>
    Task<IReadOnlyList<AnchorWalletInput>> GetReservedAsync(ChannelId channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Releases every reservation of <paramref name="channelId"/> (idempotent). An output a confirmed child spent is
    /// removed from the wallet by the chain monitor as a wallet spend; the others are free again.
    /// </summary>
    Task ReleaseAsync(ChannelId channelId, CancellationToken cancellationToken);
}