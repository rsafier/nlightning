namespace NLightning.Application.InteractiveTx.Interfaces;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.InteractiveTx.Models;
using Domain.Protocol.Interfaces;
using Models;

/// <summary>
/// One immutable negotiation state, mirroring <see cref="InteractiveTxSession"/> member for member (see there for the
/// rules each member enforces). Every step returns the next state; the driver keeps it.
/// </summary>
public interface IInteractiveTxNegotiation
{
    /// <inheritdoc cref="InteractiveTxSession.Parameters"/>
    InteractiveTxSessionParameters Parameters { get; }

    /// <inheritdoc cref="InteractiveTxSession.State"/>
    InteractiveTxSessionState State { get; }

    /// <inheritdoc cref="InteractiveTxSession.Inputs"/>
    IReadOnlyList<InteractiveTxInput> Inputs { get; }

    /// <inheritdoc cref="InteractiveTxSession.Outputs"/>
    IReadOnlyList<InteractiveTxOutput> Outputs { get; }

    /// <inheritdoc cref="InteractiveTxSession.ConstructedTx"/>
    ConstructedInteractiveTx? ConstructedTx { get; }

    /// <inheritdoc cref="InteractiveTxSession.RemoteWitnesses"/>
    IReadOnlyList<Witness>? RemoteWitnesses { get; }

    /// <inheritdoc cref="InteractiveTxSession.RemoteSharedInputSignature"/>
    CompactSignature? RemoteSharedInputSignature { get; }

    /// <inheritdoc cref="InteractiveTxSession.RemoteSharedInputPartialSignature"/>
    MusigPartialSignatureWithNonce? RemoteSharedInputPartialSignature => null;

    /// <inheritdoc cref="InteractiveTxSession.Start"/>
    InteractiveTxNegotiationStep Start();

    /// <inheritdoc cref="InteractiveTxSession.Receive"/>
    InteractiveTxNegotiationStep Receive(IChannelMessage message, IPrevTxInspector prevTxInspector);

    /// <inheritdoc cref="InteractiveTxSession.WithConstructedTransaction"/>
    IInteractiveTxNegotiation WithConstructedTransaction(ConstructedInteractiveTx transaction);

    /// <inheritdoc cref="InteractiveTxSession.OnCommitmentSignedReceived"/>
    IInteractiveTxNegotiation OnCommitmentSignedReceived();

    /// <inheritdoc cref="InteractiveTxSession.SendsTxSignaturesFirst"/>
    bool SendsTxSignaturesFirst();

    /// <inheritdoc cref="InteractiveTxSession.SendTxSignatures"/>
    InteractiveTxNegotiationStep SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                  CompactSignature? sharedInputSignature);

    /// <inheritdoc cref="InteractiveTxSession.SendTxSignatures(IReadOnlyList{Witness}, CompactSignature?, MusigPartialSignatureWithNonce?)"/>
    InteractiveTxNegotiationStep SendTxSignatures(IReadOnlyList<Witness> localWitnesses,
                                                  CompactSignature? sharedInputSignature,
                                                  MusigPartialSignatureWithNonce? sharedInputPartialSignature) =>
        sharedInputPartialSignature is null
            ? SendTxSignatures(localWitnesses, sharedInputSignature)
            : throw new NotSupportedException("This engine does not sign taproot shared inputs");

    /// <inheritdoc cref="InteractiveTxSession.Abort"/>
    InteractiveTxNegotiationStep Abort(string reason);
}