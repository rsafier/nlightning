namespace NLightning.Testing.Cluster.Run.Matrix;

/// <summary>What a suite needs before the matrix runner may start it on the cluster backend.</summary>
public enum SuiteRequirement
{
    None,

    /// <summary>
    /// <c>LightningRegtestNetworkFixture</c> delegates to <c>ILndNetworkBackend</c> (test harness phase 3 wiring).
    /// Without it <c>NLTG_TEST_BACKEND=cluster</c> is ignored and the fixture starts Docker containers outside the
    /// machine's Docker lock, so the runner skips such a suite instead (<see cref="LndBackendProbe"/>).
    /// </summary>
    LndClusterBackend
}

/// <summary>
/// One suite of the matrix (test harness plan §5 step 5): which tests of which test project, under which xunit
/// explicit mode, how many run namespaces one process of it holds at once, and how long a run may take.
/// </summary>
/// <param name="Name">The name <c>run-cluster.sh --suite/--matrix</c> takes (lower case, no '-').</param>
/// <param name="Description">One line for <c>nltg-cluster matrix list</c>.</param>
/// <param name="Project"><c>integration</c> (test/NLightning.Integration.Tests) or <c>cluster</c>.</param>
/// <param name="Selection">
/// The xunit v3 simple filters that pick the suite's tests (<c>-class</c>, <c>-namespace</c>); a caller's
/// <c>--class</c>/<c>--method</c> replaces them, and a rerun replaces them with the one failed class.
/// </param>
/// <param name="Constraints">
/// Filters always applied (<c>-trait</c>, <c>-class-</c>, <c>-trait-</c>); xunit ANDs filters of different types.
/// </param>
/// <param name="Explicit">xunit's <c>-explicit</c> mode: <c>off</c>, <c>on</c> or <c>only</c>.</param>
/// <param name="Namespaces">Run namespaces one process holds at once with xunit's default collection parallelism.</param>
/// <param name="SerialNamespaces">The same with <c>-parallel none</c> (collections one after another).</param>
/// <param name="Timeout">The hang timeout of one run of the suite (the process is killed after it).</param>
/// <param name="Requirement">What must hold before the suite may run on the cluster.</param>
/// <param name="DockerOnlyReason">Set for a suite that never runs on the cluster: why, and how to run it.</param>
public sealed record MatrixSuite(
    string Name,
    string Description,
    string Project,
    IReadOnlyList<string> Selection,
    IReadOnlyList<string> Constraints,
    string Explicit,
    int Namespaces,
    int SerialNamespaces,
    TimeSpan Timeout,
    SuiteRequirement Requirement = SuiteRequirement.None,
    string? DockerOnlyReason = null);