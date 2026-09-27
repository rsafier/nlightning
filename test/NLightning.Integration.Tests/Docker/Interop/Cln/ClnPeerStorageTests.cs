using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Cln;

using Application.Node.PeerStorage;
using Domain.Money;
using Domain.Node.PeerStorage;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 1 peer storage (<c>option_provide_storage</c>, NL-010) against Core Lightning, which offers it by default
/// (<c>--list-features-only</c> of v26.06.8 lists <c>option_provide_storage/odd</c>): over a channel we fund, CLN keeps
/// our encrypted backup blob and hands it back with <c>peer_storage_retrieval</c> after a reconnection, and we keep
/// CLN's blob and hand it back to CLN the same way.
/// </summary>
[Collection(ClnInteropCollection.Name)]
[Trait("Category", ClnInteropCollection.Category)]
public sealed class ClnPeerStorageTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 6 * 60 * 1_000;
    private static readonly TimeSpan s_storageTimeout = TimeSpan.FromSeconds(90);

    private readonly ClnFixture _fixture;
    private ClnChannelSession? _session;

    public ClnPeerStorageTests(ClnFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Console.WriteLine("[cln] CLN peer storage log lines:\n"
                        + await _fixture.Cln.GetLogLinesAsync("storage", CancellationToken.None, 60));
        Console.WriteLine("[cln] CLN unusual/broken log lines so far:\n"
                        + await _fixture.Cln.GetLogLinesAsync(string.Empty, CancellationToken.None, 60, "unusual"));
        if (_session is not null)
        {
            if (DockerDiagnostics.CurrentTestFailed)
                Console.WriteLine($"[cln] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await _session.DisposeAsync();
        }
    }

    /// <summary>
    /// We fund a channel to CLN with peer storage on: CLN stores our backup (it names our channel) and returns it
    /// after CLN drops the connection and we reconnect; we store CLN's blob and send it back to CLN after init.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AChannelWithCln_When_Reconnected_Then_BothSidesHandBackTheOthersBlob()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _session = await ClnChannelSession.BuildOurFundedAsync(
                       _fixture, "nltg-peer-storage", LightningMoney.Satoshis(500_000),
                       LightningMoney.Satoshis(100_000), ct,
                       node => node.ConfigureServices = services =>
                       {
                           services.AddPeerStorageServices();
                           services.Configure<PeerStorageOptions>(o => o.BackupInterval = TimeSpan.FromSeconds(2));
                       });
        var node = _session.Node;
        var storage = node.Services.GetRequiredService<PeerStorageService>();

        // CLN's blob reaches us (CLN sends its backup to peers that offer storage)
        var clnBlob = await Poll.ForAsync(() => storage.GetStoredBlobAsync(_session.ClnPubKey), s_storageTimeout,
                                          "CLN's peer_storage kept by us", ct);
        Console.WriteLine($"[cln] we keep CLN's blob: {clnBlob.Blob.Length} bytes");

        // CLN keeps our backup (the round sends it once the channel exists; CLN's chanbackup plugin stores peers'
        // blobs in its datastore under the peer's node id), and it is one of ours naming the channel
        var blobProvider = node.Services.GetRequiredService<IPeerBackupBlobProvider>();
        var keptByCln = await Poll.ForAsync(async () =>
        {
            var datastore = await _session.Cln.CallAsync("listdatastore", ct,
                                                         ("key", new JsonArray("chanbackup", "peers",
                                                                               node.NodeIdHex)));
            var hex = datastore["datastore"]?.AsArray().FirstOrDefault()?["hex"]?.GetValue<string>();
            return hex is null ? null : Convert.FromHexString(hex);
        }, s_storageTimeout, "our peer_storage kept by CLN", ct);
        var keptContents = await blobProvider.TryReadBlobAsync(keptByCln, ct);
        Assert.NotNull(keptContents);
        Assert.Contains(keptContents.Channels, c => c.ChannelId == _session.ChannelId);
        Assert.Equal(PeerStorageConstants.MaxBlobLength, keptByCln.Length);
        var retrievalsBefore = CountLines(await _session.Cln.GetLogLinesAsync("peer_in WIRE_PEER_STORAGE_RETRIEVAL",
                                                                              ct, 500));

        // Act: CLN drops the connection; we reconnect
        await _session.Cln.CallAsync("disconnect", ct, ("id", node.NodeIdHex), ("force", true));
        await _session.WaitUsableAsync(ct);

        // Assert: CLN handed our backup back, and it names our channel
        var clnPubKey = _session.ClnPubKey;
        var retrieval = await Poll.ForAsync(
                            () => storage.GetRetrievals().FirstOrDefault(r => r.PeerNodeId == clnPubKey),
                            s_storageTimeout, "CLN's peer_storage_retrieval of our backup", ct);
        Assert.NotNull(retrieval.Contents);
        Assert.Contains(retrieval.Contents.Channels, c => c.ChannelId == _session.ChannelId);
        Assert.Empty(retrieval.UnknownChannels);
        Assert.True(retrieval.MatchesLastSent);
        Assert.Equal(PeerStorageConstants.MaxBlobLength, retrieval.BlobLength);
        Assert.Equal(keptContents.CreatedAt, retrieval.Contents.CreatedAt);

        // ...and CLN got its own blob back from us
        await Poll.UntilAsync(async () => CountLines(await _session.Cln.GetLogLinesAsync(
                                                         "peer_in WIRE_PEER_STORAGE_RETRIEVAL", ct, 500))
                                        > retrievalsBefore, s_storageTimeout, "CLN received our retrieval", ct);

        // Act: our restart (CLN's blob comes from our database now)
        var retrievalsBeforeRestart = CountLines(await _session.Cln.GetLogLinesAsync(
                                                     "peer_in WIRE_PEER_STORAGE_RETRIEVAL", ct, 500));
        await _session.StopNodeAsync();
        await _session.StartNodeAsync(ct);
        await _session.WaitUsableAsync(ct);

        // Assert: both sides hand the other's blob back again
        var restarted = node.Services.GetRequiredService<PeerStorageService>();
        Assert.NotSame(storage, restarted);
        var afterRestart = await Poll.ForAsync(
                               () => restarted.GetRetrievals().FirstOrDefault(r => r.PeerNodeId == clnPubKey),
                               s_storageTimeout, "CLN's peer_storage_retrieval after our restart", ct);
        Assert.Contains(afterRestart.Contents!.Channels, c => c.ChannelId == _session.ChannelId);
        Assert.Empty(afterRestart.UnknownChannels);
        await Poll.UntilAsync(async () => CountLines(await _session.Cln.GetLogLinesAsync(
                                                         "peer_in WIRE_PEER_STORAGE_RETRIEVAL", ct, 500))
                                        > retrievalsBeforeRestart, s_storageTimeout,
                              "CLN received our retrieval after our restart", ct);
        Assert.NotNull(await restarted.GetStoredBlobAsync(clnPubKey));

        var clnLog = await _session.Cln.GetLogLinesAsync("storage", ct, 200);
        Console.WriteLine($"[cln] CLN storage log:\n{clnLog}");
    }

    private static int CountLines(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
}