using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Payments.Trampoline.Harness;

using Channels.Harness;

/// <summary>The four nodes of <see cref="TrampolineHarness"/>, as flags.</summary>
[Flags]
internal enum TrampolineHarnessNodes
{
    None = 0,

    /// <summary>The payer.</summary>
    A = 1,

    /// <summary>The trampoline node (the relay of phase 2).</summary>
    T = 2,

    /// <summary>A plain forwarding node between the trampoline node and the recipient.</summary>
    X = 4,

    /// <summary>The recipient (the final trampoline node).</summary>
    C = 8,

    All = A | T | X | C
}

/// <summary>
/// What a <see cref="TrampolineHarness"/> is built with: which nodes advertise <c>trampoline_routing</c>, which run the
/// production <c>PaymentService</c> and see the gossip graph, the extra A–T channel, and last changes per node (where
/// phase 2 registers the relay engine and the leg sender).
/// </summary>
[ExcludeFromCodeCoverage]
internal sealed class TrampolineHarnessOptions
{
    /// <summary>
    /// The nodes that advertise <c>trampoline_routing</c> (bit 57; with <c>AllowExperimentalFeatures</c> while it is
    /// experimental). Default: T and C.
    /// </summary>
    public TrampolineHarnessNodes Trampoline { get; set; } = TrampolineHarnessNodes.T | TrampolineHarnessNodes.C;

    /// <summary>
    /// The nodes that run the production payment service (<c>AddPaymentSendServices</c>: <c>IPaymentService</c>,
    /// route planner, mission control). Default: none (phase 1 builds A's onions by hand).
    /// </summary>
    public TrampolineHarnessNodes PaymentSenders { get; set; } = TrampolineHarnessNodes.None;

    /// <summary>
    /// The nodes whose payment service sees the harness's gossip graph (<see cref="TrampolineHarness.BuildGraph"/>:
    /// every channel with both policies and every node's announced features, bit 57 included). Only meaningful for
    /// <see cref="PaymentSenders"/>. Default: every node.
    /// </summary>
    public TrampolineHarnessNodes GraphViewers { get; set; } = TrampolineHarnessNodes.All;

    /// <summary>Also open a second A–T channel (<see cref="TrampolineHarness.AliceTrampoline2ChannelId"/>), for
    /// multi-part payments over two first hops.</summary>
    public bool SecondAliceTrampolineChannel { get; set; }

    /// <summary>Also open a second T–X and a second X–C channel (<see cref="TrampolineHarness.TrampolineX2ChannelId"/>,
    /// <see cref="TrampolineHarness.XCarol2ChannelId"/>), so T's outgoing leg can split over two routes.</summary>
    public bool SecondLegChannels { get; set; }

    /// <summary>
    /// Every node runs on the harness's shared stepped clock (<see cref="TrampolineHarness.Clock"/>), so MPP timers
    /// fire only from <see cref="TrampolineHarness.AdvanceAsync"/>. Default: true.
    /// </summary>
    public bool SteppedClock { get; set; } = true;

    /// <summary>
    /// When set, every node's log lines at this level and above go to the test's output, prefixed with the node's name
    /// (<see cref="HarnessLoggerProvider"/>). Default: off.
    /// </summary>
    public Microsoft.Extensions.Logging.LogLevel? LogLevel { get; set; }

    /// <summary>Changes to a node before it starts (options: features, routing policy, keysend...).</summary>
    public Action<SwitchNode>? ConfigureNode { get; set; }

    /// <summary>
    /// Last changes to a node's services, applied on every start after the harness's own (e.g.
    /// <c>services.AddTrampolineRelayServices()</c> on T, a recording decorator, a hook).
    /// </summary>
    public Action<SwitchNode, IServiceCollection>? ConfigureServices { get; set; }
}