using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.LndGrpc.Services;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Money;
using Walletrpc;

public sealed partial class WalletKitService
{
    public override async Task<SendOutputsResponse> SendOutputs(SendOutputsRequest request, ServerCallContext context)
    {
        if (request.SpendUnconfirmed)
            throw new RpcException(new Status(StatusCode.Unimplemented, "spend_unconfirmed is not supported"));
        CheckCoinSelectionStrategy(request.CoinSelectionStrategy);
        if (request.Label.Length > 0)
            CheckLabel(request.Label);
        if (request.SatPerKw <= 0 || request.MinConfs < 0 || request.Outputs.Any(o => o.Value <= 0))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid fee rate, confirmations or output value"));
        var spend = _serviceProvider.GetRequiredService<IWalletSpendService>();
        var outputs = request.Outputs.Select(o => ((BitcoinScript)o.PkScript.ToByteArray(), LightningMoney.Satoshis(o.Value))).ToList();
        var raw = await Run(() => spend.SendOutputsAsync(outputs, request.SatPerKw, Math.Max(1, request.MinConfs),
            request.Label, context.CancellationToken));
        return new SendOutputsResponse { RawTx = ByteString.CopyFrom(raw) };
    }
}