using Grpc.Core;

namespace NLightning.LndGrpc.Services;

using Lnrpc;

/// <summary>The key manager is unlocked before this listener starts. No wallet unlock RPC is exposed.</summary>
public sealed class StateService : State.StateBase
{
    public override Task<GetStateResponse> GetState(GetStateRequest request, ServerCallContext context) =>
        Task.FromResult(new GetStateResponse { State = WalletState.ServerActive });

    public override async Task SubscribeState(SubscribeStateRequest request,
                                               IServerStreamWriter<SubscribeStateResponse> responseStream,
                                               ServerCallContext context)
    {
        await responseStream.WriteAsync(new SubscribeStateResponse { State = WalletState.ServerActive },
                                         context.CancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
    }
}