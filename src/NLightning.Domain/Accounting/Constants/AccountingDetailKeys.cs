namespace NLightning.Domain.Accounting.Constants;

/// <summary>
/// The detail keys and values of the accounting feed that the books read (plan <c>docs/agents/ACCOUNTING_PLAN.md</c>
/// §6.1). The writers live in the Application and Infrastructure layers; they use these names so the posting rules
/// (<see cref="Books.AccountingPostingRules"/>) and the writers cannot drift apart.
/// </summary>
public static class AccountingDetailKeys
{
    #region Common

    /// <summary>The bucket value moved out of (<see cref="ChannelBucket"/>, <see cref="WalletBucket"/>,
    /// <see cref="PendingBucket"/>).</summary>
    public const string BucketFrom = "bucketFrom";

    /// <summary>The bucket value moved into.</summary>
    public const string BucketTo = "bucketTo";

    /// <summary>The bucket an event is about (an opening balance, the write-off of an HTLC claimed by the peer).</summary>
    public const string Bucket = "bucket";

    /// <summary>A free-text description (an invoice's or a payment's).</summary>
    public const string Description = "description";

    /// <summary>The sub-kind of an event (an invoice or payment kind, a funding kind).</summary>
    public const string Kind = "kind";

    /// <summary>"true" on an event that is statistics only: the books post nothing for it.</summary>
    public const string Memo = "memo";

    /// <summary>Why a value could not be told, or what a writer wants the reader to know.</summary>
    public const string Note = "note";

    /// <summary>The value of a boolean detail that is set.</summary>
    public const string True = "true";

    /// <summary>The channel bucket (our gross local balance of the channels).</summary>
    public const string ChannelBucket = "channel";

    /// <summary>The on-chain wallet bucket.</summary>
    public const string WalletBucket = "wallet";

    /// <summary>The bucket of force-closed funds not in the wallet yet.</summary>
    public const string PendingBucket = "onchain-pending";

    #endregion

    #region Labels and tags at the source (A3-T1)

    /// <summary>The operator's label of the row the event comes from (an invoice, its offer, a payment, a channel, a
    /// withdrawal); see <c>Labels.SourceLabels</c>.</summary>
    public const string Label = "label";

    /// <summary>The prefix of an operator tag: the detail <c>tag.&lt;key&gt;</c> holds the tag's value.</summary>
    public const string TagPrefix = "tag.";

    #endregion

    #region Payments

    /// <summary>"true" on a payment that paid an invoice of ours (a rebalance).</summary>
    public const string SelfPayment = "selfPayment";

    /// <summary>The failure reason of a payment that failed.</summary>
    public const string Reason = "reason";

    /// <summary>The incoming channel of a forward.</summary>
    public const string IncomingScid = "incomingScid";

    /// <summary>The outgoing channel of a forward.</summary>
    public const string OutgoingScid = "outgoingScid";

    #endregion

    #region Channels

    /// <summary>"true" on a dual-funded open.</summary>
    public const string DualFunded = "dualFunded";

    #endregion

    #region Liquidity ads (NL-850)

    /// <summary>
    /// The liquidity fee (msat, signed: positive when we paid it, negative when we earned it) that the balance of a
    /// funding or splice includes: on <see cref="Enums.AccountingEventKind.ChannelFunded"/> and
    /// <see cref="Enums.AccountingEventKind.SpliceLocked"/>, whose amounts leave it out (the liquidity event books it).
    /// </summary>
    public const string LiquidityFeeMsat = "liquidityFeeMsat";

    /// <summary>Our side of a liquidity purchase: <see cref="LiquidityBuyer"/> or <see cref="LiquiditySeller"/>.</summary>
    public const string LiquidityRole = "role";

    public const string LiquidityBuyer = "buyer";
    public const string LiquiditySeller = "seller";

    /// <summary>The channel of a liquidity purchase (also the event's channel id).</summary>
    public const string PurchaseChannelId = "channelId";

    /// <summary>The funding (or splice) transaction a liquidity purchase was made in (also the event's txid).</summary>
    public const string PurchaseFundingTxId = "fundingTxId";

    /// <summary>The other node of a liquidity purchase (also the event's counterparty).</summary>
    public const string PurchasePeer = "peer";

    /// <summary>The amount the buyer requested, in satoshis.</summary>
    public const string RequestedSat = "requestedSat";

    /// <summary>The amount the seller contributed, in satoshis.</summary>
    public const string ContributedSat = "contributedSat";

    /// <summary>The mining fee part of a liquidity fee (the seller's on-chain weight refunded), in msat.</summary>
    public const string MiningFeeMsat = "miningFeeMsat";

    /// <summary>The service fee part of a liquidity fee (the seller's own fee), in msat.</summary>
    public const string ServiceFeeMsat = "serviceFeeMsat";

    /// <summary>The <see cref="Kind"/> of a liquidity purchase made at a dual-funded open.</summary>
    public const string LiquidityKindOpen = "open";

    /// <summary>The <see cref="Kind"/> of a liquidity purchase made at an RBF of a dual-funded open.</summary>
    public const string LiquidityKindRbf = "rbf";

    /// <summary>The <see cref="Kind"/> of a liquidity purchase made at a splice.</summary>
    public const string LiquidityKindSplice = "splice";

    /// <summary>On the <see cref="Enums.AccountingEventKind.Reversal"/> of a liquidity purchase whose attempt an RBF
    /// replaced: the funding transaction of the attempt that replaced it, when known.</summary>
    public const string ReplacedBy = "replacedBy";

    #endregion

    #region Force close and on-chain resolution

    /// <summary>What a force close is (<c>LocalCommitment</c>, <c>RemoteCommitment</c>, ...).</summary>
    public const string CloseKind = "closeKind";

    /// <summary>A close's counted outputs (what entered the pending bucket), in msat.</summary>
    public const string PendingMsat = "pendingMsat";

    /// <summary>A close's balance that is neither pending nor fee (trimmed HTLCs, dust, rounding), in msat; negative when
    /// the commitment pays more than the balance that stood in for it.</summary>
    public const string LostMsat = "lostMsat";

    /// <summary>"true" on a close written by the backfill as part of the opening balances: it posts nothing.</summary>
    public const string OpeningBalance = "openingBalance";

    /// <summary>The output descriptor of a resolution.</summary>
    public const string Descriptor = "descriptor";

    /// <summary>Who resolved an output (<see cref="ResolvedByUs"/>, <see cref="ResolvedByPeer"/>,
    /// <see cref="ResolvedByIgnored"/>).</summary>
    public const string ResolvedBy = "resolvedBy";

    public const string ResolvedByUs = "us";
    public const string ResolvedByPeer = "peer";
    public const string ResolvedByIgnored = "ignored";

    /// <summary>What left the pending bucket, in msat.</summary>
    public const string PendingOutMsat = "pendingOutMsat";

    /// <summary>What entered the pending bucket (a second-level output), in msat.</summary>
    public const string PendingInMsat = "pendingInMsat";

    /// <summary>What reached our wallet, in msat.</summary>
    public const string WalletMsat = "walletMsat";

    /// <summary>
    /// The part of a resolution's fee that wallet inputs of the spender paid, in msat (NL-748: our anchors HTLC
    /// transaction with wallet fee inputs); included in the event's fee and posted against the clearing account, where
    /// the wallet events book those inputs and the change.
    /// </summary>
    public const string WalletFeeMsat = "walletFeeMsat";

    /// <summary>An output's value, in msat.</summary>
    public const string ValueMsat = "valueMsat";

    /// <summary>"true" when the output is in the pending bucket.</summary>
    public const string Counted = "counted";

    /// <summary>The off-chain event that already booked an HTLC output's value (<c>invoice</c>, <c>forward</c>,
    /// <c>payment</c>).</summary>
    public const string ValueBookedBy = "valueBookedBy";

    /// <summary>Who claimed an HTLC output.</summary>
    public const string ClaimedBy = "claimedBy";

    /// <summary>How the peer took one of our offered HTLC outputs, read from the spender's witness (NL-612):
    /// <see cref="ClaimPathPreimage"/>, <see cref="ClaimPathRevocation"/> or <see cref="ClaimPathUnknown"/>. Only a
    /// preimage claim leaves the value to the payment or forward that booked it.</summary>
    public const string ClaimPath = "claimPath";

    /// <summary>The peer's spend carries the HTLC's payment preimage.</summary>
    public const string ClaimPathPreimage = "preimage";

    /// <summary>The peer's spend takes the revocation path (our own revoked commitment's output).</summary>
    public const string ClaimPathRevocation = "revocation";

    /// <summary>The peer's spend carries no preimage of the HTLC and is no revocation spend we recognize.</summary>
    public const string ClaimPathUnknown = "unknown";

    /// <summary>"true" when a resolution's fee includes the fee bumps of its RBF replacements.</summary>
    public const string IncludesFeeBump = "includesFeeBump";

    /// <summary>The HTLC direction of an output (<c>offered</c>, <c>incoming</c>).</summary>
    public const string HtlcDirection = "htlcDirection";

    /// <summary>The commitment transaction of the close a resolution belongs to.</summary>
    public const string CloseTxId = "closeTxId";

    /// <summary>
    /// The note of a resolution whose spender also spends inputs that are not outputs of the channel (our anchor CPFP
    /// child): the output's value went into a transaction the wallet events book.
    /// </summary>
    public const string MergedNote = "merged with inputs that are not outputs of the channel";

    /// <summary>
    /// The note of a resolution by our stored sweep that also spent inputs that are not rows of the channel (the peer's
    /// anchor in our anchor sweep, NL-611): this event books their value (as a gain) and their part of the fee.
    /// </summary>
    public const string ExternalInputsNote = "also books the sweep's inputs that are not outputs of the channel";

    #endregion

    #region Wallet

    /// <summary>Where a wallet movement's transaction comes from (<see cref="ExternalSource"/>,
    /// <see cref="BroadcastSource"/>, <see cref="ChannelSource"/>, <see cref="WalletSource"/>).</summary>
    public const string Source = "source";

    /// <summary>A transaction we neither broadcast nor watch nor signed: an external deposit or withdrawal.</summary>
    public const string ExternalSource = "external";

    /// <summary>One of our stored broadcasts.</summary>
    public const string BroadcastSource = "broadcast";

    /// <summary>A watched channel transaction, or one that spends a watched channel output.</summary>
    public const string ChannelSource = "channel";

    /// <summary>A transaction that spends our wallet outputs but is not a stored broadcast.</summary>
    public const string WalletSource = "wallet";

    /// <summary>A stored broadcast's purpose.</summary>
    public const string Purpose = "purpose";

    /// <summary>A withdrawal's single external destination.</summary>
    public const string Destination = "destination";

    #endregion

    #region Opening balances (backfill)

    /// <summary>The key prefix of an opening balance (<see cref="AccountingEventKeys.OpeningBalance"/>).</summary>
    public const string OpeningKeyPrefix = "open:";

    /// <summary>The bucket of a channel's opening balance (<c>open:channel:&lt;channel id&gt;</c>).</summary>
    public const string OpeningChannelBucket = "channel:";

    /// <summary>The bucket of the wallet's opening balance (<c>open:wallet</c>).</summary>
    public const string OpeningWalletBucket = "wallet";

    /// <summary>The bucket of a close's pending opening balance (<c>open:pending:&lt;close&gt;</c>).</summary>
    public const string OpeningPendingBucket = "pending:";

    /// <summary>The bucket of the cutover marker (<c>open:cutover</c>): it posts nothing.</summary>
    public const string OpeningCutoverBucket = "cutover";

    /// <summary>The bucket prefix of memo rows (<c>open:memo:...</c>): they post nothing.</summary>
    public const string OpeningMemoBucket = "memo:";

    #endregion
}