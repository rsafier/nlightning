namespace NLightning.Domain.Protocol.Constants;

/// <summary>
/// Represents the message types used in the Lightning Network.
/// </summary>
public enum MessageTypes : ushort
{
    #region Setup & Control

    Warning = 1,
    Stfu = 2,
    PeerStorage = 7,
    PeerStorageRetrieval = 9,
    Init = 16,
    Error = 17,
    Ping = 18,
    Pong = 19,

    #endregion

    #region Channel

    OpenChannel = 32,
    AcceptChannel = 33,
    FundingCreated = 34,
    FundingSigned = 35,
    ChannelReady = 36,
    Shutdown = 38,
    ClosingSigned = 39,
    ClosingComplete = 40,
    ClosingSig = 41,
    OpenChannel2 = 64,
    AcceptChannel2 = 65,

    #endregion

    #region Splicing

    /// <summary>BOLT 2 <c>splice_locked</c> (SP-LK-01).</summary>
    SpliceLocked = 77,

    /// <summary>BOLT 2 <c>splice_init</c> (SP-W-01).</summary>
    SpliceInit = 80,

    /// <summary>BOLT 2 <c>splice_ack</c> (SP-W-02).</summary>
    SpliceAck = 81,

    /// <summary>BOLT 2 "Batching channel messages" <c>start_batch</c> (SP-OP-03/04).</summary>
    StartBatch = 127,

    #endregion

    #region Interactive Transaction Construction

    TxAddInput = 66,
    TxAddOutput = 67,
    TxRemoveInput = 68,
    TxRemoveOutput = 69,
    TxComplete = 70,
    TxSignatures = 71,
    TxInitRbf = 72,
    TxAckRbf = 73,
    TxAbort = 74,

    #endregion

    #region Commitment

    UpdateAddHtlc = 128,
    UpdateFulfillHtlc = 130,
    UpdateFailHtlc = 131,
    CommitmentSigned = 132,
    RevokeAndAck = 133,
    UpdateFee = 134,
    UpdateFailMalformedHtlc = 135,
    ChannelReestablish = 136,

    #endregion

    #region Routing

    ChannelAnnouncement = 256,
    NodeAnnouncement = 257,
    ChannelUpdate = 258,
    AnnouncementSignatures = 259,
    AnnouncementSignatures2 = 260,
    QueryShortChannelIds = 261,
    ReplyShortChannelIdsEnd = 262,
    QueryChannelRange = 263,
    ReplyChannelRange = 264,
    GossipTimestampFilter = 265,
    ChannelAnnouncement2 = 267,
    NodeAnnouncement2 = 269,
    ChannelUpdate2 = 271,

    #endregion

    #region Onion Messages

    OnionMessage = 513

    #endregion
}