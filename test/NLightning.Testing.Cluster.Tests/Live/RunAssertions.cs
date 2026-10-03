namespace NLightning.Testing.Cluster.Tests.Live;

using Cluster.Run;

/// <summary>Assertions on a run's namespace after its disposal (which deletes it in the background).</summary>
internal static class RunAssertions
{
    /// <summary>
    /// Asserts that <paramref name="ns"/> is gone or terminating: a disposed run issues the deletion and returns
    /// (<see cref="TestRunOptions.WaitForDeletion"/> is off), and the namespace keeps its admission slot until it is
    /// gone.
    /// </summary>
    public static async Task AssertDeletedOrTerminatingAsync(string ns, CancellationToken cancellationToken)
    {
        if (TestRunOptions.FromEnvironment("assert").KeepNamespace)
            return; // NLTG_KEEP_NAMESPACE: kept for debugging on purpose

        using var client = KubeClientFactory.Create();
        var namespaceObject = await RunNamespace.TryReadAsync(client, ns, cancellationToken);
        Assert.True(namespaceObject is null || namespaceObject.Metadata.DeletionTimestamp is not null,
                    $"{ns} is still {namespaceObject?.Status?.Phase} after the run's disposal");
    }
}