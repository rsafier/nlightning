namespace NLightning.Domain.Channels.Models;

using Bitcoin.Transactions.Outputs;
using Bitcoin.ValueObjects;
using Crypto.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Protocol.Models;
using Enums;
using Money;
using ValueObjects;

public class ChannelModel
{
    #region Base Properties

    public ChannelConfig ChannelConfig { get; private set; }
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
    public ulong LocalCommitmentNumber { get; }
    /// <summary>
    /// Our gross balance: it still includes the amounts of our pending offered HTLCs (<see cref="LocalOfferedHtlcs"/>).
    /// The commitment transaction factory takes each HTLC out of the balance of the side that offered it, so an HTLC
    /// update flow must not deduct an offered HTLC from this balance when it is added, only when it is settled.
    /// </summary>
    public LightningMoney LocalBalance { get; }
    public ChannelKeySetModel LocalKeySet { get; }
    public ulong LocalNextHtlcId { get; }
    public ICollection<Htlc>? LocalOfferedHtlcs { get; }
    public ICollection<Htlc>? LocalFulfilledHtlcs { get; }
    public ICollection<Htlc>? LocalOldHtlcs { get; }
    public ulong LocalRevocationNumber { get; }
    public BitcoinScript? LocalUpfrontShutdownScript { get; }

    #endregion

    #region Remote Information

    public ShortChannelId? RemoteAlias { get; set; }

    /// <summary>
    /// The number of the peer's current commitment transaction (0 after funding_created/funding_signed). It advances
    /// independently of <see cref="LocalCommitmentNumber"/> during the commitment dance (NL-188).
    /// </summary>
    public ulong RemoteCommitmentNumber { get; }
    /// <summary>
    /// The remote's gross balance: it still includes the amounts of the remote's pending offered HTLCs
    /// (<see cref="RemoteOfferedHtlcs"/>). See <see cref="LocalBalance"/> for the convention.
    /// </summary>
    public LightningMoney RemoteBalance { get; }
    public ChannelKeySetModel? RemoteKeySet { get; private set; }
    public ulong RemoteNextHtlcId { get; }
    public ulong RemoteRevocationNumber { get; }
    public ICollection<Htlc>? RemoteFulfilledHtlcs { get; }
    public ICollection<Htlc>? RemoteOfferedHtlcs { get; }
    public ICollection<Htlc>? RemoteOldHtlcs { get; }
    public BitcoinScript? RemoteUpfrontShutdownScript { get; }

    #endregion

    public ChannelModel(ChannelConfig channelConfig, ChannelId channelId, CommitmentNumber? commitmentNumber,
                        FundingOutputInfo? fundingOutput, bool isInitiator, CompactSignature? lastSentSignature,
                        CompactSignature? lastReceivedSignature, LightningMoney localBalance,
                        ChannelKeySetModel localKeySet, ulong localNextHtlcId, ulong localRevocationNumber,
                        LightningMoney remoteBalance, ChannelKeySetModel? remoteKeySet, ulong remoteNextHtlcId,
                        CompactPubKey remoteNodeId, ulong remoteRevocationNumber, ChannelState state,
                        ChannelVersion version, ICollection<Htlc>? localOfferedHtlcs = null,
                        ICollection<Htlc>? localFulfilledHtlcs = null, ICollection<Htlc>? localOldHtlcs = null,
                        BitcoinScript? localUpfrontShutdownScript = null, ICollection<Htlc>? remoteOfferedHtlcs = null,
                        ICollection<Htlc>? remoteFulfilledHtlcs = null, ICollection<Htlc>? remoteOldHtlcs = null,
                        BitcoinScript? remoteUpfrontShutdownScript = null, ulong localCommitmentNumber = 0,
                        ulong remoteCommitmentNumber = 0)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localCommitmentNumber, CommitmentNumber.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remoteCommitmentNumber, CommitmentNumber.MaxValue);

        ChannelConfig = channelConfig;
        ChannelId = channelId;
        CommitmentNumber = commitmentNumber;
        FundingOutput = fundingOutput;
        IsInitiator = isInitiator;
        LastSentSignature = lastSentSignature;
        LastReceivedSignature = lastReceivedSignature;
        LocalBalance = localBalance;
        LocalKeySet = localKeySet;
        LocalNextHtlcId = localNextHtlcId;
        LocalRevocationNumber = localRevocationNumber;
        RemoteBalance = remoteBalance;
        RemoteKeySet = remoteKeySet;
        RemoteNextHtlcId = remoteNextHtlcId;
        RemoteRevocationNumber = remoteRevocationNumber;
        State = state;
        Version = version;
        RemoteNodeId = remoteNodeId;
        LocalOfferedHtlcs = localOfferedHtlcs ?? new List<Htlc>();
        LocalFulfilledHtlcs = localFulfilledHtlcs ?? new List<Htlc>();
        LocalOldHtlcs = localOldHtlcs ?? new List<Htlc>();
        RemoteOfferedHtlcs = remoteOfferedHtlcs ?? new List<Htlc>();
        RemoteFulfilledHtlcs = remoteFulfilledHtlcs ?? new List<Htlc>();
        RemoteOldHtlcs = remoteOldHtlcs ?? new List<Htlc>();
        LocalUpfrontShutdownScript = localUpfrontShutdownScript;
        RemoteUpfrontShutdownScript = remoteUpfrontShutdownScript;
        LocalCommitmentNumber = localCommitmentNumber;
        RemoteCommitmentNumber = remoteCommitmentNumber;
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

    public void UpdateChannelConfig(ChannelConfig channelConfig)
    {
        ChannelConfig = channelConfig;
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

    public ChannelSigningInfo GetSigningInfo()
    {
        return new ChannelSigningInfo(FundingOutput!.TransactionId!.Value, FundingOutput.Index!.Value,
                                      FundingOutput.Amount, LocalKeySet.FundingCompactPubKey,
                                      RemoteKeySet!.FundingCompactPubKey, LocalKeySet.KeyIndex,
                                      RemoteKeySet.HtlcCompactBasepoint, LocalCommitmentNumber);
    }
}