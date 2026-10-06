using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Services;

using Application.Payments.Interception;
using Domain.Channels.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Routerrpc;

/// <summary>
/// LND's <c>routerrpc.Router</c> over this node (<c>LND_GRPC_PLAN.md</c>): <c>SendPaymentV2</c>,
/// <c>TrackPaymentV2</c> and <c>TrackPayments</c> over <see cref="IPaymentService"/> and the payment event bus
/// (<c>RouterService.Payments.cs</c>, wave 2, NL-1164) and <c>HtlcInterceptor</c> (<c>RouterService.Interceptor.cs</c>,
/// wave 3, NL-1183). Every other Router method answers <c>UNIMPLEMENTED</c>.
/// </summary>
public sealed partial class RouterService : Router.RouterBase
{
    /// <summary>How often a stream re-reads a payment while it waits for its outcome.</summary>
    private static readonly TimeSpan s_poll = TimeSpan.FromMilliseconds(500);

    private readonly IChannelMemoryRepository _channels;
    private readonly ILogger<RouterService> _logger;
    private readonly NodeOptions _nodeOptions;
    private readonly IPaymentService _paymentService;
    private readonly TimeProvider _timeProvider;
    private readonly IPaymentEventSource? _events;
    private readonly HtlcInterceptorHub _interceptorHub;
    private readonly ILogger<RouterService> _routerLogger;
    private readonly LndGrpcOptions _routerOptions;

    public RouterService(IPaymentService paymentService, IChannelMemoryRepository channels,
                         IOptions<NodeOptions> nodeOptions, ILogger<RouterService> logger,
                         HtlcInterceptorHub interceptorHub, IOptions<LndGrpcOptions>? options = null,
                         TimeProvider? timeProvider = null, IPaymentEventSource? events = null)
    {
        _paymentService = paymentService;
        _channels = channels;
        _nodeOptions = nodeOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _events = events;
        _interceptorHub = interceptorHub;
        _routerLogger = logger;
        _routerOptions = options?.Value ?? new LndGrpcOptions();
    }
}