namespace NLightning.Domain.Client.Enums;

/// <summary>
/// Commands sent by a client.
/// </summary>
/// <remarks>
/// Append-only: never renumber a value, the client and daemon exchange them on the wire. The next free value is 17.
/// </remarks>
public enum ClientCommand
{
    // Reserve 0 for unknown
    Unknown = 0,
    NodeInfo = 1,
    ConnectPeer = 2,
    ListPeers = 3,
    GetAddress = 4,
    WalletBalance = 5,
    OpenChannel = 6,
    OpenChannelSubscription = 7,
    ListChannels = 8,
    CreateInvoice = 9,
    PayInvoice = 10,
    ListInvoices = 11,
    ListPayments = 12,
    CloseChannel = 13,
    ForceCloseChannel = 14,
    PendingSweeps = 15,
    ChainStatus = 16
}