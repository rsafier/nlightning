namespace NLightning.Domain.Protocol.Onion.Enums;

/// <summary>
/// BOLT 4 failure codes ("Returning Errors" / "Failure Messages").
/// </summary>
/// <remarks>
/// Each value is the combination of its <see cref="FailureCodeFlags"/> and the low-order code number. The legacy
/// codes PERM|16 and 17, which BOLT 4 still describes in a note, are included so they are understood when received.
/// </remarks>
public enum FailureCode : ushort
{
    /// <summary>
    /// NODE|2: general temporary failure of the processing node.
    /// </summary>
    TemporaryNodeFailure = (ushort)FailureCodeFlags.Node | 2,

    /// <summary>
    /// PERM|NODE|2: general permanent failure of the processing node.
    /// </summary>
    PermanentNodeFailure = (ushort)FailureCodeFlags.Perm | (ushort)FailureCodeFlags.Node | 2,

    /// <summary>
    /// PERM|NODE|3: the processing node has a required feature which was not in this onion.
    /// </summary>
    RequiredNodeFeatureMissing = (ushort)FailureCodeFlags.Perm | (ushort)FailureCodeFlags.Node | 3,

    /// <summary>
    /// BADONION|PERM|4: the version byte was not understood. Data: sha256_of_onion.
    /// </summary>
    InvalidOnionVersion = (ushort)FailureCodeFlags.BadOnion | (ushort)FailureCodeFlags.Perm | 4,

    /// <summary>
    /// BADONION|PERM|5: the HMAC of the onion is incorrect. Data: sha256_of_onion.
    /// </summary>
    InvalidOnionHmac = (ushort)FailureCodeFlags.BadOnion | (ushort)FailureCodeFlags.Perm | 5,

    /// <summary>
    /// BADONION|PERM|6: the ephemeral key was unparsable. Data: sha256_of_onion.
    /// </summary>
    InvalidOnionKey = (ushort)FailureCodeFlags.BadOnion | (ushort)FailureCodeFlags.Perm | 6,

    /// <summary>
    /// UPDATE|7: the outgoing channel is temporarily unable to handle this HTLC.
    /// Data: u16 len || channel_update (may be empty, len = 0).
    /// </summary>
    TemporaryChannelFailure = (ushort)FailureCodeFlags.Update | 7,

    /// <summary>
    /// PERM|8: the outgoing channel is unable to handle any HTLCs.
    /// </summary>
    PermanentChannelFailure = (ushort)FailureCodeFlags.Perm | 8,

    /// <summary>
    /// PERM|9: the outgoing channel has a requirement advertised in its channel_announcement which is missing.
    /// </summary>
    RequiredChannelFeatureMissing = (ushort)FailureCodeFlags.Perm | 9,

    /// <summary>
    /// PERM|10: the onion specified a short_channel_id that doesn't match any leading from the processing node.
    /// </summary>
    UnknownNextPeer = (ushort)FailureCodeFlags.Perm | 10,

    /// <summary>
    /// UPDATE|11: the HTLC amount was below the outgoing channel's htlc_minimum_msat.
    /// Data: u64 htlc_msat || u16 len || channel_update (may be empty, len = 0).
    /// </summary>
    AmountBelowMinimum = (ushort)FailureCodeFlags.Update | 11,

    /// <summary>
    /// UPDATE|12: the fee amount was below that required by the outgoing channel.
    /// Data: u64 htlc_msat || u16 len || channel_update (may be empty, len = 0).
    /// </summary>
    FeeInsufficient = (ushort)FailureCodeFlags.Update | 12,

    /// <summary>
    /// UPDATE|13: the cltv_expiry does not comply with the outgoing channel's cltv_expiry_delta.
    /// Data: u32 cltv_expiry || u16 len || channel_update (may be empty, len = 0).
    /// </summary>
    IncorrectCltvExpiry = (ushort)FailureCodeFlags.Update | 13,

    /// <summary>
    /// UPDATE|14: the CLTV expiry is too close to the current block height.
    /// Data: u16 len || channel_update (may be empty, len = 0).
    /// </summary>
    ExpiryTooSoon = (ushort)FailureCodeFlags.Update | 14,

    /// <summary>
    /// PERM|15: the payment_hash is unknown, the payment_secret doesn't match, the amount is incorrect or the CLTV
    /// expiry is too close. Data: u64 htlc_msat || u32 height.
    /// </summary>
    IncorrectOrUnknownPaymentDetails = (ushort)FailureCodeFlags.Perm | 15,

    /// <summary>
    /// PERM|16: legacy <c>incorrect_payment_amount</c>, no data. Deprecated by BOLT 4 in favour of
    /// <see cref="IncorrectOrUnknownPaymentDetails"/>; never sent, only recognised when received from older peers.
    /// </summary>
    IncorrectPaymentAmount = (ushort)FailureCodeFlags.Perm | 16,

    /// <summary>
    /// 17: legacy <c>final_expiry_too_soon</c>, no data. Deprecated by BOLT 4 in favour of
    /// <see cref="IncorrectOrUnknownPaymentDetails"/>; never sent, only recognised when received from older peers.
    /// BOLT 4 notes it is non-permanent (the block height may have changed since sending), so the origin MAY retry.
    /// </summary>
    FinalExpiryTooSoon = 17,

    /// <summary>
    /// 18: the CLTV expiry in the HTLC is less than the value in the onion. Data: u32 cltv_expiry.
    /// </summary>
    FinalIncorrectCltvExpiry = 18,

    /// <summary>
    /// 19: the HTLC amount is less than the value in the onion. Data: u64 incoming_htlc_amt.
    /// </summary>
    FinalIncorrectHtlcAmount = 19,

    /// <summary>
    /// UPDATE|20: the outgoing channel has been disabled.
    /// Data: u16 disabled_flags || u16 len || channel_update (may be empty, len = 0).
    /// </summary>
    ChannelDisabled = (ushort)FailureCodeFlags.Update | 20,

    /// <summary>
    /// 21: the CLTV expiry in the HTLC is too far in the future.
    /// </summary>
    ExpiryTooFar = 21,

    /// <summary>
    /// PERM|22: the decrypted onion per-hop payload was not understood or is incomplete.
    /// Data: bigsize type || u16 offset.
    /// </summary>
    InvalidOnionPayload = (ushort)FailureCodeFlags.Perm | 22,

    /// <summary>
    /// 23: the complete amount of the multi-part payment was not received within a reasonable time.
    /// </summary>
    MppTimeout = 23,

    /// <summary>
    /// BADONION|PERM|24: an error occurred within the blinded route. Data: sha256_of_onion.
    /// </summary>
    InvalidOnionBlinding = (ushort)FailureCodeFlags.BadOnion | (ushort)FailureCodeFlags.Perm | 24
}