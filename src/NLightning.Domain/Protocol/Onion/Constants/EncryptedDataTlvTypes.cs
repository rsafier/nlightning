using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Protocol.Onion.Constants;

using Protocol.ValueObjects;

/// <summary>
/// TLV types of the BOLT 4 route blinding <c>encrypted_data_tlv</c> stream.
/// </summary>
[ExcludeFromCodeCoverage]
public static class EncryptedDataTlvTypes
{
    /// <summary>
    /// padding (variable bytes).
    /// </summary>
    public static readonly BigSize Padding = 1;

    /// <summary>
    /// short_channel_id (8 bytes).
    /// </summary>
    public static readonly BigSize ShortChannelId = 2;

    /// <summary>
    /// next_node_id (point).
    /// </summary>
    public static readonly BigSize NextNodeId = 4;

    /// <summary>
    /// path_id (variable bytes).
    /// </summary>
    public static readonly BigSize PathId = 6;

    /// <summary>
    /// next_path_key_override (point).
    /// </summary>
    public static readonly BigSize NextPathKeyOverride = 8;

    /// <summary>
    /// payment_relay (u16 cltv_expiry_delta || u32 fee_proportional_millionths || tu32 fee_base_msat).
    /// </summary>
    public static readonly BigSize PaymentRelay = 10;

    /// <summary>
    /// payment_constraints (u32 max_cltv_expiry || tu64 htlc_minimum_msat).
    /// </summary>
    public static readonly BigSize PaymentConstraints = 12;

    /// <summary>
    /// allowed_features (variable bytes).
    /// </summary>
    public static readonly BigSize AllowedFeatures = 14;
}