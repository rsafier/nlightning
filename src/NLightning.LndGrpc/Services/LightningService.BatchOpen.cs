using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Domain.Client.Requests;
using Domain.Client.Responses;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>BatchOpenChannel</c> (NL-1241; Lightning Terminal opens every channel with it): LND funds every channel of
    /// the batch from one transaction; this node funds one channel per transaction, so a batch of exactly one channel
    /// is opened (the same open as <c>OpenChannelSync</c>, see <see cref="ToOpenRequest"/>; <c>sat_per_vbyte</c> and
    /// <c>label</c> from the batch, the channel's <c>memo</c> before the label) and a larger batch is refused before
    /// anything is funded. As LND, the call answers once the funding transaction is published, with one
    /// <c>PendingUpdate</c> per channel. <c>use_base_fee</c>/<c>use_fee_rate</c> set the channel's routing policy
    /// (<c>base_fee</c>, <c>fee_rate</c> in ppm) right after the funding is published, as the node's
    /// <c>setchannelpolicy</c>; a refusal there is logged and leaves the node-wide policy (the open is not undone).
    /// Refused: <c>pending_chan_id</c> (PSBT funding), <c>spend_unconfirmed</c>, a coin selection strategy, and the
    /// per-channel fields <see cref="ToOpenRequest"/> refuses. <c>target_conf</c> and <c>min_confs</c> are ignored as in
    /// <c>OpenChannelSync</c>.
    /// </summary>
    public override async Task<BatchOpenChannelResponse> BatchOpenChannel(BatchOpenChannelRequest request,
                                                                          ServerCallContext context)
    {
        if (request.Channels.Count == 0)
            throw InvalidArgument("at least one channel must be specified");
        if (request.Channels.Count > 1)
            throw Unimplemented("this node funds one channel per transaction; send one channel per batch");
        if (request.SpendUnconfirmed
         || request.CoinSelectionStrategy != CoinSelectionStrategy.StrategyUseGlobalConfig)
            throw Unimplemented("spend_unconfirmed and coin_selection_strategy are not supported");

        if (request.SatPerVbyte < 0)
            throw InvalidArgument("sat_per_vbyte cannot be negative");

        var channel = request.Channels[0];
        if (channel.PendingChanId.Length > 0)
            throw Unimplemented("pending_chan_id (PSBT funding) is not supported");
        if (channel.UseBaseFee && channel.BaseFee > uint.MaxValue)
            throw InvalidArgument("base_fee is out of range");
        if (channel.UseFeeRate && channel.FeeRate > uint.MaxValue)
            throw InvalidArgument("fee_rate is out of range");

        var open = new OpenChannelRequest
        {
            NodePubkey = channel.NodePubkey,
            LocalFundingAmount = channel.LocalFundingAmount,
            PushSat = channel.PushSat,
            Private = channel.Private,
            MinHtlcMsat = channel.MinHtlcMsat,
            RemoteCsvDelay = channel.RemoteCsvDelay,
            CloseAddress = channel.CloseAddress,
            CommitmentType = channel.CommitmentType,
            RemoteMaxValueInFlightMsat = channel.RemoteMaxValueInFlightMsat,
            RemoteMaxHtlcs = channel.RemoteMaxHtlcs,
            MaxLocalCsv = channel.MaxLocalCsv,
            ZeroConf = channel.ZeroConf,
            ScidAlias = channel.ScidAlias,
            RemoteChanReserveSat = channel.RemoteChanReserveSat,
            SatPerVbyte = (ulong)request.SatPerVbyte,
            Memo = string.IsNullOrWhiteSpace(channel.Memo) ? request.Label : channel.Memo
        };

        // Validates every field before the node funds anything.
        _ = ToOpenRequest(open);
        var funded = await OpenUntilPublishedAsync(open, context);

        if (channel.UseBaseFee || channel.UseFeeRate)
        {
            try
            {
                await DispatchAsync<SetChannelPolicyClientRequest, ChannelPolicyClientResponse>(
                    new SetChannelPolicyClientRequest(new ChannelReference(funded.ChannelId))
                    {
                        FeeBaseMsat = channel.UseBaseFee ? (uint)channel.BaseFee : null,
                        FeeProportionalMillionths = channel.UseFeeRate ? (uint)channel.FeeRate : null
                    }, context);
            }
            catch (RpcException e)
            {
                _logger.LogWarning("BatchOpenChannel: the routing policy of channel {ChannelId} was not set: {Error}",
                                   funded.ChannelId, e.Status.Detail);
            }
        }

        return new BatchOpenChannelResponse
        {
            PendingChannels =
            {
                new PendingUpdate
                {
                    Txid = ByteString.CopyFrom((byte[])funded.TxId!.Value),
                    OutputIndex = funded.Index ?? 0
                }
            }
        };
    }
}