namespace NLightning.Testing.Cluster.Topology;

/// <summary>
/// The fail-fast wiring of a wave of parallel deployments: the first failure cancels the others, and the error thrown
/// is the first real one, not one of the cancellations it caused.
/// </summary>
internal static class FailFast
{
    /// <summary>Cancels <paramref name="abort"/> when <paramref name="task"/> faults (a disposed source is ignored).</summary>
    public static void CancelOnFault(Task task, CancellationTokenSource abort) =>
        _ = task.ContinueWith(_ =>
                              {
                                  try
                                  {
                                      abort.Cancel();
                                  }
                                  catch (ObjectDisposedException)
                                  {
                                      // The wave is over
                                  }
                              }, CancellationToken.None,
                              TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                              TaskScheduler.Default);

    /// <summary>Waits until every task has finished, whatever its outcome.</summary>
    public static Task SettleAsync(IEnumerable<Task> tasks) =>
        Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, CancellationToken.None,
                                                      TaskContinuationOptions.ExecuteSynchronously,
                                                      TaskScheduler.Default)));

    /// <summary>The first error of <paramref name="tasks"/> that is not a cancellation, or null.</summary>
    public static Exception? FirstRealError(IEnumerable<Task> tasks) =>
        tasks.Where(t => t.IsFaulted)
             .SelectMany(t => t.Exception!.InnerExceptions)
             .FirstOrDefault(e => e is not OperationCanceledException);
}