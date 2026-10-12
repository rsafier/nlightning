namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Current Commit Nonce TLV.
/// </summary>
/// <remarks>
/// BOLTs PR #1324 <c>current_commit_nonce</c>, type 24 of <c>channel_reestablish</c>:
/// [<c>66*byte</c>:<c>public_nonce</c>], sent while the sender still misses the peer's <c>commitment_signed</c> for an
/// interactive transaction (a dual-funded open or a splice), so the peer can re-sign it. Converted by
/// <c>CurrentCommitNonceTlvConverter</c>.
/// </remarks>
public sealed class CurrentCommitNonceTlv(MusigPublicNonce nonce)
    : PublicNonceTlv(TaprootTlvConstants.CurrentCommitNonce, nonce);