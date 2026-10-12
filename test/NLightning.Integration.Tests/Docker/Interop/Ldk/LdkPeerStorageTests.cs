using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Docker.Interop.Ldk;

using Application.Node.PeerStorage;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Node.PeerStorage;
using Fixtures;
using Utils;

/// <summary>
/// BOLT 1 peer storage (<c>option_provide_storage</c>) against ldk-server (NL-559): LDK offers the feature but takes
/// at most 1,024 bytes (rust-lightning <c>MAX_PEER_STORAGE_SIZE</c>), so it refuses our full-size blob with a
/// <c>warning</c> on every connection and keeps nothing of ours. We answer the refusal with a blob within the limit
/// it names, LDK keeps that one and hands it back with <c>peer_storage_retrieval</c> after a restart, so a restore
/// can get it back from an LDK peer.
/// </summary>
[Collection(LdkInteropCollection.Name)]
[Trait("Category", LdkInteropCollection.Category)]
public sealed class LdkPeerStorageTests : IAsyncLifetime
{
    private const int TestTimeoutMs = 10 * 60 * 1_000;
    private static readonly TimeSpan s_storageTimeout = TimeSpan.FromSeconds(90);

    private readonly LdkFixture _fixture;
    private LdkChannelSession? _session;

    public LdkPeerStorageTests(LdkFixture fixture, ITestOutputHelper output)
    {
        fixture.SkipIfUnavailable(); // the fixture runs on the cluster only (NL-866)
        _fixture = fixture;
        Console.SetOut(new TestOutputWriter(output));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (TestDiagnostics.CurrentTestFailed)
        {
            if (_session is not null)
                Console.WriteLine($"[ldk] channel at failure: {await _session.DescribeAsync(CancellationToken.None)}");

            await _fixture.DumpLdkLogAsync(400);
        }

        if (_session is not null)
            await _session.DisposeAsync();
    }

    /// <summary>
    /// Over a channel we fund, with peer storage on: our full-size backup is refused by LDK's warning (1,024-byte
    /// limit), the refusal is counted and answered at once with a 1,024-byte backup naming our channel, LDK keeps it
    /// and hands it back after it restarts, and it is the last blob we sent it.
    /// </summary>
    [Fact(Timeout = TestTimeoutMs)]
    public async Task Given_AChannelWithLdk_When_LdkRefusesOurFullSizeBackup_Then_ItKeepsAFittingOneAndHandsItBack()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        _session = await LdkChannelSession.BuildOurFundedAsync(
                       _fixture, "nltg-ldk-peer-storage", LightningMoney.Satoshis(500_000),
                       LightningMoney.Satoshis(100_000), ct,
                       node => node.ConfigureServices = services =>
                       {
                           services.AddPeerStorageServices();
                           services.PostConfigure<NodeOptions>(o => o.Features.OptionProvideStorage =
                                                                        FeatureSupport.Optional);
                           services.Configure<PeerStorageOptions>(o => o.BackupInterval = TimeSpan.FromSeconds(2));
                       });
        var node = _session.Node;
        var storage = node.Services.GetRequiredService<PeerStorageService>();
        var ldkId = _session.LdkPubKey;

        // Assert: LDK refused the full-size blob (65,531 bytes) with the limit its warning names, and we adapted
        var refusal = await Poll.ForAsync(() => storage.GetRefusals().FirstOrDefault(r => r.PeerNodeId == ldkId),
                                          s_storageTimeout, "LDK's refusal of our full-size backup", ct);
        Assert.Equal(1024, refusal.AcceptedLimitBytes);
        Assert.Equal(PeerStorageConstants.MaxBlobLength, refusal.LastRefusedBlobLength);
        Console.WriteLine($"[ldk] refusal: {refusal.Count}x, takes at most {refusal.AcceptedLimitBytes} bytes");

        // ...and a 1,024-byte backup naming our channel went out to it
        await Poll.UntilAsync(() => node.CountLogLines("Sent our peer_storage backup (1024 bytes)") > 0,
                              s_storageTimeout, "our 1,024-byte backup sent to LDK", ct);
        var retrievalBefore = storage.GetRetrievals().FirstOrDefault(r => r.PeerNodeId == ldkId);
        Assert.Null(retrievalBefore);

        // Act: LDK restarts (same data, same port): we reconnect and it hands back what it kept of ours
        await _fixture.RestartLdkAsync(ct);
        await _session.WaitUsableAsync(ct);

        // Assert: LDK stored the fitting backup and handed it back; it is ours, names our channel, is the last one
        // we sent it and fits the limit it enforces
        var retrieval = await Poll.ForAsync(() => storage.GetRetrievals().FirstOrDefault(r => r.PeerNodeId == ldkId),
                                            s_storageTimeout, "LDK's peer_storage_retrieval of our backup", ct);
        Assert.NotNull(retrieval.Contents);
        Assert.Contains(retrieval.Contents.Channels, c => c.ChannelId == _session.ChannelId);
        Assert.Empty(retrieval.UnknownChannels);
        Assert.True(retrieval.MatchesLastSent);
        Assert.InRange(retrieval.BlobLength, 1, 1024);
        Console.WriteLine($"[ldk] LDK handed back our backup from {retrieval.Contents.CreatedAt} "
                        + $"({retrieval.BlobLength} bytes, {retrieval.Contents.Channels.Count} channel(s))");

        // ...and what we keep of LDK's (it sends its own blob to peers that offer storage)
        var kept = await storage.GetStoredBlobAsync(ldkId);
        Console.WriteLine($"[ldk] we keep of LDK: {(kept is null ? "nothing" : $"{kept.Blob.Length} bytes")}");
    }
}