using System.Text;

namespace NLightning.Testing.Cluster.Kube;

/// <summary>
/// What a command run in a container returned: its exit code and its raw output (binary safe).
/// </summary>
public sealed record ExecResult(int ExitCode, byte[] StdOut, byte[] StdErr)
{
    public bool Succeeded => ExitCode == 0;

    public string StdOutText => Encoding.UTF8.GetString(StdOut);

    public string StdErrText => Encoding.UTF8.GetString(StdErr);

    /// <summary>Returns this result, or throws <see cref="KubeExecException"/> when the command failed.</summary>
    public ExecResult EnsureSuccess(string what) =>
        Succeeded ? this : throw new KubeExecException(what, this);
}

/// <summary>
/// A command run in a container exited with a non-zero code.
/// </summary>
public sealed class KubeExecException(string what, ExecResult result)
    : Exception($"{what} exited with {result.ExitCode}: {Trim(result.StdErrText)}")
{
    public ExecResult Result { get; } = result;

    private static string Trim(string text) => text.Length > 2000 ? text[..2000] + "..." : text.Trim();
}