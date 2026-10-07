namespace NLightning.Domain.Protocol.Enums;

/// <summary>Fixed key derivation domains for encrypted node data; no arbitrary derivation labels.</summary>
public enum NodeDataPurpose
{
    ChannelBackup = 0,
    PeerStorage = 1
}