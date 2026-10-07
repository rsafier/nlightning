namespace NLightning.Infrastructure.RemoteSigning;
/// <summary>Stable v1 wire operations. Never renumber existing operations.</summary>
public static class SignerOperations
{
    public const uint Identity = 0;
    // Added on top of the original v1 surface: never renumber the existing operations.
    public const uint ComputeSilentPaymentOutputs = 62;
    public const uint SignWalletMessage = 63;
    public const uint GetDepositAccount = 108;
    // CreateNewChannel(out ChannelBasepoints basepoints, out CompactPubKey firstPerCommitmentPoint)
    public const uint CreateNewChannel = 1;
    // GetChannelBasepoints(uint channelKeyIndex)
    public const uint GetChannelBasepoints = 2;
    // GetChannelBasepoints(ChannelId channelId)
    public const uint GetChannelBasepoints2 = 3;
    // GetNodePublicKey()
    public const uint GetNodePublicKey = 4;
    // SignNodeMessage(Hash messageHash)
    public const uint SignNodeMessage = 5;
    // VerifyNodeMessage(Hash messageHash, CompactSignature signature, CompactPubKey nodeId)
    public const uint VerifyNodeMessage = 6;
    // SignChannelAnnouncement(ChannelId channelId, ReadOnlyMemory<byte> unsignedAnnouncement, ShortChannelId shortChannelId)
    public const uint SignChannelAnnouncement = 7;
    // CreateChannelAnnouncement2Nonces(ChannelId channelId, Protocol.Payloads.ChannelAnnouncement2Payload unsignedAnnouncement)
    public const uint CreateChannelAnnouncement2Nonces = 8;
    // SignChannelAnnouncement2(ChannelId channelId, Protocol.Payloads.ChannelAnnouncement2Payload unsignedAnnouncement, MusigPublicNonce remoteNodeNonce, MusigPublicNonce remoteBitcoinNonce)
    public const uint SignChannelAnnouncement2 = 9;
    // DiscardChannelAnnouncement2Nonces(ChannelId channelId)
    public const uint DiscardChannelAnnouncement2Nonces = 10;
    // SignNodeMessageBip340(Hash messageHash)
    public const uint SignNodeMessageBip340 = 11;
    // SignLightningMessage(ReadOnlySpan<byte> message, bool singleHash)
    public const uint SignLightningMessage = 12;
    // GetPerCommitmentPoint(uint channelKeyIndex, ulong commitmentNumber)
    public const uint GetPerCommitmentPoint = 13;
    // GetPerCommitmentPoint(ChannelId channelId, ulong commitmentNumber)
    public const uint GetPerCommitmentPoint2 = 14;
    // RegisterChannel(ChannelId channelId, ChannelSigningInfo signingInfo)
    public const uint RegisterChannel = 15;
    // UnregisterChannel(ChannelId channelId)
    public const uint UnregisterChannel = 16;
    // RevealPerCommitmentSecret(ChannelId channelId, ulong commitmentNumber)
    public const uint RevealPerCommitmentSecret = 17;
    // AdvanceLocalCommitment(ChannelId channelId, ulong newLocalCommitmentNumber)
    public const uint AdvanceLocalCommitment = 18;
    // SignRemoteHtlcTransactions(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions)
    public const uint SignRemoteHtlcTransactions = 19;
    // ValidateLocalHtlcSignatures(ChannelId channelId, IReadOnlyList<HtlcSigningContext> htlcTransactions, IReadOnlyList<CompactSignature> signatures)
    public const uint ValidateLocalHtlcSignatures = 20;
    // SignLocalHtlcTransaction(ChannelId channelId, HtlcSigningContext htlcTransaction)
    public const uint SignLocalHtlcTransaction = 21;
    // SignLocalCommitmentForBroadcast(ChannelId channelId, ulong commitmentNumber, SignedTransaction unsignedCommitment, CompactSignature remoteSignature)
    public const uint SignLocalCommitmentForBroadcast = 22;
    // MarkDataLoss(ChannelId channelId)
    public const uint MarkDataLoss = 23;
    // MarkBroadcastSigned(ChannelId channelId, ulong commitmentNumber)
    public const uint MarkBroadcastSigned = 24;
    // TryGetBroadcastSignedCommitment(ChannelId channelId, out ulong commitmentNumber)
    public const uint TryGetBroadcastSignedCommitment = 25;
    // SignSweepInput(ChannelId channelId, SweepSigningContext context)
    public const uint SignSweepInput = 26;
    // SignWalletTransaction(SignedTransaction unsignedTransaction)
    public const uint SignWalletTransaction = 27;
    // SignWalletTransaction(SignedTransaction unsignedTransaction, IReadOnlyList<Wallet.Models.SpentOutput> otherSpentOutputs)
    public const uint SignWalletTransaction2 = 28;
    // SignWalletTransaction(SignedTransaction unsignedTransaction, Guid reservationId, IReadOnlyList<Wallet.Models.SpentOutput> otherSpentOutputs)
    public const uint SignWalletTransaction3 = 29;
    // SignFundingTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    public const uint SignFundingTransaction = 30;
    // SignChannelTransaction(ChannelId channelId, SignedTransaction unsignedTransaction)
    public const uint SignChannelTransaction = 31;
    // ValidateSignature(ChannelId channelId, CompactSignature signature, SignedTransaction unsignedTransaction)
    public const uint ValidateSignature = 32;
    // SignAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex, LightningMoney amount)
    public const uint SignAnchorInput = 33;
    // SignTaprootAnchorInput(ChannelId channelId, SignedTransaction unsignedTransaction, int inputIndex, CompactPubKey? ourPerCommitmentPoint, IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs)
    public const uint SignTaprootAnchorInput = 34;
    // GetLocalVerificationNonce(uint channelKeyIndex, TxId? fundingTxId, ulong localCommitmentNumber)
    public const uint GetLocalVerificationNonce = 35;
    // GetLocalVerificationNonce(ChannelId channelId, TxId? fundingTxId, ulong localCommitmentNumber)
    public const uint GetLocalVerificationNonce2 = 36;
    // SignRemoteCommitmentPartial(ChannelId channelId, TxId? fundingTxId, SignedTransaction unsignedCommitment, MusigPublicNonce remoteVerificationNonce)
    public const uint SignRemoteCommitmentPartial = 37;
    // ValidateLocalCommitmentPartialSignature(ChannelId channelId, TxId? fundingTxId, ulong localCommitmentNumber, MusigPartialSignatureWithNonce remoteSignature, SignedTransaction unsignedCommitment)
    public const uint ValidateLocalCommitmentPartialSignature = 38;
    // SignLocalCommitmentForBroadcast(ChannelId channelId, TxId? fundingTxId, ulong commitmentNumber, SignedTransaction unsignedCommitment, MusigPartialSignatureWithNonce remoteSignature)
    public const uint SignLocalCommitmentForBroadcast2 = 39;
    // CreateClosingNonce(ChannelId channelId)
    public const uint CreateClosingNonce = 40;
    // SignClosingAsCloser(ChannelId channelId, SignedTransaction unsignedClosing, MusigPublicNonce remoteCloseeNonce)
    public const uint SignClosingAsCloser = 41;
    // SignClosingAsClosee(ChannelId channelId, SignedTransaction unsignedClosing, MusigPublicNonce localCloseeNonce, MusigPartialSignatureWithNonce remoteCloserSignature)
    public const uint SignClosingAsClosee = 42;
    // ValidateClosingPartialSignature(ChannelId channelId, SignedTransaction unsignedClosing, MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce, MusigPublicNonce localNonce)
    public const uint ValidateClosingPartialSignature = 43;
    // AggregateClosingSignature(ChannelId channelId, SignedTransaction unsignedClosing, MusigPartialSignature localSignature, MusigPublicNonce localNonce, MusigPartialSignature remoteSignature, MusigPublicNonce remoteNonce)
    public const uint AggregateClosingSignature = 44;
    // ForgetClosingNonces(ChannelId channelId)
    public const uint ForgetClosingNonces = 45;
    // GetLocalVerificationNonce(ChannelId channelId, uint fundingKeyIndex, TxId fundingTxId, ulong localCommitmentNumber)
    public const uint GetLocalVerificationNonce3 = 46;
    // CreateSpliceFundingNonce(ChannelId channelId)
    public const uint CreateSpliceFundingNonce = 47;
    // SignSpliceSharedInputPartial(ChannelId channelId, TxId newFundingTxId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex, IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs, MusigPublicNonce localFundingNonce, MusigPublicNonce remoteFundingNonce)
    public const uint SignSpliceSharedInputPartial = 48;
    // AggregateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex, IReadOnlyList<Wallet.Models.SpentOutput> spentOutputs, MusigPartialSignatureWithNonce localSignature, MusigPartialSignatureWithNonce remoteSignature)
    public const uint AggregateSpliceSharedInputSignature = 49;
    // GetFundingPubKey(ChannelId channelId, uint fundingKeyIndex)
    public const uint GetFundingPubKey = 50;
    // RegisterFunding(ChannelId channelId, Channels.Splicing.ChannelFunding funding)
    public const uint RegisterFunding = 51;
    // MarkSpliceCommitmentPersisted(ChannelId channelId, TxId fundingTxId, ulong localCommitmentNumber)
    public const uint MarkSpliceCommitmentPersisted = 52;
    // SignSpliceSharedInput(ChannelId channelId, TxId newFundingTxId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex)
    public const uint SignSpliceSharedInput = 53;
    // ValidateSpliceSharedInputSignature(ChannelId channelId, SignedTransaction unsignedSpliceTransaction, int sharedInputIndex, CompactSignature remoteSignature)
    public const uint ValidateSpliceSharedInputSignature = 54;
    // SignChannelTransaction(ChannelId channelId, TxId fundingTxId, SignedTransaction unsignedTransaction)
    public const uint SignChannelTransaction2 = 55;
    // ValidateSignature(ChannelId channelId, TxId fundingTxId, CompactSignature signature, SignedTransaction unsignedTransaction)
    public const uint ValidateSignature2 = 56;
    // SignLocalCommitmentForBroadcast(ChannelId channelId, TxId fundingTxId, ulong commitmentNumber, SignedTransaction unsignedCommitment, CompactSignature remoteSignature)
    public const uint SignLocalCommitmentForBroadcast3 = 57;
    // LockFunding(ChannelId channelId, TxId fundingTxId)
    public const uint LockFunding = 58;
    // LockFunding(ChannelId channelId, TxId fundingTxId, ShortChannelId? shortChannelId)
    public const uint LockFunding2 = 59;
    // GetBolt12PayerId(ReadOnlyMemory<byte> invoiceRequestMetadata)
    public const uint GetBolt12PayerId = 60;
    // SignBolt12(Offers.Models.Bolt12SigningKey key, string tag, Hash merkleRoot)
    public const uint SignBolt12 = 61;
    public const uint ComputeNodeSharedSecret = 100;
    public const uint SignBolt11Invoice = 101;
    public const uint GetWalletPublicKey = 102;
    public const uint EncryptNodeData = 103;
    public const uint DecryptNodeData = 104;
    public const uint ComputeOfferPathId = 105;
    public const uint ReserveChannelKeyIndex = 106;
    public const uint EnsureLastUsedChannelIndexAtLeast = 107;
    public static int ArgumentCount(uint operation) => operation switch
    {
        Identity => 0,
        ComputeSilentPaymentOutputs => 4,
        SignWalletMessage => 2,
        GetDepositAccount => 1,
        CreateNewChannel => 0,
        GetChannelBasepoints => 1,
        GetChannelBasepoints2 => 1,
        GetNodePublicKey => 0,
        SignNodeMessage => 1,
        VerifyNodeMessage => 3,
        SignChannelAnnouncement => 3,
        CreateChannelAnnouncement2Nonces => 2,
        SignChannelAnnouncement2 => 4,
        DiscardChannelAnnouncement2Nonces => 1,
        SignNodeMessageBip340 => 1,
        SignLightningMessage => 2,
        GetPerCommitmentPoint => 2,
        GetPerCommitmentPoint2 => 2,
        RegisterChannel => 2,
        UnregisterChannel => 1,
        RevealPerCommitmentSecret => 2,
        AdvanceLocalCommitment => 2,
        SignRemoteHtlcTransactions => 2,
        ValidateLocalHtlcSignatures => 3,
        SignLocalHtlcTransaction => 2,
        SignLocalCommitmentForBroadcast => 4,
        MarkDataLoss => 1,
        MarkBroadcastSigned => 2,
        TryGetBroadcastSignedCommitment => 1,
        SignSweepInput => 2,
        SignWalletTransaction => 2,
        SignWalletTransaction2 => 3,
        SignWalletTransaction3 => 4,
        SignFundingTransaction => 3,
        SignChannelTransaction => 2,
        ValidateSignature => 3,
        SignAnchorInput => 4,
        SignTaprootAnchorInput => 5,
        GetLocalVerificationNonce => 3,
        GetLocalVerificationNonce2 => 3,
        SignRemoteCommitmentPartial => 4,
        ValidateLocalCommitmentPartialSignature => 5,
        SignLocalCommitmentForBroadcast2 => 5,
        CreateClosingNonce => 1,
        SignClosingAsCloser => 3,
        SignClosingAsClosee => 4,
        ValidateClosingPartialSignature => 5,
        AggregateClosingSignature => 6,
        ForgetClosingNonces => 1,
        GetLocalVerificationNonce3 => 4,
        CreateSpliceFundingNonce => 1,
        SignSpliceSharedInputPartial => 7,
        AggregateSpliceSharedInputSignature => 6,
        GetFundingPubKey => 2,
        RegisterFunding => 2,
        MarkSpliceCommitmentPersisted => 3,
        SignSpliceSharedInput => 4,
        ValidateSpliceSharedInputSignature => 4,
        SignChannelTransaction2 => 3,
        ValidateSignature2 => 4,
        SignLocalCommitmentForBroadcast3 => 5,
        LockFunding => 2,
        LockFunding2 => 3,
        GetBolt12PayerId => 1,
        SignBolt12 => 3,
        ComputeNodeSharedSecret => 1,
        SignBolt11Invoice => 2,
        GetWalletPublicKey => 3,
        EncryptNodeData => 4,
        DecryptNodeData => 4,
        ComputeOfferPathId => 1,
        ReserveChannelKeyIndex => 0,
        EnsureLastUsedChannelIndexAtLeast => 1,
        _ => throw new ArgumentException("Unknown signer operation."),
    };
}