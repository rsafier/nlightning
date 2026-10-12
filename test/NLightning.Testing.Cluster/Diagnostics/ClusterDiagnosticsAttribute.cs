using System.Reflection;
using Xunit;
using Xunit.v3;

namespace NLightning.Testing.Cluster.Diagnostics;

/// <summary>
/// The xunit v3 test-failure hook of <see cref="ClusterDiagnostics"/>: after a failed test, every harness run still
/// alive that belongs to the test or to its collection's fixtures is marked failed and dumped (with
/// <see cref="DiagnosticsMode.Always"/>, after every test). Apply it once per test assembly:
/// <c>[assembly: ClusterDiagnostics]</c>. Tests without runs cost nothing; the hook never fails a test.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ClusterDiagnosticsAttribute : BeforeAfterTestAttribute
{
    /// <summary>How long the hook waits for its dumps before it lets the test end.</summary>
    public static TimeSpan HookTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        var scope = ClusterTestScope.Current();
        if (ClusterDiagnostics.LiveRunsOf(scope).Count == 0)
            return;

        var state = TestContext.Current.TestState;
        var failed = state?.Result == TestResult.Failed;
        var failure = failed ? Describe(state!) : null;
        try
        {
            // After is synchronous: run the dumps off the test's context and wait for them
            if (!Task.Run(() => ClusterDiagnostics.OnTestFinishedAsync(scope, failed, failure)).Wait(HookTimeout))
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"[nltg-diag] diagnostics still running after {HookTimeout}; continuing");
        }
        catch (Exception e)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"[nltg-diag] diagnostics failed: {ClusterDiagnostics.Describe(e)}");
        }
    }

    private static string Describe(TestResultState state)
    {
        var types = state.ExceptionTypes ?? [];
        var messages = state.ExceptionMessages ?? [];
        return types.Length == 0
                   ? "(no exception)"
                   : string.Join(" ---> ", types.Select((t, i) => $"{t}: {(i < messages.Length ? messages[i] : null)}"));
    }
}