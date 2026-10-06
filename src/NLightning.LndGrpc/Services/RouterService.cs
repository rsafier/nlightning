using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Services;

using Application.Payments.Interception;

/// <summary>
/// LND's <c>routerrpc.Router</c> service over this node (<c>LND_GRPC_PLAN.md</c>): the methods are in the partial files
/// (<c>RouterService.Interceptor.cs</c>, NL-1183); every method not overridden answers <c>UNIMPLEMENTED</c>.
/// </summary>
public sealed partial class RouterService : Routerrpc.Router.RouterBase
{
    private readonly HtlcInterceptorHub _interceptorHub;
    private readonly ILogger<RouterService> _routerLogger;
    private readonly LndGrpcOptions _routerOptions;

    public RouterService(HtlcInterceptorHub interceptorHub, ILogger<RouterService> logger,
                         IOptions<LndGrpcOptions>? options = null)
    {
        _interceptorHub = interceptorHub;
        _routerLogger = logger;
        _routerOptions = options?.Value ?? new LndGrpcOptions();
    }
}