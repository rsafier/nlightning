using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Bark;

using Images;
using Kube;
using Run;

/// <summary>
/// A bark wallet of a run (NL-1148 wave C): Second's <c>bark</c> CLI, built into the <c>nltg-captaind</c> image from the
/// same bark commit as the server, in a pod that idles (<c>sleep infinity</c>) while the test drives the wallet with
/// <c>kubectl exec</c>. One wallet per data directory (<see cref="DataDirectory"/>), on captaind's own chain (its
/// bitcoind's RPC) and talking to captaind's public Ark gRPC — exactly the client a Bark user runs.
/// </summary>
/// <remarks>
/// Every command runs with <c>--quiet</c> (no terminal log, so stdout is the command's JSON alone) and keeps bark's
/// <c>debug.log</c> in the data directory (<see cref="ReadDebugLogAsync"/> for a failure dump). Exec has no timeout of
/// its own, so each call carries one.
/// </remarks>
public sealed class BarkWalletNode
{
    private BarkWalletNode(KubeNodeHandle handle)
    {
        Handle = handle;
    }

    /// <summary>The scratch volume the wallets' data directories live on.</summary>
    public const string DataPath = "/wallet";

    /// <summary>The wallet's data directory (<c>--datadir</c>).</summary>
    public const string DataDirectory = DataPath + "/bark";

    /// <summary>The deployed pod.</summary>
    public KubeNodeHandle Handle { get; }

    /// <summary>The workload: the captaind image (which carries <c>bark</c>) idling for exec.</summary>
    public static NodeWorkload Workload(string name = "bark-wallet", ImageRef? image = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var workload = new NodeWorkload(name, NodeKind.Other, image ?? ImageVersions.Captaind)
        {
            Command = ["sleep", "infinity"],
            Resources = new WorkloadResources("50m", "64Mi", "1", "512Mi"),
            TerminationGracePeriodSeconds = 1
        };
        workload.ScratchVolumes["wallet"] = DataPath;
        return workload;
    }

    /// <summary>Deploys the wallet pod into <paramref name="run"/>'s namespace.</summary>
    public static async Task<BarkWalletNode> DeployAsync(TestRun run, string name, TimeSpan readyTimeout,
                                                         CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var handle = await run.DeployAsync(Workload(name), readyTimeout, cancellationToken).ConfigureAwait(false);
        return new BarkWalletNode(handle);
    }

    /// <summary>
    /// The arguments of <c>bark create</c> for a regtest wallet on captaind at <paramref name="arkUrl"/> with the chain
    /// from bitcoind's RPC.
    /// </summary>
    public static IReadOnlyList<string> CreateArguments(string arkUrl, string bitcoindUrl, string rpcUser,
                                                        string rpcPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arkUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bitcoindUrl);
        return
        [
            "create", "--regtest", "--ark", arkUrl, "--bitcoind", bitcoindUrl, "--bitcoind-user", rpcUser,
            "--bitcoind-pass", rpcPassword
        ];
    }

    /// <summary>The full command line of one wallet call.</summary>
    public static IReadOnlyList<string> Command(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return ["bark", "--quiet", "--datadir", DataDirectory, .. arguments];
    }

    /// <summary>Runs one wallet command and returns its stdout.</summary>
    /// <exception cref="BarkWalletException">The command exited non-zero (stdout and stderr in the message).</exception>
    /// <exception cref="TimeoutException">The command did not finish within <paramref name="timeout"/>.</exception>
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
                                       CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        ExecResult result;
        try
        {
            result = await Handle.ExecAsync(Command(arguments), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"bark {string.Join(' ', arguments)} did not finish within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s");
        }

        var stdOut = Encoding.UTF8.GetString(result.StdOut);
        if (result.ExitCode != 0)
            throw new BarkWalletException(arguments, result.ExitCode, stdOut, Encoding.UTF8.GetString(result.StdErr));
        return stdOut;
    }

    /// <summary>Runs one wallet command and parses its JSON output.</summary>
    public async Task<JsonNode> RunJsonAsync(IReadOnlyList<string> arguments, TimeSpan timeout,
                                             CancellationToken cancellationToken)
    {
        var output = await RunAsync(arguments, timeout, cancellationToken).ConfigureAwait(false);
        return ParseJson(arguments, output);
    }

    /// <summary>The JSON a bark command printed (its stdout); a command that printed none is an error.</summary>
    public static JsonNode ParseJson(IReadOnlyList<string> arguments, string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
            throw new BarkWalletException(arguments, 0, output, "the command printed no JSON");
        return JsonNode.Parse(trimmed)
            ?? throw new BarkWalletException(arguments, 0, output, "the command printed JSON null");
    }

    /// <summary>The tail of the wallet's <c>debug.log</c> (empty when there is none yet).</summary>
    public async Task<string> ReadDebugLogAsync(int lines, CancellationToken cancellationToken)
    {
        var result = await Handle.ExecAsync(
                                     ["sh", "-c",
                                      $"tail -n {lines.ToString(CultureInfo.InvariantCulture)} {DataDirectory}/debug.log 2>/dev/null || true"],
                                     cancellationToken)
                                 .ConfigureAwait(false);
        return Encoding.UTF8.GetString(result.StdOut);
    }
}

/// <summary>A bark wallet command that failed.</summary>
public sealed class BarkWalletException(IReadOnlyList<string> arguments, int exitCode, string stdOut, string stdErr)
    : Exception($"bark {string.Join(' ', arguments)} exited {exitCode.ToString(CultureInfo.InvariantCulture)}: "
              + $"{stdErr.Trim()} {stdOut.Trim()}".Trim())
{
    public int ExitCode { get; } = exitCode;

    public string StdOut { get; } = stdOut;

    public string StdErr { get; } = stdErr;
}