namespace NLightning.Domain.Protocol.Tlv;

using Constants;
using Crypto.ValueObjects;

/// <summary>
/// Funding Nonce TLV.
/// </summary>
/// <remarks>
/// BOLTs PR #1324 <c>funding_nonce</c>, type 6 of <c>tx_complete</c>: [<c>66*byte</c>:<c>public_nonce</c>], the sender's
/// signing nonce for the shared taproot input (the previous funding output) of a splice. Eclair 0.14.3 sends it only
/// when the session spends a shared taproot input. Converted by <c>FundingNonceTlvConverter</c>.
/// </remarks>
public sealed class FundingNonceTlv(MusigPublicNonce nonce) : PublicNonceTlv(TaprootTlvConstants.FundingNonce, nonce);