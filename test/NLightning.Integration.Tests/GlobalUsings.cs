global using Moq;
global using Xunit;

// Dumps the cluster harness runs of a failed test (pods, logs, events, storage, node state) into TestResults/cluster;
// tests without a harness run (all but the Cluster/ ones) cost nothing
[assembly: NLightning.Testing.Cluster.Diagnostics.ClusterDiagnostics]