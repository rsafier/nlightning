namespace NLightning.Integration.Tests.Docker.Utils;

/// <summary>
/// Failure diagnostics for the integration tests. The in-process nodes already log to the test output (prefixed with
/// their name); the cluster runs add a namespace dump (<c>[assembly: ClusterDiagnostics]</c>).
/// </summary>
public static class TestDiagnostics
{
    /// <summary>
    /// Whether the current test has failed. Valid in the test class's <c>DisposeAsync</c> (xUnit sets the result
    /// before cleanup).
    /// </summary>
    public static bool CurrentTestFailed => TestContext.Current.TestState?.Result == TestResult.Failed;
}