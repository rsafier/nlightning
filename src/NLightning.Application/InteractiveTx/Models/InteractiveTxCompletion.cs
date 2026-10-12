namespace NLightning.Application.InteractiveTx.Models;

using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Models;

/// <summary>
/// A negotiation whose <c>tx_signatures</c> were exchanged both ways: the transaction is fully signed (BOLT 2: the
/// receiver of the last <c>tx_signatures</c> SHOULD apply the witnesses and broadcast it). Handed to
/// <see cref="Interfaces.IInteractiveTxHost.OnCompletedAsync"/>.
/// </summary>
/// <param name="Session">The stored negotiation, in <see cref="Domain.Protocol.InteractiveTx.Enums.InteractiveTxSessionState.Signed"/>.</param>
/// <param name="Transaction">The constructed transaction.</param>
/// <param name="SignedTransaction">The transaction with every witness (<see cref="Domain.Protocol.InteractiveTx.Interfaces.IInteractiveTxBuilder.Finalize"/>).</param>
/// <param name="FeeratePerKw">The feerate the attempt was negotiated at.</param>
public sealed record InteractiveTxCompletion(
    InteractiveTxSessionModel Session,
    ConstructedInteractiveTx Transaction,
    SignedTransaction SignedTransaction,
    uint FeeratePerKw);