using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Services;

using Application.Channels.RoutingPolicies;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Exceptions;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Infrastructure.Bitcoin.Networks;
using Lnrpc;
using NBitcoin;

public sealed partial class LightningService
{
    /// <summary>
    /// <c>FeeReport</c> as LND 0.21.4 answers it (NL-1239): one entry per channel <c>ListChannels</c> lists with the
    /// forwarding policy in force (the node's <c>getchannelpolicy</c>, the policy <c>UpdateChannelPolicy</c> sets:
    /// <c>fee_rate</c> is <c>fee_per_mil</c> / 1,000,000; this node sets no inbound fees, so those are 0), and the fees
    /// of the forwards settled over the last 24 hours, 7 days and 30 days, summed in msat and truncated to sat as LND
    /// does (the forwards <c>ForwardingHistory</c> lists, by when the forward started).
    /// </summary>
    public override async Task<FeeReportResponse> FeeReport(FeeReportRequest request, ServerCallContext context)
    {
        var response = new FeeReportResponse();
        foreach (var channel in _channels.FindChannels(IsListed)
                                         .OrderBy(c => c.ShortChannelId.BlockHeight)
                                         .ThenBy(c => c.ShortChannelId.TransactionIndex))
        {
            var policy = await EffectivePolicyAsync(channel, context.CancellationToken);
            if (policy is null)
                continue;

            response.ChannelFees.Add(new ChannelFeeReport
            {
                ChanId = ToChanId(channel.ShortChannelId),
                ChannelPoint = ChannelPoint(channel),
                BaseFeeMsat = policy.FeeBaseMsat,
                FeePerMil = policy.FeeProportionalMillionths,
                FeeRate = policy.FeeProportionalMillionths / 1_000_000D
            });
        }

        var now = _timeProvider.GetUtcNow();
        await using var scope = CreateScope();
        var forwards = UnitOfWork(scope).ForwardCircuitDbRepository;
        response.DayFeeSum = await FeeSumAsync(forwards, now - TimeSpan.FromDays(1), now, context.CancellationToken);
        response.WeekFeeSum = await FeeSumAsync(forwards, now - TimeSpan.FromDays(7), now, context.CancellationToken);
        response.MonthFeeSum =
            await FeeSumAsync(forwards, now - TimeSpan.FromDays(30), now, context.CancellationToken);
        return response;
    }

    /// <summary>The policy in force for one of our channels, or null when the channel left memory meanwhile.</summary>
    private async Task<EffectiveChannelPolicy?> EffectivePolicyAsync(ChannelModel channel,
                                                                     CancellationToken cancellationToken)
    {
        if (_channelPolicyService is null)
            return ChannelPolicyRules.Resolve(channel, _nodeOptions.Routing, null);

        try
        {
            return await _channelPolicyService.GetAsync(channel.ChannelId, cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The fees, in whole sat (LND truncates the msat sum), of the forwards fulfilled in [since, until].</summary>
    private static async Task<ulong> FeeSumAsync(IForwardCircuitDbRepository forwards,
                                                 DateTimeOffset since, DateTimeOffset until,
                                                 CancellationToken cancellationToken)
    {
        var totals = await forwards.SummarizeAsync(
                         new ForwardCircuitListQuery(0, 1, since, until, ForwardCircuitStatus.Fulfilled),
                         cancellationToken);
        return (ulong)Math.Max(0, totals.FulfilledFeesMsat) / 1000;
    }

    /// <summary>Loop's single-address wallet quote, without reserving or signing inputs.</summary>
    public override async Task<EstimateFeeResponse> EstimateFee(EstimateFeeRequest request, ServerCallContext context)
    {
        if (request.AddrToAmount.Count != 1 || request.SpendUnconfirmed || request.MinConfs > 1
         || request.Inputs.Count != 0 || request.CoinSelectionStrategy != CoinSelectionStrategy.StrategyUseGlobalConfig)
            throw Unimplemented("fee quotes support one address and default confirmed coin selection");
        if (request.TargetConf < 0 || request.MinConfs < 0 || request.AddrToAmount.Values.Any(v => v <= 0))
            throw InvalidArgument("invalid amount or confirmation target");
        await using var scope = _scopeFactory.CreateAsyncScope();
        var spend = scope.ServiceProvider.GetRequiredService<IWalletSpendService>();
        var fees = scope.ServiceProvider.GetRequiredService<IFeeService>();
        var rate = request.TargetConf == 0 ? await fees.GetFeeRatePerKwAsync(context.CancellationToken)
            : await fees.GetFeeRatePerKwAsync((uint)request.TargetConf, context.CancellationToken);
        var output = request.AddrToAmount.Single();
        try
        {
            var quote = await spend.EstimateOutputFeeAsync(QuoteScript(output.Key),
                LightningMoney.Satoshis(output.Value), rate, context.CancellationToken);
            return new EstimateFeeResponse
            {
                FeeSat = quote.Fee.Satoshi,
                SatPerVbyte = (ulong)((quote.FeeRatePerKw.Satoshi * 4 + 999) / 1000)
            };
        }
        catch (FormatException e) { throw InvalidArgument(e.Message); }
        catch (ArgumentException e) { throw InvalidArgument(e.Message); }
        catch (Exception e) when (e is WalletSpendException or InsufficientFundsException or AnchorReserveException)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, e.Message));
        }
    }

    private BitcoinScript QuoteScript(string address)
    {
        var network = _nodeOptions.BitcoinNetwork.ToNBitcoinNetwork();
        try { return BitcoinAddress.Create(address, network).ScriptPubKey.ToBytes(); }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            // A fee quote accepts a checksummed witness program even when its x-only point cannot be spent.
            var encoder = network.GetBech32Encoder(Bech32Type.TAPROOT_ADDRESS, true)
                ?? throw new ArgumentException("network does not support taproot addresses");
            var program = encoder.Decode(address, out var version);
            if (version != 1 || program.Length != 32)
                throw new ArgumentException("invalid quote address", e);
            return new byte[] { 0x51, 0x20 }.Concat(program).ToArray();
        }
    }

}