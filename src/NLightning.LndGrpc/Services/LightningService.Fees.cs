using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Exceptions;
using Domain.Money;
using Infrastructure.Bitcoin.Networks;
using Lnrpc;
using NBitcoin;

public sealed partial class LightningService
{
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