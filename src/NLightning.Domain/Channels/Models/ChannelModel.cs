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

    /// <summary>
    /// The parameters each side announced (<see cref="ChannelParams.Local"/> ours, <see cref="ChannelParams.Remote"/>
    /// the peer's) and the shared ones. See <see cref="ChannelParams"/> for which side each value binds.
    /// </summary>
    public ChannelParams ChannelParams { get; private set; }
    public ChannelId ChannelId { get; private set; }
    public ShortChannelId ShortChannelId { get; set; }
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
    public BitcoinScript? LocalUpfrontShutdownScript => ChannelParams.Local.UpfrontShutdownScript;

    #endregion

    #region Remote Information

    public ShortChannelId? RemoteAlias { get; set; }
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
    public BitcoinScript? RemoteUpfrontShutdownScript => ChannelParams.Remote.UpfrontShutdownScript;

    #endregion

    public ChannelModel(ChannelParams channelParams, ChannelId channelId, CommitmentNumber? commitmentNumber,
                        FundingOutputInfo? fundingOutput, bool isInitiator, CompactSignature? lastSentSignature,
                        CompactSignature? lastReceivedSignature, LightningMoney localBalance,
                        ChannelKeySetModel localKeySet, ulong localNextHtlcId, ulong localRevocationNumber,
                        LightningMoney remoteBalance, ChannelKeySetModel? remoteKeySet, ulong remoteNextHtlcId,
                        CompactPubKey remoteNodeId, ulong remoteRevocationNumber, ChannelState state,
                        ChannelVersion version, ICollection<Htlc>? localOfferedHtlcs = null,
                        ICollection<Htlc>? localFulfilledHtlcs = null, ICollection<Htlc>? localOldHtlcs = null,
                        ICollection<Htlc>? remoteOfferedHtlcs = null, ICollection<Htlc>? remoteFulfilledHtlcs = null,
                        ICollection<Htlc>? remoteOldHtlcs = null)
    {
        ChannelParams = channelParams;
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

    public void UpdateCommitmentNumber(CommitmentNumber commitmentNumber)
    {
        if (CommitmentNumber is null)
            throw new InvalidOperationException("Commitment number not set");

        if (commitmentNumber.ObscuringFactor != CommitmentNumber.ObscuringFactor)
            throw new InvalidOperationException("Commitment number belongs to a different channel");

        if (commitmentNumber.Value < CommitmentNumber.Value)
            throw new InvalidOperationException("Commitment number cannot go backwards");

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
                                      RemoteKeySet!.FundingCompactPubKey, LocalKeySet.KeyIndex);
    }
}