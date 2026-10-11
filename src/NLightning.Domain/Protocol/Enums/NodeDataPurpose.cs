namespace NLightning.Domain.Protocol.Enums;

/// <summary>Fixed key derivation domains for encrypted node data; no arbitrary derivation labels.</summary>
public enum NodeDataPurpose
{
    ChannelBackup = 0,
    PeerStorage = 1,

    /// <summary>The LND gRPC macaroon root keys kept in the database (NL-1389).</summary>
    MacaroonRootKey = 2
}