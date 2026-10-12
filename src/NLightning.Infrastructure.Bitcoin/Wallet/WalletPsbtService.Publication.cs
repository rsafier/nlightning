using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Wallet;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Signing.Recovery;

public sealed partial class WalletPsbtService
{
    private static readonly ChannelId s_publicationWorkflowKey = new(SHA256.HashData("NLightning/native-psbt-publication/v1"u8));
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly IRemoteSigningWorkflowCoordinator? _signingWorkflows;
    public bool HasNativePublicationRecovery => _signingWorkflows is INativeWalletPsbtSigningRecovery;

    public async Task<byte[]> SendOutputsCapturedAsync(IReadOnlyList<(BitcoinScript Script, LightningMoney Amount)> outputs,
        long feeRatePerKw, int minConfirmations, string label, CancellationToken cancellationToken)
    {
        if (!HasNativePublicationRecovery) throw new NotSupportedException("Native PSBT publication recovery is unavailable.");
        await _publicationGate.WaitAsync(cancellationToken);
        try
        {
            await RecoverPublicationsLockedAsync(cancellationToken);
            var lockId = RandomNumberGenerator.GetBytes(32);
            var funded = await FundPsbtAsync(new PsbtFundRequest(outputs, [], feeRatePerKw, minConfirmations,
                lockId, DefaultLeaseDuration), cancellationToken);
            ISigningWorkflowScope? workflow = null;
            var intentStarted = false;
            try
            {
                await _gate.WaitAsync(cancellationToken);
                NativeWalletPsbtPublicationIntent intent;
                try
                {
                    var parsed = LoadPsbt(funded.Psbt);
                    var tx = parsed.GetGlobalTransaction();
                    var reservations = await ReservationsForAsync(tx, cancellationToken);
                    intent = new NativeWalletPsbtPublicationIntent(funded.Psbt.ToArray(), tx.ToBytes(),
                        reservations.Select(r => r.Id).ToArray(), funded.Fee.Satoshi,
                        checked((uint)feeRatePerKw), _blockchainMonitor.LastProcessedBlockHeight, label);
                    var encoded = JsonSerializer.SerializeToUtf8Bytes(intent);
                    // A failed intent save can be ambiguous; retain reservations until durable state is inspected.
                    intentStarted = true;
                    workflow = await _signingWorkflows!.BeginAsync(Descriptor(encoded));
                }
                finally { _gate.Release(); }
                await RequireReservationsAsync(intent, cancellationToken);
                workflow.Activate();
                var finalized = await FinalizePublicationAsync(intent, workflow.WorkflowId, cancellationToken);
                var signed = new SignedTransaction(new TxId(LoadPsbt(intent.FundedPsbt).GetGlobalTransaction().GetHash().ToBytes()),
                    finalized.RawFinalTx);
                ValidatePublication(intent, signed);
                var row = PublicationRow(intent, signed);
                await SavePublicationRowAsync(row, workflow);
                if (!await _blockchainMonitor.PublishAsync(row))
                    throw new WalletPsbtException(WalletPsbtError.PublishRefused, "The saved transaction was not accepted; its publication remains pending.");
                return row.RawTransaction.ToArray();
            }
            catch
            {
                if (!intentStarted)
                    foreach (var lease in funded.Leases)
                        await ReleaseAsync(lockId, lease.TxId, lease.Index, CancellationToken.None);
                throw;
            }
            finally { workflow?.Dispose(); }
        }
        finally { _publicationGate.Release(); }
    }

    public async Task RecoverPublicationsAsync(CancellationToken cancellationToken)
    {
        if (!HasNativePublicationRecovery) return;
        await _publicationGate.WaitAsync(cancellationToken);
        try
        {
            await RecoverPublicationsLockedAsync(cancellationToken);
            await SweepAsync(cancellationToken);
        }
        finally { _publicationGate.Release(); }
    }

    private async Task RecoverPublicationsLockedAsync(CancellationToken cancellationToken)
    {
        var pending = await _signingWorkflows!.GetPendingAsync(s_publicationWorkflowKey);
        if (pending.Count == 0) return;
        if (pending.Count != 1 || pending[0].State != SigningWorkflowState.Pending
            || pending[0].Kind != SigningWorkflowKind.WalletPsbtPublication || pending[0].PublicationIntent is null)
            throw new InvalidOperationException("PSBT publication recovery requires attention.");
        var saved = pending[0];
        var intent = DecodePublicationIntent(saved.PublicationIntent!);
        await RequireReservationsAsync(intent, cancellationToken);
        using var workflow = await _signingWorkflows.BeginAsync(Descriptor(saved.PublicationIntent!));
        workflow.Activate();
        var signed = ((INativeWalletPsbtSigningRecovery)_signingWorkflows).ReplayPsbtPublication(workflow);
        if (signed is null)
        {
            var finalized = await FinalizePublicationAsync(intent, workflow.WorkflowId, cancellationToken);
            signed = new SignedTransaction(new TxId(LoadPsbt(intent.FundedPsbt).GetGlobalTransaction().GetHash().ToBytes()),
                finalized.RawFinalTx);
        }
        ValidatePublication(intent, signed);
        var row = PublicationRow(intent, signed);
        await SavePublicationRowAsync(row, workflow);
        await _blockchainMonitor.PublishAsync(row);
    }

    private async Task<PsbtFinalizeResult> FinalizePublicationAsync(NativeWalletPsbtPublicationIntent intent, Guid workflowId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RequireReservationsAsync(intent, ct);
            return await FinalizePsbtCoreAsync(intent.FundedPsbt, ct, intent, workflowId);
        }
        finally { _gate.Release(); }
    }

    private async Task RequirePublicationAdmissionAsync(Transaction tx, FeeInputReservation reservation,
        NativeWalletPsbtPublicationIntent? publication, Guid? owningWorkflowId, bool allowStoredPublication, CancellationToken ct)
    {
        if (publication is not null)
        {
            var encoded = JsonSerializer.SerializeToUtf8Bytes(publication);
            var pending = await _signingWorkflows!.GetPendingAsync(s_publicationWorkflowKey);
            if (!publication.ReservationIds.Contains(reservation.Id)
                || !tx.ToBytes().AsSpan().SequenceEqual(publication.UnsignedTransaction)
                || pending.Count != 1 || pending[0].WorkflowId != owningWorkflowId || pending[0].State != SigningWorkflowState.Pending
                || pending[0].Kind != SigningWorkflowKind.WalletPsbtPublication
                || !(pending[0].PublicationIntent ?? []).AsSpan().SequenceEqual(encoded))
                throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "signing is not owned by this publication intent");
            return;
        }
        var unconsumed = await _signingWorkflows!.GetPendingAsync(s_publicationWorkflowKey);
        if (unconsumed.Any(saved => saved.PublicationIntent is null
            || DecodePublicationIntent(saved.PublicationIntent).ReservationIds.Contains(reservation.Id)))
            throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "input has an unconsumed publication intent");
        if (!await IsPublicationHeldAsync(reservation, ct)) return;
        if (allowStoredPublication)
        {
            using var scope = _scopeFactory.CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var saved = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(new TxId(tx.GetHash().ToBytes()));
            if (saved is not null && saved.RawTransaction.AsSpan().SequenceEqual(tx.ToBytes())) return;
        }
        throw new WalletPsbtException(WalletPsbtError.FailedPrecondition, "input is held by another durable publication decision");
    }

    private static NativeWalletPsbtPublicationIntent DecodePublicationIntent(byte[] encoded)
    {
        var intent = JsonSerializer.Deserialize<NativeWalletPsbtPublicationIntent>(encoded)
            ?? throw new InvalidOperationException("PSBT publication intent is invalid.");
        ValidateIntent(intent);
        return intent;
    }

    private static void ValidateIntent(NativeWalletPsbtPublicationIntent intent)
    {
        if (intent.FundedPsbt is not { Length: > 0 } || intent.UnsignedTransaction is not { Length: > 0 }
            || intent.ReservationIds is not { Count: > 0 } || intent.ReservationIds.Any(id => id == Guid.Empty)
            || intent.ReservationIds.Distinct().Count() != intent.ReservationIds.Count || intent.FeeSat <= 0
            || intent.FeeRatePerKw < WalletSpendService.MinFeeRatePerKw || intent.FeeRatePerKw > WalletSpendService.MaxFeeRatePerKw
            || intent.Height == 0 || intent.Label?.Length > 500)
            throw new InvalidOperationException("PSBT publication intent is invalid.");
    }

    private static SigningWorkflowDescriptor Descriptor(byte[] encoded) =>
        new(s_publicationWorkflowKey, SigningWorkflowKind.WalletPsbtPublication, 0, 0, SHA256.HashData(encoded))
        { PublicationIntent = encoded };

    private async Task<IReadOnlyList<FeeInputReservation>> ReservationsForAsync(Transaction tx, CancellationToken ct)
    {
        var all = await _feeInputSelector.GetAllAsync(ct);
        var outpoints = tx.Inputs.Select(i => (new TxId(i.PrevOut.Hash.ToBytes()), i.PrevOut.N)).ToHashSet();
        var held = all.Where(r => r.Inputs.Any(i => outpoints.Contains((i.TxId, i.Index)))).ToArray();
        if (outpoints.Count != tx.Inputs.Count || held.Length == 0
            || held.Any(r => !TryParseLease(r.Purpose, out _, out _))
            || held.SelectMany(r => r.Inputs).Count() != outpoints.Count
            || !held.SelectMany(r => r.Inputs).Select(i => (i.TxId, i.Index)).ToHashSet().SetEquals(outpoints))
            throw new InvalidOperationException("PSBT publication inputs do not match their retained leases.");
        return held;
    }

    private async Task RequireReservationsAsync(NativeWalletPsbtPublicationIntent intent, CancellationToken ct)
    {
        var psbt = LoadPsbt(intent.FundedPsbt);
        var held = await ReservationsForAsync(psbt.GetGlobalTransaction(), ct);
        if (intent.ReservationIds.Count != held.Count
            || !intent.ReservationIds.ToHashSet().SetEquals(held.Select(r => r.Id)))
            throw new InvalidOperationException("PSBT publication lost its original reservations.");
        foreach (var input in psbt.Inputs)
        {
            var coin = held.SelectMany(r => r.Inputs).Single(i => i.TxId == new TxId(input.PrevOut.Hash.ToBytes()) && i.Index == input.PrevOut.N);
            if (input.WitnessUtxo is null || input.WitnessUtxo.Value.Satoshi != coin.Amount.Satoshi
                || !input.WitnessUtxo.ScriptPubKey.ToBytes().AsSpan().SequenceEqual((byte[])coin.ScriptPubKey))
                throw new InvalidOperationException("PSBT publication lease amount or script changed.");
        }
    }

    private void ValidatePublication(NativeWalletPsbtPublicationIntent intent, SignedTransaction signed)
    {
        ValidateIntent(intent);
        var psbt = LoadPsbt(intent.FundedPsbt);
        var unsigned = psbt.GetGlobalTransaction();
        if (!unsigned.ToBytes().AsSpan().SequenceEqual(intent.UnsignedTransaction))
            throw new InvalidOperationException("PSBT publication unsigned transaction changed.");
        var final = Transaction.Load(signed.RawTxBytes, _network);
        var skeleton = final.Clone();
        foreach (var input in skeleton.Inputs) { input.WitScript = WitScript.Empty; input.ScriptSig = Script.Empty; }
        if (!skeleton.ToBytes().AsSpan().SequenceEqual(intent.UnsignedTransaction)
            || new TxId(final.GetHash().ToBytes()) != signed.TxId)
            throw new InvalidOperationException("PSBT publication receipt changed its unsigned transaction.");
        var spent = psbt.Inputs.Select(i => i.WitnessUtxo
            ?? throw new InvalidOperationException("PSBT publication lost its witness UTXO.")).ToArray();
        if (spent.Sum(o => o.Value.Satoshi) - final.Outputs.Sum(o => o.Value.Satoshi) != intent.FeeSat)
            throw new InvalidOperationException("PSBT publication receipt changed its fee.");
        var validator = final.CreateValidator(spent);
        for (var i = 0; i < final.Inputs.Count; i++)
            if (validator.ValidateInput(i).Error is not (null or ScriptError.OK))
                throw new InvalidOperationException("PSBT publication receipt has an invalid input witness.");
    }

    private static BroadcastTransactionModel PublicationRow(NativeWalletPsbtPublicationIntent intent, SignedTransaction signed) =>
        new(signed, BroadcastPurpose.WalletSend, null, intent.Height, intent.FeeRatePerKw,
            fee: LightningMoney.Satoshis(intent.FeeSat))
        { Label = intent.Label };

    private async Task<BroadcastTransactionModel> SavePublicationRowAsync(BroadcastTransactionModel row, ISigningWorkflowScope? workflow)
    {
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var existing = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(row.TransactionId);
        if (existing is null) uow.BroadcastTransactionDbRepository.Add(row);
        else if (!existing.RawTransaction.AsSpan().SequenceEqual(row.RawTransaction)
            || existing.Purpose != row.Purpose || existing.ChannelId != row.ChannelId
            || existing.ReplacesTransactionId != row.ReplacesTransactionId
            || workflow is not null && (existing.Label != row.Label || existing.Tags != row.Tags
                || existing.FirstBroadcastHeight != row.FirstBroadcastHeight || existing.FeeratePerKw != row.FeeratePerKw
                || existing.Fee?.MilliSatoshi != row.Fee?.MilliSatoshi))
            throw new InvalidOperationException("PSBT publication conflicts with its saved broadcast.");
        if (workflow is not null) await workflow.StageConsumeAsync(uow);
        await uow.SaveChangesAsync();
        return existing ?? row;
    }

    private async Task<bool> IsPublicationHeldAsync(FeeInputReservation reservation, CancellationToken ct)
    {
        if (!HasNativePublicationRecovery) return false;
        ct.ThrowIfCancellationRequested();
        var pending = await _signingWorkflows!.GetPendingAsync(s_publicationWorkflowKey);
        foreach (var workflow in pending)
        {
            if (workflow.Kind != SigningWorkflowKind.WalletPsbtPublication || workflow.PublicationIntent is null)
                throw new InvalidOperationException("PSBT publication recovery history is invalid.");
            if (DecodePublicationIntent(workflow.PublicationIntent).ReservationIds.Contains(reservation.Id)) return true;
        }
        using var scope = _scopeFactory.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var pendingInputs = await PendingBroadcastOutpoints.GetAsync(uow, _network, _logger);
        return reservation.Inputs.Any(i => pendingInputs.Contains((i.TxId, i.Index)));
    }
}