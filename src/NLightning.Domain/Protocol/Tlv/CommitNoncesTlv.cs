namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.Constants;
using Crypto.ValueObjects;

/// <summary>
/// Commit Nonces TLV.
/// </summary>
/// <remarks>
/// BOLTs PR #1324 <c>commit_nonces</c>, type 4 of <c>tx_complete</c>: [<c>66*byte</c>:<c>commit_nonce</c>]
/// [<c>66*byte</c>:<c>next_commit_nonce</c>], the sender's verification nonces for the commitment of the transaction
/// negotiated so far and for the one after it. Eclair 0.14.3 sends it in every tx_complete of a taproot session (a new
/// one, with new nonces, after each change of the transaction). Converted by <c>CommitNoncesTlvConverter</c>.
/// </remarks>
public sealed class CommitNoncesTlv : BaseTlv
{
    /// <summary>The size of the TLV value: two 66-byte public nonces.</summary>
    public const int ValueLength = 2 * MusigConstants.PublicNonceLen;

    /// <summary>The nonce for the commitment the <c>commitment_signed</c> of this session signs.</summary>
    public MusigPublicNonce CommitNonce { get; }

    /// <summary>The nonce for the commitment after it (the first <c>commitment_signed</c> of normal operation).</summary>
    public MusigPublicNonce NextCommitNonce { get; }

    public CommitNoncesTlv(MusigPublicNonce commitNonce, MusigPublicNonce nextCommitNonce)
        : base(TaprootTlvConstants.CommitNonces)
    {
        // ReSharper disable ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if ((byte[])commitNonce is null)
            throw new ArgumentException("The commit nonce is empty.", nameof(commitNonce));
        if ((byte[])nextCommitNonce is null)
            throw new ArgumentException("The next commit nonce is empty.", nameof(nextCommitNonce));
        // ReSharper restore ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract

        CommitNonce = commitNonce;
        NextCommitNonce = nextCommitNonce;

        Value = [.. (byte[])commitNonce, .. (byte[])nextCommitNonce];
        Length = Value.Length;
    }
}