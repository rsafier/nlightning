using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Node;

using Domain.Protocol.ValueObjects;
using Domain.Signing;
using Persistence.Contexts;
using Persistence.Entities.Node;

/// <summary>Immutable database enrollment is checked before runtime services can create key-bearing state.</summary>
public sealed class NodeSigningEnrollmentStore(NLightningDbContext database)
{
    public async Task ValidateAsync(NodeSigningContext context, CancellationToken cancellationToken = default)
    {
        context.Validate();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var enrollments = await database.Set<NodeSigningEnrollmentEntity>().AsNoTracking().ToListAsync(cancellationToken);
        var network = BitcoinNetwork.Resolve(context.Network).Name;
        if (enrollments.Count != 0)
        {
            var enrolled = enrollments.Count == 1 ? enrollments[0] : null;
            if (enrolled is null || enrolled.Id != 1 || enrolled.SchemaVersion != 1
             || !string.Equals(enrolled.NodeId, context.NodeId, StringComparison.Ordinal)
             || !string.Equals(enrolled.OwnerId, context.OwnerId, StringComparison.Ordinal)
             || !string.Equals(enrolled.SignerId, context.SignerId, StringComparison.Ordinal)
             || !string.Equals(enrolled.Network, network, StringComparison.Ordinal)
             || !enrolled.NodePublicKey.AsSpan().SequenceEqual((byte[])context.NodePublicKey))
                throw new InvalidOperationException("The node database is enrolled to another signing context or identity.");
        }
        else
        {
            if (await HasAuthorityStateAsync(database, cancellationToken))
                throw new InvalidOperationException("An existing node database without signing enrollment cannot be adopted by a new context.");
            database.Set<NodeSigningEnrollmentEntity>().Add(new NodeSigningEnrollmentEntity
            {
                Id = 1,
                SchemaVersion = 1,
                NodeId = context.NodeId,
                OwnerId = context.OwnerId,
                SignerId = context.SignerId,
                Network = network,
                NodePublicKey = ((byte[])context.NodePublicKey).ToArray(),
                CreatedAtTicks = DateTime.UtcNow.Ticks
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> HasAuthorityStateAsync(NLightningDbContext database, CancellationToken cancellationToken)
        => await database.Channels.AnyAsync(cancellationToken)
        || await database.ChannelConfigs.AnyAsync(cancellationToken)
        || await database.ChannelKeySets.AnyAsync(cancellationToken)
        || await database.Htlcs.AnyAsync(cancellationToken)
        || await database.ChannelLocalAliases.AnyAsync(cancellationToken)
        || await database.RemoteShachains.AnyAsync(cancellationToken)
        || await database.Commitments.AnyAsync(cancellationToken)
        || await database.FeeUpdates.AnyAsync(cancellationToken)
        || await database.RevokedCommitments.AnyAsync(cancellationToken)
        || await database.InteractiveTxSessions.AnyAsync(cancellationToken)
        || await database.ChannelFundings.AnyAsync(cancellationToken)
        || await database.ChannelPolicies.AnyAsync(cancellationToken)
        || await database.WalletAddresses.AnyAsync(cancellationToken)
        || await database.WalletAccounts.AnyAsync(cancellationToken)
        || await database.Utxos.AnyAsync(cancellationToken)
        || await database.KeyRingKeys.AnyAsync(cancellationToken)
        || await database.SilentPaymentLabels.AnyAsync(cancellationToken)
        || await database.SilentPaymentScanState.AnyAsync(cancellationToken)
        || await database.SigningWorkflows.AnyAsync(cancellationToken)
        || await database.SigningRequests.AnyAsync(cancellationToken)
        || await database.VlsChannelMappings.AnyAsync(cancellationToken)
        || await database.Invoices.AnyAsync(cancellationToken)
        || await database.Offers.AnyAsync(cancellationToken)
        || await database.Payments.AnyAsync(cancellationToken)
        || await database.CashuQuotes.AnyAsync(cancellationToken)
        || await database.PeerStorageBlobs.AnyAsync(cancellationToken)
        || await database.AccountingEvents.AnyAsync(cancellationToken)
        || await database.AccountingEntries.AnyAsync(cancellationToken)
        || await database.AccountingPostings.AnyAsync(cancellationToken)
        || await database.AccountingBalances.AnyAsync(cancellationToken)
        || await database.AccountingCursor.AnyAsync(cancellationToken)
        || await database.AccountingLots.AnyAsync(cancellationToken)
        || await database.AccountingPeriods.AnyAsync(cancellationToken)
        || await database.AccountingPrices.AnyAsync(cancellationToken)
        || await database.AccountingPriceReplacementAudits.AnyAsync(cancellationToken)
        || await database.AccountingRules.AnyAsync(cancellationToken)
        || await database.AccountingOverrides.AnyAsync(cancellationToken)
        || await database.AccountingLotReliefs.AnyAsync(cancellationToken)
        || await database.WatchedTransactions.AnyAsync(cancellationToken)
        || await database.WatchedOutpoints.AnyAsync(cancellationToken)
        || await database.BroadcastTransactions.AnyAsync(cancellationToken)
        || await database.ChannelCloses.AnyAsync(cancellationToken)
        || await database.OutputResolutions.AnyAsync(cancellationToken)
        || await database.OnchainHtlcObservations.AnyAsync(cancellationToken)
        || await database.FeeInputReservations.AnyAsync(cancellationToken)
        || await database.FeeInputReservationInputs.AnyAsync(cancellationToken)
        || await database.ImportedTapscripts.AnyAsync(cancellationToken)
        || await database.ImportedWatchIndexes.AnyAsync(cancellationToken)
        || await database.WalletTransactions.AnyAsync(cancellationToken)
        || await database.WalletHistoryRescanStates.AnyAsync(cancellationToken)
        || await database.WalletTransactionLabels.AnyAsync(cancellationToken)
        || await database.SilentPaymentOutputs.AnyAsync(cancellationToken)
        || await database.CashuDeposits.AnyAsync(cancellationToken)
        || await database.LiquidityPurchases.AnyAsync(cancellationToken)
        || await database.ForwardCircuits.AnyAsync(cancellationToken)
        || await database.PaymentParts.AnyAsync(cancellationToken)
        || await database.PaymentHops.AnyAsync(cancellationToken)
        || await database.PaymentPartHops.AnyAsync(cancellationToken)
        || await database.OnionReplayEntries.AnyAsync(cancellationToken)
        || await database.TrampolineRelays.AnyAsync(cancellationToken)
        || await database.TrampolineRelayParts.AnyAsync(cancellationToken)
        || await database.TrampolineRelayAttempts.AnyAsync(cancellationToken)
        || await database.PaymentTrampolineHops.AnyAsync(cancellationToken)
        || await database.Peers.AnyAsync(cancellationToken)
        || await database.PeerStorageRetrievals.AnyAsync(cancellationToken);
}