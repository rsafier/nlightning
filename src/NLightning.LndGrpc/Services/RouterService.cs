using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.LndGrpc.Services;

using Domain.Channels.Interfaces;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Routerrpc;

/// <summary>
/// LND's <c>routerrpc.Router</c> over this node (<c>LND_GRPC_PLAN.md</c> §3 wave 2, NL-1164): <c>SendPaymentV2</c>,
/// <c>TrackPaymentV2</c> and <c>TrackPayments</c> over <see cref="IPaymentService"/> and the payment event bus. Every
/// other Router method answers <c>UNIMPLEMENTED</c>. Partial: later waves add their methods in files of their own.
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

    public RouterService(IPaymentService paymentService, IChannelMemoryRepository channels,
                         IOptions<NodeOptions> nodeOptions, ILogger<RouterService> logger,
                         TimeProvider? timeProvider = null, IPaymentEventSource? events = null)
    {
        _paymentService = paymentService;
        _channels = channels;
        _nodeOptions = nodeOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _events = events;
    }
}