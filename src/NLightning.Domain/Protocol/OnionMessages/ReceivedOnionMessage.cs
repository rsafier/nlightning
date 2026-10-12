namespace NLightning.Domain.Protocol.OnionMessages;

using Crypto.ValueObjects;

/// <summary>
/// An onion message delivered to us as its final hop, after the BOLT 4 reader checks (peeled, unblinded, at most one
/// payload field).
/// </summary>
/// <param name="Contents">The final hop's records other than <c>reply_path</c> and <c>encrypted_recipient_data</c>.
/// </param>
/// <param name="ReplyPath">The sender's <c>reply_path</c>, or null when it allows no reply.</param>
/// <param name="PathId">The <c>path_id</c> of our decrypted <c>encrypted_recipient_data</c>, or null when it has none
/// (the sender built the path to us itself). A path_id names the blinded path of ours the message came through, which
/// BOLT 12 needs (an invoice_request must arrive through one of the offer's paths).</param>
/// <param name="FromPeer">The peer that handed the message to us (the last hop, not the sender).</param>
public sealed record ReceivedOnionMessage(OnionMessageContents Contents, WireBlindedPath? ReplyPath,
                                          ReadOnlyMemory<byte>? PathId, CompactPubKey FromPeer);