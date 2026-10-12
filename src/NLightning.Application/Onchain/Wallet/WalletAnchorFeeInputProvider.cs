namespace NLightning.Application.Onchain.Wallet;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Models;
using Resolvers.Local;

/// <summary>
/// Backs the anchors HTLC transactions' <see cref="IAnchorFeeInputProvider"/> (BOLT 5 plan O7-T3) with the wallet's
/// persisted <see cref="IFeeInputSelector"/> and <see cref="ILightningSigner.SignWalletTransaction(SignedTransaction, Guid, IReadOnlyList{SpentOutput})"/>
/// (O7-T1). An owner's reservation carries the purpose <c>anchor-htlc:&lt;commitment txid&gt;:&lt;vout&gt;</c>, so it is
/// found (and replaced or released) again after a restart.
/// </summary>
public sealed class WalletAnchorFeeInputProvider : IAnchorFeeInputProvider
{
    private const string PurposePrefix = "anchor-htlc:";

    private readonly IFeeInputSelector _feeInputSelector;
    private readonly ILightningSigner _lightningSigner;
    private readonly ISweepDestinationProvider _sweepDestinationProvider;

    public WalletAnchorFeeInputProvider(IFeeInputSelector feeInputSelector, ILightningSigner lightningSigner,
                                        ISweepDestinationProvider sweepDestinationProvider)
    {
        _feeInputSelector = feeInputSelector;
        _lightningSigner = lightningSigner;
        _sweepDestinationProvider = sweepDestinationProvider;
    }

    /// <summary>The reservation purpose of an HTLC output's fee inputs.</summary>
    public static string GetPurpose(AnchorFeeInputOwner owner) =>
        $"{PurposePrefix}{owner.CommitmentTxId}:{owner.OutputIndex}";

    /// <inheritdoc />
    public async Task<AnchorFeeInputSelection?> SelectAsync(AnchorFeeInputOwner owner, long baseWeight,
                                                            uint feeratePerKw, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseWeight);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseWeight, int.MaxValue);

        // The owner's next selection replaces its earlier reservation (a failed round, an RBF replacement)
        await ReleaseAsync(owner, cancellationToken);

        FeeInputReservation reservation;
        try
        {
            reservation = await _feeInputSelector.ReserveAsync(LightningMoney.Zero,
                                                               LightningMoney.Satoshis(Math.Max(1u, feeratePerKw)),
                                                               (int)baseWeight, GetPurpose(owner), cancellationToken);
        }
        catch (InsufficientFundsException)
        {
            return null;
        }

        try
        {
            var changeScript = reservation.ChangeScript is { } script
                                   ? (byte[])script
                                   : await _sweepDestinationProvider.GetDestinationScriptAsync(cancellationToken);
            var inputs = reservation.Inputs
                                    .Select(i => new AnchorFeeInput(i.TxId, i.Index, (ulong)i.Amount.Satoshi,
                                                                    (byte[])i.ScriptPubKey, i.InputWeight))
                                    .ToList();
            return new AnchorFeeInputSelection(inputs, changeScript);
        }
        catch
        {
            await _feeInputSelector.ReleaseAsync(reservation.Id, CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SignedTransaction> SignAsync(SignedTransaction transaction,
                                                   IReadOnlyList<AnchorFeeInput> feeInputs, SpentOutput htlcInput,
                                                   CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(feeInputs);
        if (feeInputs.Count == 0)
            throw new InvalidOperationException("An anchors HTLC transaction needs at least one fee input");

        // The one reservation that holds every fee input: the signer then signs that reservation's inputs only
        var all = await _feeInputSelector.GetAllAsync(cancellationToken);
        var reservation = all.FirstOrDefault(r => r.Purpose.StartsWith(PurposePrefix, StringComparison.Ordinal)
                                               && feeInputs.All(f => r.Inputs.Any(i => i.TxId == f.TxId
                                                                                   && i.Index == f.Vout)))
                       ?? throw new InvalidOperationException("The fee inputs are not one reservation of the wallet");

        var signed = new SignedTransaction(transaction.TxId, (byte[])transaction.RawTxBytes.Clone());
        try
        {
            if (!_lightningSigner.SignWalletTransaction(signed, reservation.Id, [htlcInput]))
                throw new InvalidOperationException("The transaction has no wallet input to sign");
        }
        catch (SignerException e)
        {
            throw new InvalidOperationException($"The wallet cannot sign the fee inputs: {e.Message}", e);
        }

        return signed;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(AnchorFeeInputOwner owner, CancellationToken cancellationToken)
    {
        var purpose = GetPurpose(owner);
        var all = await _feeInputSelector.GetAllAsync(cancellationToken);
        foreach (var reservation in all.Where(r => r.Purpose == purpose))
            await _feeInputSelector.ReleaseAsync(reservation.Id, cancellationToken);
    }
}