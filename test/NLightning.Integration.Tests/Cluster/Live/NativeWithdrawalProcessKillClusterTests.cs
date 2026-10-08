using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NBitcoin;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Onchain.Enums;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.RemoteSigning.Tests;
using NLightning.Signing.Contracts;
using NLightning.Testing.Cluster.Run;
using NLightning.Testing.Cluster.Topology;
using ClusterPoll = NLightning.Testing.Cluster.Poll;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.Integration.Tests.Cluster.Live;

/// <summary>Kills the process owning a real wallet database while Core and its native signer remain alive.</summary>
[Trait("Category", "Cluster")]
public sealed partial class NativeWithdrawalProcessKillClusterTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);
    private const string ChildEnvironment = "NLTG_NATIVE_WITHDRAWAL_CHILD";
    private static void Log(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    [Theory(Explicit = true)]
    [InlineData("intent")]
    [InlineData("prepared")]
    [InlineData("reply")]
    [InlineData("completed")]
    [InlineData("consumed")]
    public Task KilledWithdrawalReplaysOriginalIntentAndCoreAcceptsIdenticalBytes(string killpoint) =>
        RunProofAsync(killpoint, rejection: null);

    [Theory(Explicit = true)]
    [InlineData("synthetic-unknown")]
    [InlineData("lost-receipt")]
    public Task KilledWithdrawalRetainsReservationWhenRecoveryCannotProveItsReceipt(string rejection) =>
        RunProofAsync(rejection == "lost-receipt" ? "completed" : "prepared", rejection);

    private static async Task RunProofAsync(string killpoint, string? rejection)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var run = await TestRun.StartAsync(TestRunOptions.FromEnvironment("native-withdrawal-kill")
            with
        { Quota = NamespaceQuota.Spike, Log = Log }, ct);
        using var topology = await new TopologyBuilder
        { Log = Log, ReadyTimeout = TimeSpan.FromMinutes(4), StepTimeout = s_timeout }
            .AddBitcoinCore("miner").BuildAsync(run, ct);
        var chain = Assert.IsType<BitcoinCoreTopologyChain>(topology.Chain);
        var endpoint = ClusterChainEndpoint.Create(topology.Chain);
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        var directory = Path.Combine(signer.DirectoryPath, "withdrawal-node");
        Directory.CreateDirectory(directory);
        var configuration = new WorkerConfiguration(signer.SocketPath, endpoint.Rpc.Address.ToString(),
            topology.Chain.RpcUser, topology.Chain.RpcPassword, endpoint.ZmqHost,
            endpoint.ZmqBlockPort, endpoint.ZmqTxPort);
        await File.WriteAllTextAsync(Path.Combine(directory, "worker.json"), JsonSerializer.Serialize(configuration), ct);
        var database = Path.Combine(directory, "node.db");
        await using var original = StartWorker(directory, killpoint);
        await original.WaitForFileAsync(Path.Combine(directory, "gate"));
        var before = await ReadSnapshotAsync(database);
        Assert.Equal(killpoint == "consumed" ? 2 : 1, before.WorkflowState);
        Assert.NotEmpty(before.Inputs);
        Assert.Equal(SHA256.HashData(before.Intent), before.Fingerprint);
        using var intent = JsonDocument.Parse(before.Intent);
        Assert.Equal(before.ReservationId, intent.RootElement.GetProperty("ReservationId").GetGuid());
        Assert.Equal(killpoint == "intent" ? 0 : 1, before.Requests.Count);
        Assert.Equal(killpoint == "consumed" ? 1 : 0, before.Broadcasts.Count);
        using var observer = new RemoteSignerConnection(signer.Options());
        byte[]? expectedReceipt = null;
        if (killpoint != "intent")
        {
            var request = Assert.Single(before.Requests);
            Assert.Equal(SignerOperations.SignWalletTransaction3, request.Operation);
            var receipt = observer.Reconcile(WireRequest.Parser.ParseFrom(request.Envelope));
            if (killpoint == "reply")
                receipt = await ClusterPoll.ForAsync(_ => Task.FromResult(
                    observer.Reconcile(WireRequest.Parser.ParseFrom(request.Envelope)) is { Outcome: RequestOutcome.Completed } completed
                        ? completed : null), s_timeout, TimeSpan.FromMilliseconds(25), "signer committed withheld withdrawal reply", ct);
            Assert.Equal(killpoint == "prepared" ? RequestOutcome.NotFound : RequestOutcome.Completed, receipt.Outcome);
            if (receipt.Outcome == RequestOutcome.Completed) expectedReceipt = receipt.Response.Payload.ToByteArray();
            Assert.Equal(killpoint is "prepared" or "reply" ? 1 : killpoint == "completed" ? 2 : 3, request.State);
        }
        // Nothing has reached Core at any of these gates, including the atomic consume-before-publish gate.
        var unsigned = Transaction.Load(intent.RootElement.GetProperty("UnsignedTransaction").GetBytesFromBase64(), Network.RegTest);
        var txid = unsigned.GetHash().ToString();
        Assert.DoesNotContain(txid, await chain.Chain.Rpc.GetRawMempoolAsync(ct));
        original.Kill();
        await original.WaitForExitAsync();
        Assert.NotEqual(0, original.ExitCode);
        var killed = await ReadSnapshotAsync(database);
        Assert.Equal(before.Intent, killed.Intent);
        Assert.Equal(before.ReservationId, killed.ReservationId);
        Assert.Equal(before.Inputs, killed.Inputs);

        if (rejection == "lost-receipt")
        {
            await signer.StopAsync();
            RemoveWalletReceipt(Path.Combine(signer.DirectoryPath, "state"), Assert.Single(before.Requests).RequestId);
            await signer.RestartAsync();
        }
        var historyBeforeRecovery = await File.ReadAllBytesAsync(Path.Combine(signer.DirectoryPath, "state"), ct);
        await using var recovered = StartWorker(directory, rejection ?? "recover");
        if (rejection is not null)
        {
            await recovered.WaitForExitAsync();
            Assert.NotEqual(0, recovered.ExitCode);
            var failure = await ReadSnapshotAsync(database);
            Assert.Equal(before.Intent, failure.Intent);
            Assert.Equal(before.ReservationId, failure.ReservationId);
            Assert.Equal(before.Inputs, failure.Inputs);
            Assert.Equal(3, failure.WorkflowState);
            var refused = Assert.Single(failure.Requests);
            Assert.Equal(Assert.Single(before.Requests).RequestId, refused.RequestId);
            Assert.Equal(Assert.Single(before.Requests).Envelope, refused.Envelope);
            Assert.Equal(4, refused.State);
            Assert.Empty(failure.Broadcasts);
            Assert.Equal(historyBeforeRecovery, await File.ReadAllBytesAsync(Path.Combine(signer.DirectoryPath, "state"), ct));
            Assert.DoesNotContain(txid, await chain.Chain.Rpc.GetRawMempoolAsync(ct));
            Assert.Contains(rejection == "synthetic-unknown" ? "Unknown" : "durable receipt no longer matches", recovered.OutputSnapshot);
            Log($"{run.Namespace}: {rejection} refused withdrawal recovery and retained its original reserved inputs");
            return;
        }

        await recovered.WaitForFileAsync(Path.Combine(directory, "recovered"));
        var after = await ReadSnapshotAsync(database);
        Assert.Equal(before.WorkflowId, after.WorkflowId);
        Assert.Equal(before.Intent, after.Intent);
        Assert.Equal(before.Fingerprint, after.Fingerprint);
        Assert.Equal(before.ReservationId, after.ReservationId);
        Assert.Equal(before.Inputs, after.Inputs);
        Assert.Equal(2, after.WorkflowState);
        var resumed = Assert.Single(after.Requests);
        Assert.Equal(3, resumed.State);
        Assert.NotNull(resumed.Response);
        if (before.Requests.Count != 0)
        {
            Assert.Equal(before.Requests[0].RequestId, resumed.RequestId);
            Assert.Equal(before.Requests[0].Envelope, resumed.Envelope);
            if (before.Requests[0].Response is { } response) Assert.Equal(response, resumed.Response);
        }
        if (expectedReceipt is not null) Assert.Equal(expectedReceipt, resumed.Response);
        var broadcast = Assert.Single(after.Broadcasts);
        var signed = SignerWire.Read<SignedTransaction>(SignerWire.Decode(resumed.Response!)[1]);
        Assert.Equal(signed.RawTxBytes, broadcast.Raw);
        Assert.Equal(unsigned.GetHash(), Transaction.Load(broadcast.Raw, Network.RegTest).GetHash());
        Assert.Equal("killed withdrawal", broadcast.Label);
        Assert.Equal("proof=native-withdrawal", broadcast.Tags);
        Assert.Equal(resumed.Response, observer.Reconcile(WireRequest.Parser.ParseFrom(resumed.Envelope)).Response.Payload.ToByteArray());
        await chain.Chain.WaitForMempoolAsync(txid, ct, s_timeout);
        var raw = await chain.Chain.Rpc.CallAsync("getrawtransaction", new Dictionary<string, object?>
        { ["txid"] = txid, ["verbose"] = false }, ct);
        Assert.Equal(Convert.ToHexString(broadcast.Raw), ((string?)raw)!.ToUpperInvariant());
        var rebroadcast = await chain.Chain.Rpc.CallAsync("sendrawtransaction", new Dictionary<string, object?>
        { ["hexstring"] = Convert.ToHexString(broadcast.Raw) }, ct);
        Assert.Equal(txid, (string?)rebroadcast);
        await topology.MineAndSyncAsync(TopologyDeployer.ConfirmationBlocks, ct);
        await recovered.WaitForExitAsync();
        Assert.True(recovered.ExitCode == 0, recovered.OutputSnapshot);
        var confirmed = await ReadSnapshotAsync(database);
        Assert.Null(confirmed.ReservationId);
        Assert.Empty(confirmed.Inputs);
        Assert.Equal((int)BroadcastState.Confirmed, Assert.Single(confirmed.Broadcasts).State);
        Assert.Equal(broadcast.Raw, Assert.Single(confirmed.Broadcasts).Raw);
        Assert.Equal(resumed.Envelope, Assert.Single(confirmed.Requests).Envelope);
        Assert.Equal(resumed.Response, Assert.Single(confirmed.Requests).Response);
        if (killpoint is "reply" or "completed" or "consumed")
            Assert.Equal(historyBeforeRecovery, await File.ReadAllBytesAsync(Path.Combine(signer.DirectoryPath, "state"), ct));
        Log($"{run.Namespace}: killed at {killpoint}; Core accepted and rebroadcast the exact recovered withdrawal bytes, then confirmed its reserved spend");
    }

    private sealed record WorkerConfiguration(string SocketPath, string RpcUrl, string RpcUser, string RpcPassword,
        string ZmqHost, int ZmqBlockPort, int ZmqTxPort);
    private sealed record SavedRequest(Guid RequestId, uint Operation, byte[] Envelope, int State, byte[]? Response);
    private sealed record SavedBroadcast(byte[] Raw, int State, string? Label, string? Tags);
    private sealed record Snapshot(Guid WorkflowId, int WorkflowState, byte[] Intent, byte[] Fingerprint,
        Guid? ReservationId, string[] Inputs, List<SavedRequest> Requests, List<SavedBroadcast> Broadcasts);

    private static async Task<Snapshot> ReadSnapshotAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var workflowId = Guid.Empty;
        var state = 0;
        byte[] intent = [], fingerprint = [];
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT WorkflowId,State,PublicationIntent,SnapshotFingerprint FROM SigningWorkflows WHERE Kind=8";
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            workflowId = Guid.Parse(reader.GetString(0)); state = reader.GetInt32(1);
            intent = (byte[])reader[2]; fingerprint = (byte[])reader[3];
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        var requests = new List<SavedRequest>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT RequestId,Operation,Envelope,State,Response FROM SigningRequests ORDER BY Ordinal";
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                requests.Add(new SavedRequest(Guid.Parse(reader.GetString(0)), (uint)reader.GetInt64(1),
                    (byte[])reader[2], reader.GetInt32(3), reader.IsDBNull(4) ? null : (byte[])reader[4]));
        }
        Guid? reservation = null;
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT Id FROM FeeInputReservations WHERE Purpose='withdraw'";
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            if (await reader.ReadAsync(TestContext.Current.CancellationToken)) reservation = Guid.Parse(reader.GetString(0));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        }
        var inputs = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT hex(TransactionId)||':'||\"Index\"||':'||hex(ScriptPubKey)||':'||AmountSats FROM FeeInputReservationInputs ORDER BY Position";
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken)) inputs.Add(reader.GetString(0));
        }
        var broadcasts = new List<SavedBroadcast>();
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = "SELECT RawTransaction,State,Label,Tags FROM BroadcastTransactions WHERE Purpose=9";
            await using var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                broadcasts.Add(new SavedBroadcast((byte[])reader[0], reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return new Snapshot(workflowId, state, intent, fingerprint, reservation, inputs.ToArray(), requests, broadcasts);
    }

    private static void RemoveWalletReceipt(string path, Guid requestId)
    {
        var journal = File.ReadAllBytes(path);
        const int headerLength = 8 + 33 + 32;
        using var remaining = new MemoryStream();
        remaining.Write(journal.AsSpan(0, headerLength));
        var removed = 0;
        for (var position = headerLength; position < journal.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(position, sizeof(int)));
            Assert.True(size > 0 && position + sizeof(int) + size <= journal.Length);
            using var record = JsonDocument.Parse(journal.AsMemory(position + sizeof(int), size));
            var root = record.RootElement;
            if (root.GetProperty("Operation").GetUInt32() == SignerOperations.SignWalletTransaction3
                && root.GetProperty("RequestId").GetString() == requestId.ToString("N")) removed++;
            else remaining.Write(journal.AsSpan(position, sizeof(int) + size));
            position += sizeof(int) + size;
        }
        Assert.Equal(1, removed);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        remaining.Position = 0;
        remaining.CopyTo(stream);
        stream.Flush(flushToDisk: true);
    }
}