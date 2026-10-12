global using Xunit;

// Dumps the harness runs of a failed test (pods, logs, events, storage, node state) into TestResults/cluster
[assembly: NLightning.Testing.Cluster.Diagnostics.ClusterDiagnostics]