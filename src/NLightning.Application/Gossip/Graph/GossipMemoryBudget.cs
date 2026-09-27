using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Gossip.Graph;

using Interfaces;
using Metrics;

/// <summary>
/// The gossip memory budget <c>Gossip:MaxMemoryMb</c> (NL-373, BOLT 7 plan G5-T1, §3.8), checked against the resident
/// set of the whole process.
/// </summary>
/// <remarks>
/// <para>
/// Degrade policy, kept simple on purpose: while the process is over <see cref="GossipGraphOptions.MaxMemoryMb"/>,
/// the ingress refuses every <b>new</b> channel (a <c>channel_announcement</c> of a short channel id the graph does not
/// hold) and every <b>new</b> node (a <c>node_announcement</c> of a node without one stored), counted as rejected with
/// the reason <see cref="GossipMetricReasons.MemoryBudget"/>. Everything already in the graph keeps taking
/// <c>channel_update</c>s and newer <c>node_announcement</c>s, pruning goes on, and our own gossip is always applied.
/// New entries are accepted again once the process is below <see cref="GossipGraphOptions.MemoryResumePercent"/> of
/// the budget (hysteresis, so a graph at the edge does not flap). Each crossing is logged once (a warning going over,
/// an information coming back) and counted in <c>nlightning.gossip.memory.budget.exceeded</c>; the last reading is the
/// gauge <c>nlightning.gossip.memory.working_set</c>. A refused channel is not queued for a re-request: the next range
/// sync with a peer asks for it again.
/// </para>
/// <para>
/// The process is read lazily, at most once per <see cref="GossipGraphOptions.MemorySampleInterval"/>, when the ingress
/// is about to add something new or <c>describegraph</c> asks. The budget covers the whole process (graph, snapshots,
/// sync garbage such as <c>getblock</c> JSON, NL-416, and everything that is not gossip), since that is what an
/// operator sizes a machine by. Thread-safe.
/// </para>
/// </remarks>
public sealed class GossipMemoryBudget
{
    private const long BytesPerMegabyte = 1L << 20;

    private readonly IProcessMemoryReader _reader;
    private readonly ILogger<GossipMemoryBudget> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly GossipMetrics? _metrics;
    private readonly TimeSpan _sampleInterval;
    private readonly Lock _gate = new();

    private DateTimeOffset? _sampledAt;
    private ProcessMemoryUsage _lastUsage;
    private bool _isOverBudget;
    private long _crossings;
    private long _refused;

    public GossipMemoryBudget(IOptions<GossipGraphOptions> options, ILogger<GossipMemoryBudget> logger,
                              IProcessMemoryReader? reader = null, TimeProvider? timeProvider = null,
                              GossipMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        BudgetBytes = Math.Max(0, value.MaxMemoryMb) * BytesPerMegabyte;
        ResumeBytes = BudgetBytes * Math.Clamp(value.MemoryResumePercent, 1, 100) / 100;
        _sampleInterval = value.MemorySampleInterval;
        _reader = reader ?? ProcessMemoryReader.Instance;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _metrics = metrics;
        metrics?.RegisterMemoryWorkingSet(() =>
        {
            lock (_gate)
                return _lastUsage.WorkingSetBytes;
        });
    }

    /// <summary>The budget in bytes; 0 when <c>Gossip:MaxMemoryMb</c> is 0 (off).</summary>
    public long BudgetBytes { get; }

    /// <summary>Below this many bytes an exceeded budget accepts new entries again.</summary>
    public long ResumeBytes { get; }

    /// <summary>The budget is on.</summary>
    public bool IsEnabled => BudgetBytes > 0;

    /// <summary>
    /// True while new channels and nodes are refused: the process went over the budget and has not come back below
    /// <see cref="ResumeBytes"/> yet. Reads the process when the last reading is older than the sample interval.
    /// </summary>
    public bool IsOverBudget
    {
        get
        {
            if (!IsEnabled)
                return false;

            Refresh();
            lock (_gate)
                return _isOverBudget;
        }
    }

    /// <summary>The times the process went over the budget since the start.</summary>
    public long Crossings => Interlocked.Read(ref _crossings);

    /// <summary>New channels and nodes refused over the budget since the start.</summary>
    public long RefusedCount => Interlocked.Read(ref _refused);

    /// <summary>The budget's state now (reads the process when the last reading is old).</summary>
    public GossipMemoryBudgetState GetState()
    {
        Refresh();
        lock (_gate)
            return new GossipMemoryBudgetState(BudgetBytes, ResumeBytes, _lastUsage.WorkingSetBytes,
                                               _lastUsage.ManagedHeapBytes, _isOverBudget, Crossings, RefusedCount);
    }

    /// <summary>
    /// The ingress's admission check for a new channel or node (<paramref name="what"/> names it for the result):
    /// null when it may be added, else the <see cref="GossipMetricReasons.MemoryBudget"/> refusal.
    /// </summary>
    internal GossipIngressResult? RefuseNew(string what)
    {
        if (!IsOverBudget)
            return null;

        Interlocked.Increment(ref _refused);
        return GossipIngressResult.Limited(
            $"the process is over Gossip:MaxMemoryMb ({BudgetBytes / BytesPerMegabyte} MiB): new {what} refused",
            GossipMetricReasons.MemoryBudget);
    }

    private void Refresh()
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            if (_sampledAt is { } at && now - at < _sampleInterval)
                return;

            _sampledAt = now;
            ProcessMemoryUsage usage;
            try
            {
                usage = _reader.Read();
            }
            catch (Exception e)
            {
                // A failed read keeps the last decision; the next interval reads again
                _logger.LogDebug(e, "Could not read the process memory");
                return;
            }

            _lastUsage = usage;
            if (!IsEnabled)
                return;

            if (!_isOverBudget && usage.WorkingSetBytes > BudgetBytes)
            {
                _isOverBudget = true;
                Interlocked.Increment(ref _crossings);
                _metrics?.RecordMemoryBudgetExceeded();
                _logger.LogWarning(
                    "The process uses {UsedMb} MiB (managed heap {HeapMb} MiB), above Gossip:MaxMemoryMb {BudgetMb} "
                  + "MiB: new channels and nodes from gossip are refused until it is below {ResumeMb} MiB; known ones "
                  + "keep updating", usage.WorkingSetBytes / BytesPerMegabyte,
                    usage.ManagedHeapBytes / BytesPerMegabyte, BudgetBytes / BytesPerMegabyte,
                    ResumeBytes / BytesPerMegabyte);
            }
            else if (_isOverBudget && usage.WorkingSetBytes < ResumeBytes)
            {
                _isOverBudget = false;
                _logger.LogInformation(
                    "The process uses {UsedMb} MiB, below {ResumeMb} MiB of Gossip:MaxMemoryMb {BudgetMb} MiB: new "
                  + "channels and nodes from gossip are accepted again ({Refused} refused so far)",
                    usage.WorkingSetBytes / BytesPerMegabyte, ResumeBytes / BytesPerMegabyte,
                    BudgetBytes / BytesPerMegabyte, RefusedCount);
            }
        }
    }
}

/// <summary>What <see cref="GossipMemoryBudget.GetState"/> read.</summary>
/// <param name="BudgetBytes"><c>Gossip:MaxMemoryMb</c> in bytes; 0 when the budget is off.</param>
/// <param name="ResumeBytes">The level below which new entries are accepted again.</param>
/// <param name="WorkingSetBytes">The process's resident set at the last reading (0 before the first).</param>
/// <param name="ManagedHeapBytes">The managed heap at the last reading.</param>
/// <param name="IsOverBudget">New channels and nodes are refused now.</param>
/// <param name="Crossings">The times the process went over the budget since the start.</param>
/// <param name="Refused">New channels and nodes refused over the budget since the start.</param>
public sealed record GossipMemoryBudgetState(
    long BudgetBytes,
    long ResumeBytes,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    bool IsOverBudget,
    long Crossings,
    long Refused);