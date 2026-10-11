using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.Acceptance;
using Infrastructure.Bitcoin.Networks;
using Lnrpc;

public sealed partial class LightningService
{
    /// <summary>
    /// LND's <c>ChannelAcceptor</c> (NL-1180): while the stream is open, every inbound <c>open_channel</c> and
    /// <c>open_channel2</c> waits for the client's answer (<see cref="RpcChannelAcceptor"/>); several streams must all
    /// accept, as with LND. Without a stream the node accepts opens as usual.
    /// </summary>
    public override async Task ChannelAcceptor(IAsyncStreamReader<ChannelAcceptResponse> requestStream,
                                               IServerStreamWriter<ChannelAcceptRequest> responseStream,
                                               ServerCallContext context)
    {
        IChannelOpenDecisionGate? gate;
        await using (var scope = CreateScope())
            gate = scope.ServiceProvider.GetService<IChannelOpenDecisionGate>();
        if (gate is null)
            throw Unimplemented("this node has no channel open gate");

        var acceptor = new RpcChannelAcceptor(_nodeOptions.BitcoinNetwork.ToNBitcoinNetwork(),
                                              _options.AcceptorTimeout, _options.MaxPendingChannelAccepts,
                                              _timeProvider, _logger);
        using var registration = gate.Register(acceptor);
        _logger.LogInformation("LND gRPC channel acceptor connected");
        try
        {
            await acceptor.RunAsync(requestStream, responseStream, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            // The client went away
        }
        finally
        {
            _logger.LogInformation("LND gRPC channel acceptor disconnected");
        }
    }
}