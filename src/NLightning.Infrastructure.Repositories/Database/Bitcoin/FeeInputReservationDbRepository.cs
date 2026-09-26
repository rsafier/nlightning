using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Bitcoin;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Constants;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Persistence.Contexts;
using Persistence.Entities.Bitcoin;

/// <summary>
/// The persisted fee input reservations (BOLT 5 plan O7-T1, migration <c>AddFeeInputReservations</c>). Writes are
/// staged and committed by <c>IUnitOfWork.SaveChangesAsync</c>.
/// </summary>
public class FeeInputReservationDbRepository(NLightningDbContext context)
    : BaseDbRepository<FeeInputReservationEntity>(context), IFeeInputReservationDbRepository
{
    private readonly DbSet<FeeInputReservationInputEntity> _inputs = context.FeeInputReservationInputs;

    public void Add(FeeInputReservation reservation, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        Insert(new FeeInputReservationEntity
        {
            Id = reservation.Id,
            Purpose = reservation.Purpose,
            FeeSats = reservation.Fee.Satoshi,
            ChangeAmountSats = reservation.ChangeAmount.Satoshi,
            ChangeScript = reservation.ChangeScript is { } changeScript ? (byte[])changeScript : null,
            CreatedAt = createdAt
        });

        for (var i = 0; i < reservation.Inputs.Count; i++)
        {
            var input = reservation.Inputs[i];
            _inputs.Add(new FeeInputReservationInputEntity
            {
                TransactionId = input.TxId,
                Index = input.Index,
                ReservationId = reservation.Id,
                AmountSats = input.Amount.Satoshi,
                AddressType = input.AddressType,
                ScriptPubKey = input.ScriptPubKey,
                Position = i
            });
        }
    }

    public async Task<bool> DeleteAsync(Guid reservationId)
    {
        var entity = await DbSet.Include(e => e.Inputs).FirstOrDefaultAsync(e => e.Id == reservationId);
        if (entity is null)
            return false;

        // Delete the inputs explicitly too: the cascade is not relied on for tracked rows
        if (entity.Inputs is not null)
            _inputs.RemoveRange(entity.Inputs);
        DbSet.Remove(entity);
        return true;
    }

    public async Task<FeeInputReservation?> GetByIdAsync(Guid reservationId)
    {
        var entity = await DbSet.AsNoTracking().Include(e => e.Inputs).FirstOrDefaultAsync(e => e.Id == reservationId);
        return entity is null ? null : MapEntityToModel(entity);
    }

    public async Task<IReadOnlyList<FeeInputReservation>> GetAllAsync()
    {
        var entities = await DbSet.AsNoTracking().Include(e => e.Inputs).ToListAsync();
        return entities.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id).Select(MapEntityToModel).ToList();
    }

    public async Task<IReadOnlyList<(TxId TxId, uint Index, Guid ReservationId)>> GetReservedOutpointsAsync()
    {
        var inputs = await _inputs.AsNoTracking().ToListAsync();
        return inputs.Select(i => (i.TransactionId, i.Index, i.ReservationId)).ToList();
    }

    private static FeeInputReservation MapEntityToModel(FeeInputReservationEntity entity)
    {
        var inputs = (entity.Inputs ?? [])
                    .OrderBy(i => i.Position)
                    .Select(i => new WalletInput(i.TransactionId, i.Index, LightningMoney.Satoshis(i.AmountSats),
                                                 i.AddressType, i.ScriptPubKey,
                                                 WalletWeights.GetInputWeight(i.AddressType)))
                    .ToList();

        // Not a conditional expression: null would convert to BitcoinScript through its implicit byte[] operator
        BitcoinScript? changeScript = null;
        if (entity.ChangeScript is not null)
            changeScript = new BitcoinScript(entity.ChangeScript);

        return new FeeInputReservation(entity.Id, entity.Purpose, inputs, LightningMoney.Satoshis(entity.FeeSats),
                                       LightningMoney.Satoshis(entity.ChangeAmountSats), changeScript);
    }
}