namespace NLightning.Application.Onchain.Wallet;

using Anchors;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Backs the anchor CPFP's <see cref="IAnchorFeeInputSource"/> (BOLT 5 plan O7-T2) with the wallet's persisted
/// <see cref="IFeeInputSelector"/> (O7-T1). A channel's reservations carry the purpose
/// <c>anchor-cpfp:&lt;channel id&gt;</c>, so they are found again after a restart.
/// </summary>
public sealed class WalletAnchorFeeInputSource : IAnchorFeeInputSource
{
    private const string PurposePrefix = "anchor-cpfp:";

    private readonly IFeeInputSelector _feeInputSelector;

    public WalletAnchorFeeInputSource(IFeeInputSelector feeInputSelector)
    {
        _feeInputSelector = feeInputSelector;
    }

    /// <summary>The reservation purpose of a channel's CPFP inputs.</summary>
    public static string GetPurpose(ChannelId channelId) => PurposePrefix + channelId;

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnchorWalletInput>?> ReserveAsync(ChannelId channelId, ulong amountSat,
                                                                     uint feeratePerKw,
                                                                     CancellationToken cancellationToken)
    {
        try
        {
            var reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Satoshis(amountSat),
                                                                   LightningMoney.Satoshis(Math.Max(1u, feeratePerKw)),
                                                                   0, GetPurpose(channelId), cancellationToken);
            return reservation.Inputs.Select(ToAnchorWalletInput).ToList();
        }
        catch (InsufficientFundsException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AnchorWalletInput>> GetReservedAsync(ChannelId channelId,
                                                                        CancellationToken cancellationToken)
    {
        var purpose = GetPurpose(channelId);
        var all = await _feeInputSelector.GetAllAsync(cancellationToken);
        return all.Where(r => r.Purpose == purpose)
                  .SelectMany(r => r.Inputs)
                  .Select(ToAnchorWalletInput)
                  .ToList();
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(ChannelId channelId, CancellationToken cancellationToken)
    {
        var purpose = GetPurpose(channelId);
        var all = await _feeInputSelector.GetAllAsync(cancellationToken);
        foreach (var reservation in all.Where(r => r.Purpose == purpose))
            await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
    }

    private static AnchorWalletInput ToAnchorWalletInput(WalletInput input) =>
        new(input.TxId, input.Index, (ulong)input.Amount.Satoshi, (byte[])input.ScriptPubKey, input.InputWeight);
}