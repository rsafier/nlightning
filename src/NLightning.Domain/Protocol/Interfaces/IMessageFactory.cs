namespace NLightning.Domain.Protocol.Interfaces;

using Bitcoin.ValueObjects;
using Channels.ValueObjects;
using Crypto.ValueObjects;
using Gossip.Addresses;
using LiquidityAds.Models;
using Messages;
using Models;
using Money;
using Tlv;

public interface IMessageFactory
{
    /// <summary>
    /// Creates an init message. <paramref name="remoteAddress"/> is the BOLT 1 <c>remote_addr</c> TLV: the address
    /// descriptor of the connection's remote endpoint, which the receiver of an IP connection SHOULD send (NL-009);
    /// null sends no <c>remote_addr</c>. When we sell liquidity (<c>Node:LiquidityAds:FundingRates</c>, NL-850) our
    /// <c>option_will_fund</c> rates go with it.
    /// </summary>
    InitMessage CreateInitMessage(AddressDescriptor? remoteAddress = null);
    WarningMessage CreateWarningMessage(string message, ChannelId? channelId);
    WarningMessage CreateWarningMessage(byte[] data, ChannelId? channelId);
    StfuMessage CreateStfuMessage(ChannelId channelId, bool initiator);
    ErrorMessage CreateErrorMessage(string message, ChannelId? channelId);
    ErrorMessage CreateErrorMessage(byte[] data, ChannelId? channelId);
    PingMessage CreatePingMessage();
    PongMessage CreatePongMessage(IMessage pingMessage);

    TxAddInputMessage CreateTxAddInputMessage(ChannelId channelId, ulong serialId, byte[] prevTx, uint prevTxVout,
                                              uint sequence);

    TxAddOutputMessage CreateTxAddOutputMessage(ChannelId channelId, ulong serialId, LightningMoney amount,
                                                BitcoinScript script);

    TxRemoveInputMessage CreateTxRemoveInputMessage(ChannelId channelId, ulong serialId);
    TxRemoveOutputMessage CreateTxRemoveOutputMessage(ChannelId channelId, ulong serialId);
    TxCompleteMessage CreateTxCompleteMessage(ChannelId channelId);
    TxSignaturesMessage CreateTxSignaturesMessage(ChannelId channelId, byte[] txId, List<Witness> witnesses);

    /// <summary>A <c>tx_init_rbf</c>; <paramref name="requestFunding"/> is our liquidity ads request (NL-850), null
    /// for none.</summary>
    TxInitRbfMessage CreateTxInitRbfMessage(ChannelId channelId, uint locktime, uint feerate,
                                            long fundingOutputContrubution,
                                            bool requireConfirmedInputs, RequestFunding? requestFunding = null);

    /// <summary>A <c>tx_ack_rbf</c>; <paramref name="willFund"/> is our liquidity ads answer (NL-850), null for
    /// none.</summary>
    TxAckRbfMessage CreateTxAckRbfMessage(ChannelId channelId, long fundingOutputContrubution,
                                          bool requireConfirmedInputs, WillFund? willFund = null);

    TxAbortMessage CreateTxAbortMessage(ChannelId channelId, byte[] data);

    ChannelReadyMessage CreateChannelReadyMessage(ChannelId channelId, CompactPubKey secondPerCommitmentPoint,
                                                  ShortChannelId? shortChannelId = null);

    ShutdownMessage CreateShutdownMessage(ChannelId channelId, BitcoinScript scriptPubkey);

    /// <summary>
    /// Creates an open_channel whose dust limit, reserve, htlc minimum, max accepted HTLCs, max in flight and
    /// to_self_delay are taken from <paramref name="localParams"/> (the values we announce).
    /// </summary>
    OpenChannel1Message CreateOpenChannel1Message(ChannelId temporaryChannelId, LightningMoney fundingAmount,
                                                  CompactPubKey fundingPubKey, LightningMoney pushAmount,
                                                  ChannelParty localParams, LightningMoney feeRatePerKw,
                                                  CompactPubKey revocationBasepoint,
                                                  CompactPubKey paymentBasepoint, CompactPubKey delayedPaymentBasepoint,
                                                  CompactPubKey htlcBasepoint, CompactPubKey firstPerCommitmentPoint,
                                                  ChannelFlags channelFlags,
                                                  ChannelTypeTlv channelTypeTlv,
                                                  UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv);

    /// <summary>
    /// Creates an <c>open_channel2</c> (BOLT 2 "Channel Establishment v2") announcing <paramref name="localParams"/>
    /// (dust limit, htlc minimum, max accepted HTLCs, max in flight, to_self_delay; v2 has no reserve field).
    /// </summary>
    OpenChannel2Message CreateOpenChannel2Message(ChannelId temporaryChannelId, uint fundingFeeRatePerKw,
                                                  uint commitmentFeeRatePerKw, LightningMoney fundingAmount,
                                                  ChannelParty localParams, uint locktime,
                                                  CompactPubKey fundingPubKey, CompactPubKey revocationBasepoint,
                                                  CompactPubKey paymentBasepoint,
                                                  CompactPubKey delayedPaymentBasepoint, CompactPubKey htlcBasepoint,
                                                  CompactPubKey firstPerCommitmentPoint,
                                                  CompactPubKey secondPerCommitmentPoint, ChannelFlags channelFlags,
                                                  ChannelTypeTlv channelTypeTlv,
                                                  UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv = null,
                                                  bool requireConfirmedInputs = false,
                                                  RequestFunding? requestFunding = null);

    /// <summary>
    /// Creates an accept_channel whose dust limit, reserve, htlc minimum, max accepted HTLCs, max in flight and
    /// to_self_delay are taken from <paramref name="localParams"/> (the values we announce, never the opener's).
    /// </summary>
    AcceptChannel1Message CreateAcceptChannel1Message(ChannelParty localParams, ChannelTypeTlv channelTypeTlv,
                                                      CompactPubKey delayedPaymentBasepoint,
                                                      CompactPubKey firstPerCommitmentPoint,
                                                      CompactPubKey fundingPubKey, CompactPubKey htlcBasepoint,
                                                      uint minimumDepth, CompactPubKey paymentBasepoint,
                                                      CompactPubKey revocationBasepoint, ChannelId temporaryChannelId,
                                                      UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv);

    /// <summary>
    /// Creates an <c>accept_channel2</c> echoing the opener's <paramref name="temporaryChannelId"/> and
    /// <paramref name="channelTypeTlv"/>, with <paramref name="fundingAmount"/> as our contribution (zero for none) and
    /// the values of <paramref name="localParams"/>.
    /// </summary>
    AcceptChannel2Message CreateAcceptChannel2Message(ChannelId temporaryChannelId, LightningMoney fundingAmount,
                                                      ChannelParty localParams, uint minimumDepth,
                                                      CompactPubKey fundingPubKey, CompactPubKey revocationBasepoint,
                                                      CompactPubKey paymentBasepoint,
                                                      CompactPubKey delayedPaymentBasepoint,
                                                      CompactPubKey htlcBasepoint,
                                                      CompactPubKey firstPerCommitmentPoint,
                                                      CompactPubKey secondPerCommitmentPoint,
                                                      ChannelTypeTlv channelTypeTlv,
                                                      UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv = null,
                                                      bool requireConfirmedInputs = false,
                                                      WillFund? willFund = null);

    FundingCreatedMessage CreateFundingCreatedMessage(ChannelId temporaryChannelId, TxId fundingTxId,
                                                      ushort fundingOutputIndex, CompactSignature signature);

    FundingSignedMessage CreateFundingSignedMessage(ChannelId channelId, CompactSignature signature);

    UpdateAddHtlcMessage CreateUpdateAddHtlcMessage(ChannelId channelId, ulong id, ulong amountMsat,
                                                    ReadOnlyMemory<byte> paymentHash, uint cltvExpiry,
                                                    ReadOnlyMemory<byte> onionRoutingPacket);

    /// <summary>
    /// An <c>update_fulfill_htlc</c>, with the <c>attribution_data</c> TLV (1) when <paramref name="attributionData"/>
    /// is not empty and the <c>fulfillment_payload</c> TLV (3) when <paramref name="fulfillmentPayload"/> is not empty.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="attributionData"/> is neither empty nor 920 bytes.</exception>
    UpdateFulfillHtlcMessage CreateUpdateFulfillHtlcMessage(ChannelId channelId, ulong id,
                                                            ReadOnlyMemory<byte> preimage,
                                                            ReadOnlyMemory<byte> attributionData = default,
                                                            ReadOnlyMemory<byte> fulfillmentPayload = default);

    /// <summary>
    /// An <c>update_fail_htlc</c>, with the <c>attribution_data</c> TLV (1) when <paramref name="attributionData"/> is
    /// not empty.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="attributionData"/> is neither empty nor 920 bytes.</exception>
    UpdateFailHtlcMessage CreateUpdateFailHtlcMessage(ChannelId channelId, ulong id, ReadOnlyMemory<byte> reason,
                                                      ReadOnlyMemory<byte> attributionData = default);

    CommitmentSignedMessage CreateCommitmentSignedMessage(ChannelId channelId, CompactSignature signature,
                                                          IEnumerable<CompactSignature> htlcSignatures,
                                                          TxId fundingTxId);

    RevokeAndAckMessage CreateRevokeAndAckMessage(ChannelId channelId, ReadOnlyMemory<byte> perCommitmentSecret,
                                                  CompactPubKey nextPerCommitmentPoint);

    UpdateFeeMessage CreateUpdateFeeMessage(ChannelId channelId, uint feeratePerKw);

    UpdateFailMalformedHtlcMessage CreateUpdateFailMalformedHtlcMessage(ChannelId channelId, ulong id,
                                                                        ReadOnlyMemory<byte> sha256OfOnion,
                                                                        ushort failureCode);

    ChannelReestablishMessage CreateChannelReestablishMessage(ChannelId channelId, ulong nextCommitmentNumber,
                                                              ulong nextRevocationNumber,
                                                              ReadOnlyMemory<byte> yourLastPerCommitmentSecret,
                                                              CompactPubKey myCurrentPerCommitmentPoint);

    /// <summary>
    /// An <c>announcement_signatures</c> (BOLT 7, type 259) for <paramref name="channelId"/>: our node-key and
    /// funding-key signatures of the channel's <c>channel_announcement</c> hash.
    /// </summary>
    /// <exception cref="ArgumentException">A signature is not 64 bytes.</exception>
    AnnouncementSignaturesMessage CreateAnnouncementSignaturesMessage(ChannelId channelId,
                                                                      ShortChannelId shortChannelId,
                                                                      CompactSignature nodeSignature,
                                                                      CompactSignature bitcoinSignature);

    /// <summary>A <c>splice_init</c> (BOLT 2, type 80, SP-W-01).</summary>
    /// <param name="channelId">The channel.</param>
    /// <param name="fundingContributionSatoshis">Our signed contribution (negative for a splice-out).</param>
    /// <param name="fundingFeeratePerKw">The splice transaction's feerate.</param>
    /// <param name="locktime">The splice transaction's <c>nLockTime</c>.</param>
    /// <param name="fundingPubKey">Our funding key for the new funding.</param>
    /// <param name="requireConfirmedInputs">Set <c>require_confirmed_inputs</c> (TLV 2).</param>
    /// <param name="requestFunding">Our liquidity ads request (NL-850), null for none.</param>
    SpliceInitMessage CreateSpliceInitMessage(ChannelId channelId, long fundingContributionSatoshis,
                                              uint fundingFeeratePerKw, uint locktime, CompactPubKey fundingPubKey,
                                              bool requireConfirmedInputs = false,
                                              RequestFunding? requestFunding = null);

    /// <summary>A <c>splice_ack</c> (BOLT 2, type 81, SP-W-02); <paramref name="willFund"/> is our liquidity ads
    /// answer (NL-850), null for none.</summary>
    SpliceAckMessage CreateSpliceAckMessage(ChannelId channelId, long fundingContributionSatoshis,
                                            CompactPubKey fundingPubKey, bool requireConfirmedInputs = false,
                                            WillFund? willFund = null);

    /// <summary>A <c>splice_locked</c> (BOLT 2, type 77, SP-LK-01).</summary>
    SpliceLockedMessage CreateSpliceLockedMessage(ChannelId channelId, TxId spliceTxId);

    /// <summary>
    /// A <c>start_batch</c> (BOLT 2, type 127) announcing <paramref name="batchSize"/> <c>commitment_signed</c>
    /// messages (<c>message_type</c> = 132, SP-OP-03).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is not in 2..20.</exception>
    StartBatchMessage CreateStartBatchMessage(ChannelId channelId, ushort batchSize);

    #region Simple taproot channels (option_simple_taproot, BOLTs PR #1324; NL-877)

    /// <summary>
    /// Creates an open_channel for a simple taproot channel: as the overload without a nonce, plus
    /// <c>next_local_nonce</c> (TLV 4), our verification nonce for the first commitment the peer signs for us.
    /// </summary>
    OpenChannel1Message CreateOpenChannel1Message(ChannelId temporaryChannelId, LightningMoney fundingAmount,
                                                  CompactPubKey fundingPubKey, LightningMoney pushAmount,
                                                  ChannelParty localParams, LightningMoney feeRatePerKw,
                                                  CompactPubKey revocationBasepoint,
                                                  CompactPubKey paymentBasepoint, CompactPubKey delayedPaymentBasepoint,
                                                  CompactPubKey htlcBasepoint, CompactPubKey firstPerCommitmentPoint,
                                                  ChannelFlags channelFlags,
                                                  ChannelTypeTlv channelTypeTlv,
                                                  UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv,
                                                  MusigPublicNonce nextLocalNonce);

    /// <summary>
    /// Creates an accept_channel for a simple taproot channel: as the overload without a nonce, plus
    /// <c>next_local_nonce</c> (TLV 4).
    /// </summary>
    AcceptChannel1Message CreateAcceptChannel1Message(ChannelParty localParams, ChannelTypeTlv channelTypeTlv,
                                                      CompactPubKey delayedPaymentBasepoint,
                                                      CompactPubKey firstPerCommitmentPoint,
                                                      CompactPubKey fundingPubKey, CompactPubKey htlcBasepoint,
                                                      uint minimumDepth, CompactPubKey paymentBasepoint,
                                                      CompactPubKey revocationBasepoint, ChannelId temporaryChannelId,
                                                      UpfrontShutdownScriptTlv? upfrontShutdownScriptTlv,
                                                      MusigPublicNonce nextLocalNonce);

    /// <summary>
    /// Creates a channel_ready for a simple taproot channel, with <c>next_local_nonce</c> (TLV 4).
    /// </summary>
    ChannelReadyMessage CreateChannelReadyMessage(ChannelId channelId, CompactPubKey secondPerCommitmentPoint,
                                                  ShortChannelId? shortChannelId, MusigPublicNonce nextLocalNonce);

    /// <summary>
    /// Creates a funding_created for a simple taproot channel: the 64-byte <c>signature</c> is all zeros
    /// (<see cref="CompactSignature.Zero"/>) and the partial signature goes in <c>partial_signature_with_nonce</c>
    /// (TLV 2).
    /// </summary>
    FundingCreatedMessage CreateFundingCreatedMessage(ChannelId temporaryChannelId, TxId fundingTxId,
                                                      ushort fundingOutputIndex,
                                                      MusigPartialSignatureWithNonce partialSignatureWithNonce);

    /// <summary>
    /// Creates a funding_signed for a simple taproot channel (zero <c>signature</c>, TLV 2).
    /// </summary>
    FundingSignedMessage CreateFundingSignedMessage(ChannelId channelId,
                                                    MusigPartialSignatureWithNonce partialSignatureWithNonce);

    /// <summary>
    /// Creates a commitment_signed for a simple taproot channel: zero <c>signature</c>, the partial signature in TLV 2,
    /// <c>funding_txid</c> (TLV 1) as always; the HTLC signatures are 64-byte schnorr signatures.
    /// </summary>
    CommitmentSignedMessage CreateCommitmentSignedMessage(ChannelId channelId,
                                                          MusigPartialSignatureWithNonce partialSignatureWithNonce,
                                                          IEnumerable<CompactSignature> htlcSignatures,
                                                          TxId fundingTxId);

    /// <summary>
    /// Creates a revoke_and_ack for a simple taproot channel, with <c>next_local_nonces</c> (TLV 22): one nonce per
    /// active funding.
    /// </summary>
    RevokeAndAckMessage CreateRevokeAndAckMessage(ChannelId channelId, ReadOnlyMemory<byte> perCommitmentSecret,
                                                  CompactPubKey nextPerCommitmentPoint, FundingNonces nextLocalNonces);

    /// <summary>
    /// Creates a channel_reestablish with every optional TLV: <c>next_funding</c> (1), <c>my_current_funding_locked</c>
    /// (5), the simple taproot <c>next_local_nonces</c> (22) and BOLTs PR #1324 <c>current_commit_nonce</c> (24); a
    /// null argument leaves its TLV out.
    /// </summary>
    ChannelReestablishMessage CreateChannelReestablishMessage(ChannelId channelId, ulong nextCommitmentNumber,
                                                              ulong nextRevocationNumber,
                                                              ReadOnlyMemory<byte> yourLastPerCommitmentSecret,
                                                              CompactPubKey myCurrentPerCommitmentPoint,
                                                              FundingNonces? nextLocalNonces,
                                                              MusigPublicNonce? currentCommitNonce = null,
                                                              NextFundingTlv? nextFundingTlv = null,
                                                              MyCurrentFundingLockedTlv? myCurrentFundingLockedTlv =
                                                                  null);

    /// <summary>
    /// Creates a shutdown for a simple taproot channel, with <c>shutdown_nonce</c> (TLV 8): our closee nonce.
    /// </summary>
    ShutdownMessage CreateShutdownMessage(ChannelId channelId, BitcoinScript scriptPubkey,
                                          MusigPublicNonce shutdownNonce);

    /// <summary>
    /// Creates a tx_complete of a simple taproot interactive-tx session (BOLTs PR #1324): <c>commit_nonces</c> (TLV 4)
    /// and, for a splice of a taproot channel, <c>funding_nonce</c> (TLV 6).
    /// </summary>
    TxCompleteMessage CreateTxCompleteMessage(ChannelId channelId, MusigPublicNonce commitNonce,
                                              MusigPublicNonce nextCommitNonce, MusigPublicNonce? fundingNonce = null);

    /// <summary>
    /// Creates a tx_signatures with <c>shared_input_partial_signature</c> (TLV 2, BOLTs PR #1324): our partial signature
    /// with nonce of the shared taproot input of a splice.
    /// </summary>
    TxSignaturesMessage CreateTxSignaturesMessage(ChannelId channelId, byte[] txId, List<Witness> witnesses,
                                                  MusigPartialSignatureWithNonce sharedInputPartialSignature);

    #endregion
}