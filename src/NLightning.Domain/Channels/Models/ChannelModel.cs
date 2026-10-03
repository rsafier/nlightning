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

    /// <summary>
    /// The derivation index of our funding key in the current funding (splicing plan D5): 0 for a channel never
    /// spliced, the locked splice's <c>ChannelFunding.LocalFundingKeyIndex</c> after a lock. Set with
    /// <see cref="SetLocalFundingKeyIndex"/> by the reload (from the current <c>ChannelFundings</c> row) and by the lock
    /// next to <see cref="ReplaceFundingOutput"/>; <see cref="GetSigningInfo"/> reports it with the current funding's
    /// keys (NL-495, wave spr).
    /// </summary>
    public uint LocalFundingKeyIndex { get; private set; }

    /// <summary>
    /// Our funding pubkey of the current funding: the funding output's (a locked splice rotates it, splicing plan D5),
    /// else the key set's. The 2-of-2 script and both anchor outputs of a commitment use the current funding's keys
    /// (BOLT 3), never the key set's once a splice locked.
    /// </summary>
    public CompactPubKey LocalFundingPubKey => FundingOutput?.LocalFundingPubKey ?? LocalKeySet.FundingCompactPubKey;

    /// <summary>
    /// The peer's funding pubkey of the current funding (see <see cref="LocalFundingPubKey"/>); null before the peer's
    /// key set is known.
    /// </summary>
    public CompactPubKey? RemoteFundingPubKey =>
        FundingOutput?.RemoteFundingPubKey ?? RemoteKeySet?.FundingCompactPubKey;
    public bool IsInitiator { get; }
    public CompactPubKey RemoteNodeId { get; }
    public ChannelState State { get; private set; }
    public ChannelVersion Version { get; }
    public WalletAddressModel? ChangeAddress { get; set; }

    /// <summary>
    /// The operator's label (NL-602 A3-T1, migration <c>AddAccountingFinancial</c>; at most 256 UTF-8 bytes), or null.
    /// The accounting writers copy it into the event's details.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// The operator's tags as one canonical <c>k=v</c> list (NL-602 A3-T1, at most 1 KiB), or null.
    /// </summary>
    public string? Tags { get; set; }

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
    /// The id of the first HTLC the peer added after our <c>shutdown</c>: the peer's next HTLC id when our
    /// <c>shutdown</c> was persisted (NL-279, B2-SHUT-S08). Every incoming HTLC with an id at or above it was added
    /// after our <c>shutdown</c> and is failed back instead of being routed or accepted. Null while we have not sent a
    /// <c>shutdown</c>, or for a <c>shutdown</c> persisted by a build that did not record it (such HTLCs are then
    /// handled as before).
    /// </summary>
    public ulong? FirstRemoteHtlcIdAfterLocalShutdown { get; private set; }

    /// <summary>
    /// The fully signed mutual close transaction both sides agreed on, persisted before it is broadcast
    /// (<see cref="ChannelState.Closing"/>), or null.
    /// </summary>
    public SignedTransaction? ClosingTransaction { get; private set; }

    /// <summary>
    /// The protocol the <see cref="ClosingTransaction"/> was agreed with (NL-610), or null: not recorded (a close
    /// agreed before it was, or a mutual close found on chain whose shape did not tell).
    /// </summary>
    public MutualCloseProtocol? CloseProtocol { get; private set; }

    /// <summary>
    /// For an <see cref="MutualCloseProtocol.Simple"/> close: true when we were the closer (our <c>closing_complete</c>,
    /// so we paid the fee), false when the peer was; null when not recorded or for a legacy close (NL-610).
    /// </summary>
    public bool? LocalIsCloser { get; private set; }

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

    /// <summary>
    /// Simple taproot channels (NL-877 T3): the peer's MuSig2 partial signature of our first commitment, with its
    /// signing nonce (<c>funding_created</c>/<c>funding_signed</c> <c>partial_signature_with_nonce</c>), kept as
    /// <see cref="LastReceivedSignature"/> is for the other channel types (which then holds no usable signature). Null
    /// for every other channel type.
    /// </summary>
    public MusigPartialSignatureWithNonce? LastReceivedPartialSignature { get; private set; }

    /// <summary>
    /// Simple taproot channels, during a v1 open (NL-877 T5): the peer's <c>next_local_nonce</c> of its
    /// <c>open_channel</c> (we are the fundee) or <c>accept_channel</c> (we are the funder), its verification nonce for
    /// its commitment 0, which our <c>funding_signed</c>/<c>funding_created</c> partial signature is made against.
    /// Memory only: the temporary channel it belongs to is never persisted.
    /// </summary>
    public MusigPublicNonce? RemoteOpeningNonce { get; set; }

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

    private LightningMoney _localBalance;
    private LightningMoney _remoteBalance;
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
        FundingOutput = MarkFormat(fundingOutput);
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

    /// <summary>
    /// Sets the <c>upfront_shutdown_script</c> we announce (NL-045), before <c>open_channel</c>/<c>accept_channel</c>
    /// is sent: only while the channel is being opened and has no upfront script yet, since BOLT 2 binds our
    /// <c>shutdown</c> to it (B2-SHUT-S09).
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel is past <see cref="ChannelState.V1Opening"/> or already
    /// has an upfront script.</exception>
    public void SetLocalUpfrontShutdownScript(BitcoinScript script)
    {
        if (State != ChannelState.V1Opening)
            throw new InvalidOperationException("Our upfront shutdown script can only be set while opening the channel");
        if (ChannelParams.Local.UpfrontShutdownScript is not null)
            throw new InvalidOperationException("Our upfront shutdown script is already set");

        ChannelParams = ChannelParams.WithLocal(ChannelParams.Local.WithUpfrontShutdownScript(script));
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

        FundingOutput = MarkFormat(fundingOutput);
    }

    /// <summary>
    /// A simple taproot channel's funding output is the MuSig2 P2TR one (<see cref="FundingOutputInfo.IsSimpleTaproot"/>,
    /// NL-877 T5): set here, so every builder that reads the channel's funding output builds the right script.
    /// </summary>
    private FundingOutputInfo? MarkFormat(FundingOutputInfo? fundingOutput)
    {
        if (fundingOutput is not null && ChannelParams.OptionSimpleTaproot)
            fundingOutput.IsSimpleTaproot = true;

        return fundingOutput;
    }

    /// <summary>
    /// A splice was locked (splicing plan §3.3: <see cref="FundingOutput"/> stays a view of the current funding): the
    /// channel now spends <paramref name="fundingOutput"/>, with its own outpoint, capacity and funding keys. The short
    /// channel id is not changed here (wave SP2).
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel has no funding output yet.</exception>
    public void ReplaceFundingOutput(FundingOutputInfo fundingOutput)
    {
        ArgumentNullException.ThrowIfNull(fundingOutput);
        if (FundingOutput is null)
            throw new InvalidOperationException("The channel has no funding output to replace");

        FundingOutput = MarkFormat(fundingOutput);
    }

    /// <summary>
    /// An RBF of a dual-funded open changed a contribution (BOLT 2 lets either side change its
    /// <c>funding_output_contribution</c> in <c>tx_init_rbf</c>/<c>tx_ack_rbf</c>, NL-521): the channel's funding output
    /// (outpoint and capacity, same funding keys), both balances and the parameters that follow the capacity (the
    /// reserve and in-flight limits) become the new attempt's. Only before the first snapshot (no <c>channel_ready</c>
    /// yet), while the channel is being opened.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel has a snapshot, is past
    /// <see cref="ChannelState.V1FundingSigned"/> or has no funding output yet.</exception>
    public void ReplaceUnconfirmedFunding(FundingOutputInfo fundingOutput, LightningMoney localBalance,
                                          LightningMoney remoteBalance, ChannelParams channelParams)
    {
        ArgumentNullException.ThrowIfNull(fundingOutput);
        ArgumentNullException.ThrowIfNull(localBalance);
        ArgumentNullException.ThrowIfNull(remoteBalance);
        if (Commitments is not null || State is not (ChannelState.V1Opening or ChannelState.V1FundingSigned))
            throw new InvalidOperationException("Only the funding of a channel that is still being opened can change");
        if (FundingOutput is null)
            throw new InvalidOperationException("The channel has no funding output to replace");
        if (localBalance.MilliSatoshi + remoteBalance.MilliSatoshi != fundingOutput.Amount.MilliSatoshi)
            throw new ArgumentException("The balances must add up to the funding output's amount",
                                        nameof(fundingOutput));

        FundingOutput = fundingOutput;
        _localBalance = localBalance;
        _remoteBalance = remoteBalance;
        ChannelParams = channelParams;
    }

    public void UpdateLastSentSignature(CompactSignature lastSentSignature)
    {
        LastSentSignature = lastSentSignature;
    }

    public void UpdateLastReceivedSignature(CompactSignature lastReceivedSignature)
    {
        LastReceivedSignature = lastReceivedSignature;
    }

    /// <summary>Sets <see cref="LastReceivedPartialSignature"/> (simple taproot channels).</summary>
    public void UpdateLastReceivedPartialSignature(MusigPartialSignatureWithNonce? lastReceivedPartialSignature)
    {
        LastReceivedPartialSignature = lastReceivedPartialSignature;
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

    /// <summary>
    /// Records the peer's next HTLC id at the moment our <c>shutdown</c> is persisted
    /// (<see cref="FirstRemoteHtlcIdAfterLocalShutdown"/>). Set once: a later call keeps the first value.
    /// </summary>
    public void SetFirstRemoteHtlcIdAfterLocalShutdown(ulong htlcId) =>
        FirstRemoteHtlcIdAfterLocalShutdown ??= htlcId;

    /// <summary>
    /// Lowers <see cref="FirstRemoteHtlcIdAfterLocalShutdown"/> to <paramref name="remoteNextHtlcId"/> when the peer's
    /// uncommitted adds were dropped (a reconnection, <c>RevertUncommitted</c>) after our <c>shutdown</c>: the peer
    /// reuses those ids for adds it sends on the new connection, where our <c>shutdown</c> is retransmitted after
    /// <c>channel_reestablish</c>, so they too come after it (NL-279). Nothing changes without our shutdown or when the
    /// boundary is already at or below it.
    /// </summary>
    /// <returns>True when the boundary was lowered (the channel row must be saved).</returns>
    public bool LowerFirstRemoteHtlcIdAfterLocalShutdown(ulong remoteNextHtlcId)
    {
        if (LocalShutdownScript is null || FirstRemoteHtlcIdAfterLocalShutdown is not { } first
                                        || remoteNextHtlcId >= first)
            return false;

        FirstRemoteHtlcIdAfterLocalShutdown = remoteNextHtlcId;
        return true;
    }

    /// <summary>
    /// Puts back a boundary lowered by <see cref="LowerFirstRemoteHtlcIdAfterLocalShutdown"/> whose save failed.
    /// </summary>
    public void RestoreFirstRemoteHtlcIdAfterLocalShutdown(ulong? htlcId) => FirstRemoteHtlcIdAfterLocalShutdown = htlcId;

    /// <summary>
    /// True when the peer added incoming HTLC <paramref name="htlcId"/> after our <c>shutdown</c> (BOLT 2: we SHOULD
    /// fail to route it, B2-SHUT-S08).
    /// </summary>
    public bool IsRemoteHtlcAddedAfterLocalShutdown(ulong htlcId) =>
        LocalShutdownScript is not null && FirstRemoteHtlcIdAfterLocalShutdown is { } first && htlcId >= first;

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

    /// <summary>Records how the closing transaction was agreed (NL-610): the protocol and, for a simple close, whether
    /// we were the closer.</summary>
    public void SetCloseTerms(MutualCloseProtocol? protocol, bool? localIsCloser)
    {
        CloseProtocol = protocol;
        LocalIsCloser = protocol == MutualCloseProtocol.Simple ? localIsCloser : null;
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

    /// <summary>Sets <see cref="LocalFundingKeyIndex"/> (NL-495): the current funding's key index.</summary>
    public void SetLocalFundingKeyIndex(uint localFundingKeyIndex)
    {
        LocalFundingKeyIndex = localFundingKeyIndex;
    }

    /// <summary>
    /// The signer's view of the channel. Contract (NL-495, wave spr): the funding fields are the <b>current</b>
    /// funding's (<see cref="FundingOutput"/>'s outpoint, capacity and keys, <see cref="LocalFundingPubKey"/>/
    /// <see cref="RemoteFundingPubKey"/>, and <see cref="LocalFundingKeyIndex"/>), not the key sets' original funding
    /// keys, so a signer that first learns the channel from the model after a splice lock (a restart without an
    /// <c>IChannelSigningInfoSource</c>) signs the spliced funding with the rotated key.
    /// </summary>
    public ChannelSigningInfo GetSigningInfo()
    {
        return new ChannelSigningInfo(FundingOutput!.TransactionId!.Value, FundingOutput.Index!.Value,
                                      FundingOutput.Amount, LocalFundingPubKey,
                                      RemoteFundingPubKey ?? RemoteKeySet!.FundingCompactPubKey, LocalKeySet.KeyIndex,
                                      RemoteKeySet!.HtlcCompactBasepoint, LocalCommitmentNumber, DataLossDetected)
        {
            RemoteNodeId = RemoteNodeId,
            ShortChannelId = ((byte[]?)ShortChannelId)?.Length > 0 ? ShortChannelId : (ShortChannelId?)null,
            AnnounceChannel = AnnounceChannel,
            LocalFundingKeyIndex = LocalFundingKeyIndex,
            IsSimpleTaproot = ChannelParams.OptionSimpleTaproot
        };
    }
}