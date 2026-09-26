namespace NLightning.Domain.Channels.Models;

using Bitcoin.Transactions.Outputs;
using Bitcoin.ValueObjects;
using Commitments;
using Crypto.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Protocol.Models;
using Enums;
using Money;
using ValueObjects;

public class ChannelModel
{
    #region Base Properties

    /// <summary>
    /// The parameters each side announced (<see cref="ChannelParams.Local"/> ours, <see cref="ChannelParams.Remote"/>
    /// the peer's) and the shared ones. See <see cref="ChannelParams"/> for which side each value binds.
    /// </summary>
    public ChannelParams ChannelParams { get; private set; }
    public ChannelId ChannelId { get; private set; }
    public ShortChannelId ShortChannelId { get; set; }
    /// <summary>
    /// The immutable obscuring helper shared by both commitments (built from the opener and accepter payment
    /// basepoints). The commitment numbers themselves are <see cref="LocalCommitmentNumber"/> and
    /// <see cref="RemoteCommitmentNumber"/>.
    /// </summary>
    public CommitmentNumber? CommitmentNumber { get; private set; }
    public uint FundingCreatedAtBlockHeight { get; set; }
    public FundingOutputInfo? FundingOutput { get; private set; }
    public bool IsInitiator { get; }
    public CompactPubKey RemoteNodeId { get; }
    public ChannelState State { get; private set; }
    public ChannelVersion Version { get; }
    public WalletAddressModel? ChangeAddress { get; set; }

    #endregion

    #region Commitment state

    /// <summary>
    /// The commitment state machine snapshot (plan §3.2), or null for a channel that has none yet (before the wiring
    /// creates one, or a channel opened before migration <c>AddCommitmentState</c>). Replace it only through
    /// <see cref="UpdateCommitments"/>, after the transition that produced it is saved (invariant I2).
    /// </summary>
    /// <remarks>
    /// While it is set, <see cref="LocalBalance"/>, <see cref="RemoteBalance"/>, the next HTLC ids and the commitment
    /// and revocation numbers are read from it, and <c>IChannelDbRepository.UpdateAsync</c> no longer writes them
    /// (<c>IChannelStateDbRepository</c> does).
    /// </remarks>
    public ChannelCommitments? Commitments { get; private set; }

    /// <summary>
    /// The wire bytes of the updates and <c>commitment_signed</c> we sent last, kept until the peer revokes (decision
    /// D4); retransmitted verbatim on reestablish.
    /// </summary>
    public ReadOnlyMemory<byte>? SentCommitDiff { get; private set; }

    /// <summary>Which of <c>commitment_signed</c>/<c>revoke_and_ack</c> we sent last (reestablish order).</summary>
    public LastSentCommitmentMessage LastSentCommitmentMessage { get; private set; }

    /// <summary>The <c>error</c> message we sent when we failed the channel, re-sent on every reconnection (N6-T3).</summary>
    public ReadOnlyMemory<byte>? ErrorSent { get; private set; }

    /// <summary>
    /// True once <c>channel_reestablish</c> proved that we lost data (plan §3.11 step 2): we must never sign or
    /// broadcast our commitment again (invariant I12).
    /// </summary>
    public bool DataLossDetected { get; private set; }

    #endregion

    #region Close state (BOLT2 plan N10)

    /// <summary>
    /// The script of the <c>shutdown</c> we sent (or are about to send: it is persisted before it goes out), or null
    /// while we have not sent one. Re-sent on every reconnection (B2-RE-28).
    /// </summary>
    public BitcoinScript? LocalShutdownScript { get; private set; }

    /// <summary>The script of the peer's <c>shutdown</c>, or null while it has not sent one.</summary>
    public BitcoinScript? RemoteShutdownScript { get; private set; }

    /// <summary>
    /// The fully signed mutual close transaction both sides agreed on, persisted before it is broadcast
    /// (<see cref="ChannelState.Closing"/>), or null.
    /// </summary>
    public SignedTransaction? ClosingTransaction { get; private set; }

    #endregion

    #region Announcement state (BOLT 7 plan G1)

    /// <summary>
    /// Whether the channel is public (<c>announce_channel</c> in <c>open_channel.channel_flags</c>); stored with the
    /// channel parameters (<see cref="ChannelParams.AnnounceChannel"/>).
    /// </summary>
    public bool AnnounceChannel => ChannelParams.AnnounceChannel;

    /// <summary>
    /// The peer's <c>announcement_signatures</c> for the channel's current short channel id, or null while none was
    /// received (or after <see cref="ResetAnnouncementSignatures"/>).
    /// </summary>
    public ChannelAnnouncementSignatures? RemoteAnnouncementSignatures { get; private set; }

    /// <summary>
    /// When we last sent our <c>announcement_signatures</c>, or null while we have not (persisted before it goes out,
    /// so a restart re-sends it until the peer's signatures are stored).
    /// </summary>
    public DateTimeOffset? LocalAnnouncementSignaturesSentAt { get; private set; }

    #endregion

    #region Signatures

    public CompactSignature? LastSentSignature { get; private set; }
    public CompactSignature? LastReceivedSignature { get; private set; }

    #endregion

    #region Local Information

    public ICollection<ShortChannelId>? LocalAliases { get; set; }

    /// <summary>
    /// The number of our current commitment transaction (0 after funding_signed). Our per-commitment point for it is
    /// <c>ILightningSigner.GetPerCommitmentPoint(channelId, LocalCommitmentNumber)</c>; the point we announce next
    /// (channel_ready, revoke_and_ack) is the one of <c>LocalCommitmentNumber + 1</c>. It never changes at funding
    /// confirmation (NL-188).
    /// </summary>
    public ulong LocalCommitmentNumber => Commitments?.LocalCommit.Number ?? _localCommitmentNumber;
    /// <summary>
    /// Our gross balance: it still includes the amounts of our pending offered HTLCs (<see cref="LocalOfferedHtlcs"/>).
    /// The commitment transaction factory takes each HTLC out of the balance of the side that offered it, so an HTLC
    /// update flow must not deduct an offered HTLC from this balance when it is added, only when it is settled.
    /// </summary>
    public LightningMoney LocalBalance =>
        Commitments is null ? _localBalance : LightningMoney.MilliSatoshis(Commitments.LocalBalanceMsat);
    public ChannelKeySetModel LocalKeySet { get; }
    public ulong LocalNextHtlcId => Commitments?.LocalNextHtlcId ?? _localNextHtlcId;
    public ICollection<Htlc>? LocalOfferedHtlcs { get; }
    public ICollection<Htlc>? LocalFulfilledHtlcs { get; }
    public ICollection<Htlc>? LocalOldHtlcs { get; }
    /// <summary>
    /// How many of our commitments we revoked. With a snapshot it equals the local commitment number, because the
    /// engine revokes the previous commitment in the same transition that accepts the new one.
    /// </summary>
    public ulong LocalRevocationNumber => Commitments?.LocalCommit.Number ?? _localRevocationNumber;
    public BitcoinScript? LocalUpfrontShutdownScript => ChannelParams.Local.UpfrontShutdownScript;

    #endregion

    #region Remote Information

    public ShortChannelId? RemoteAlias { get; set; }

    /// <summary>
    /// The number of the peer's current commitment transaction (0 after funding_created/funding_signed). It advances
    /// independently of <see cref="LocalCommitmentNumber"/> during the commitment dance (NL-188).
    /// </summary>
    public ulong RemoteCommitmentNumber => Commitments?.RemoteCommit.Number ?? _remoteCommitmentNumber;
    /// <summary>
    /// The remote's gross balance: it still includes the amounts of the remote's pending offered HTLCs
    /// (<see cref="RemoteOfferedHtlcs"/>). See <see cref="LocalBalance"/> for the convention.
    /// </summary>
    public LightningMoney RemoteBalance =>
        Commitments is null ? _remoteBalance : LightningMoney.MilliSatoshis(Commitments.RemoteBalanceMsat);
    public ChannelKeySetModel? RemoteKeySet { get; private set; }
    public ulong RemoteNextHtlcId => Commitments?.RemoteNextHtlcId ?? _remoteNextHtlcId;
    /// <summary>
    /// How many commitments the peer revoked. With a snapshot it equals the peer's current commitment number (an
    /// unacked commitment we signed is not counted until its <c>revoke_and_ack</c>).
    /// </summary>
    public ulong RemoteRevocationNumber => Commitments?.RemoteCommit.Number ?? _remoteRevocationNumber;
    public ICollection<Htlc>? RemoteFulfilledHtlcs { get; }
    public ICollection<Htlc>? RemoteOfferedHtlcs { get; }
    public ICollection<Htlc>? RemoteOldHtlcs { get; }
    public BitcoinScript? RemoteUpfrontShutdownScript => ChannelParams.Remote.UpfrontShutdownScript;

    #endregion

    private readonly LightningMoney _localBalance;
    private readonly LightningMoney _remoteBalance;
    private readonly ulong _localNextHtlcId;
    private readonly ulong _remoteNextHtlcId;
    private readonly ulong _localCommitmentNumber;
    private readonly ulong _remoteCommitmentNumber;
    private readonly ulong _localRevocationNumber;
    private readonly ulong _remoteRevocationNumber;

    public ChannelModel(ChannelParams channelParams, ChannelId channelId, CommitmentNumber? commitmentNumber,
                        FundingOutputInfo? fundingOutput, bool isInitiator, CompactSignature? lastSentSignature,
                        CompactSignature? lastReceivedSignature, LightningMoney localBalance,
                        ChannelKeySetModel localKeySet, ulong localNextHtlcId, ulong localRevocationNumber,
                        LightningMoney remoteBalance, ChannelKeySetModel? remoteKeySet, ulong remoteNextHtlcId,
                        CompactPubKey remoteNodeId, ulong remoteRevocationNumber, ChannelState state,
                        ChannelVersion version, ICollection<Htlc>? localOfferedHtlcs = null,
                        ICollection<Htlc>? localFulfilledHtlcs = null, ICollection<Htlc>? localOldHtlcs = null,
                        ICollection<Htlc>? remoteOfferedHtlcs = null, ICollection<Htlc>? remoteFulfilledHtlcs = null,
                        ICollection<Htlc>? remoteOldHtlcs = null, ulong localCommitmentNumber = 0,
                        ulong remoteCommitmentNumber = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localCommitmentNumber, CommitmentNumber.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remoteCommitmentNumber, CommitmentNumber.MaxValue);

        ChannelParams = channelParams;
        ChannelId = channelId;
        CommitmentNumber = commitmentNumber;
        FundingOutput = fundingOutput;
        IsInitiator = isInitiator;
        LastSentSignature = lastSentSignature;
        LastReceivedSignature = lastReceivedSignature;
        _localBalance = localBalance;
        LocalKeySet = localKeySet;
        _localNextHtlcId = localNextHtlcId;
        _localRevocationNumber = localRevocationNumber;
        _remoteBalance = remoteBalance;
        RemoteKeySet = remoteKeySet;
        _remoteNextHtlcId = remoteNextHtlcId;
        _remoteRevocationNumber = remoteRevocationNumber;
        State = state;
        Version = version;
        RemoteNodeId = remoteNodeId;
        LocalOfferedHtlcs = localOfferedHtlcs ?? new List<Htlc>();
        LocalFulfilledHtlcs = localFulfilledHtlcs ?? new List<Htlc>();
        LocalOldHtlcs = localOldHtlcs ?? new List<Htlc>();
        RemoteOfferedHtlcs = remoteOfferedHtlcs ?? new List<Htlc>();
        RemoteFulfilledHtlcs = remoteFulfilledHtlcs ?? new List<Htlc>();
        RemoteOldHtlcs = remoteOldHtlcs ?? new List<Htlc>();
        _localCommitmentNumber = localCommitmentNumber;
        _remoteCommitmentNumber = remoteCommitmentNumber;
    }

    public void UpdateState(ChannelState newState)
    {
        if (State == ChannelState.V2Opening && newState < ChannelState.V2Opening
         || State >= ChannelState.V1Opening && newState == ChannelState.V2Opening)
            throw new ArgumentOutOfRangeException(nameof(newState), "Invalid channel state for update.");

        if (newState <= State)
            throw new ArgumentOutOfRangeException(nameof(newState), "New state must be greater than current state.");

        State = newState;
    }

    public void UpdateChannelId(ChannelId newChannelId)
    {
        if (newChannelId == ChannelId.Zero)
            throw new ArgumentException("New channel ID cannot be empty.", nameof(newChannelId));

        ChannelId = newChannelId;
    }

    /// <summary>
    /// Stores the parameters the peer announced (the initiator learns them from <c>accept_channel</c>).
    /// Our own parameters never change.
    /// </summary>
    public void UpdateRemoteParams(ChannelParty remoteParams)
    {
        ChannelParams = ChannelParams.WithRemote(remoteParams);
    }

    public void AddRemoteKeySet(ChannelKeySetModel remoteKeySet)
    {
        if (RemoteKeySet is not null)
            throw new InvalidOperationException("Remote key set already set");

        RemoteKeySet = remoteKeySet;
    }

    public void AddCommitmentNumber(CommitmentNumber commitmentNumber)
    {
        if (CommitmentNumber is not null)
            throw new InvalidOperationException("Commitment number already set");

        CommitmentNumber = commitmentNumber;
    }

    public void AddFundingOutput(FundingOutputInfo fundingOutput)
    {
        if (FundingOutput is not null)
            throw new InvalidOperationException("Funding output already set");

        FundingOutput = fundingOutput;
    }

    public void UpdateLastSentSignature(CompactSignature lastSentSignature)
    {
        LastSentSignature = lastSentSignature;
    }

    public void UpdateLastReceivedSignature(CompactSignature lastReceivedSignature)
    {
        LastReceivedSignature = lastReceivedSignature;
    }

    /// <summary>
    /// Swaps in the snapshot produced by a transition, after that transition was saved (invariant I2), together with
    /// the extras saved with it (same rules as <c>IChannelStateDbRepository.ApplyAsync</c>: a null member is unchanged,
    /// and the sent diff is cleared once there is no unacked remote commitment).
    /// </summary>
    /// <exception cref="ArgumentException">The snapshot belongs to another channel.</exception>
    public void UpdateCommitments(ChannelCommitments next, ChannelStateExtras? extras = null)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.ChannelId != ChannelId)
            throw new ArgumentException($"The snapshot belongs to channel {next.ChannelId}, not {ChannelId}",
                                        nameof(next));

        Commitments = next;
        if (extras?.SentCommitDiff is { } diff)
            SentCommitDiff = diff.ToArray();
        if (next.RemoteNextCommit is null)
            SentCommitDiff = null;
        if (extras?.LastSent is { } lastSent)
            LastSentCommitmentMessage = lastSent;
    }

    /// <summary>
    /// Records the <c>error</c> we sent when failing the channel (N6-T3), so it can be re-sent on reconnection.
    /// </summary>
    public void MarkErrorSent(ReadOnlyMemory<byte> errorMessage)
    {
        ErrorSent = errorMessage.ToArray();
    }

    /// <summary>Records the script of the <c>shutdown</c> we send (BOLT 2: only once, so it never changes).</summary>
    /// <exception cref="InvalidOperationException">Another script was already recorded.</exception>
    public void SetLocalShutdownScript(BitcoinScript script)
    {
        if (LocalShutdownScript is { } current && current != script)
            throw new InvalidOperationException("Our shutdown script is already set");

        LocalShutdownScript = script;
    }

    /// <summary>Records the script of the peer's <c>shutdown</c>.</summary>
    /// <exception cref="InvalidOperationException">Another script was already recorded.</exception>
    public void SetRemoteShutdownScript(BitcoinScript script)
    {
        if (RemoteShutdownScript is { } current && current != script)
            throw new InvalidOperationException("The peer's shutdown script is already set");

        RemoteShutdownScript = script;
    }

    /// <summary>
    /// Replaces the peer's script with the <c>closer_scriptpubkey</c> of a <c>closing_complete</c> we signed (BOLT 2
    /// <c>option_simple_close</c>: the closee MUST use it for its own later <c>closing_complete</c>). Only for that
    /// flow: the legacy close keeps the <c>shutdown</c> script (<see cref="SetRemoteShutdownScript"/>).
    /// </summary>
    public void ReplaceRemoteShutdownScript(BitcoinScript script)
    {
        if (RemoteShutdownScript is null)
            throw new InvalidOperationException("The peer's shutdown script is not set yet");

        RemoteShutdownScript = script;
    }

    /// <summary>Records the agreed, fully signed mutual close transaction.</summary>
    public void SetClosingTransaction(SignedTransaction closingTransaction)
    {
        ArgumentNullException.ThrowIfNull(closingTransaction);
        ClosingTransaction = closingTransaction;
    }

    /// <summary>Stores the peer's <c>announcement_signatures</c> for the channel's current short channel id.</summary>
    public void SetRemoteAnnouncementSignatures(ChannelAnnouncementSignatures signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        RemoteAnnouncementSignatures = signatures;
    }

    /// <summary>Records when we sent our <c>announcement_signatures</c>.</summary>
    public void MarkAnnouncementSignaturesSent(DateTimeOffset sentAt)
    {
        LocalAnnouncementSignaturesSentAt = sentAt;
    }

    /// <summary>
    /// Forgets both sides' announcement state, e.g. when a reorg moved the short channel id: the signatures of the old
    /// one are useless for the new announcement.
    /// </summary>
    public void ResetAnnouncementSignatures()
    {
        RemoteAnnouncementSignatures = null;
        LocalAnnouncementSignaturesSentAt = null;
    }

    /// <summary>Records that <c>channel_reestablish</c> proved we lost data (never cleared).</summary>
    public void MarkDataLossDetected()
    {
        DataLossDetected = true;
    }

    public ChannelSigningInfo GetSigningInfo()
    {
        return new ChannelSigningInfo(FundingOutput!.TransactionId!.Value, FundingOutput.Index!.Value,
                                      FundingOutput.Amount, LocalKeySet.FundingCompactPubKey,
                                      RemoteKeySet!.FundingCompactPubKey, LocalKeySet.KeyIndex,
                                      RemoteKeySet.HtlcCompactBasepoint, LocalCommitmentNumber, DataLossDetected)
        {
            RemoteNodeId = RemoteNodeId,
            ShortChannelId = ((byte[]?)ShortChannelId)?.Length > 0 ? ShortChannelId : (ShortChannelId?)null,
            AnnounceChannel = AnnounceChannel
        };
    }
}