using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;

namespace NLightning.Integration.Tests.Docker.Onchain.Cheater;

using Application.Channels.Safety;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;
using Utils;

/// <summary>
/// A local commitment of an NLightning node, fully signed for broadcast but kept aside (BOLT 5 plan §5 Proof O5 (b)).
/// </summary>
/// <param name="Number">Its commitment number.</param>
/// <param name="Transaction">The signed commitment (both signatures in the funding witness).</param>
public sealed record CapturedCommitment(ulong Number, Transaction Transaction);

/// <summary>
/// The deterministic cheater of Proof O5 (b): signs a node's <b>current</b> local commitment for broadcast with a
/// test-only second <see cref="LocalLightningSigner"/> built on the node's key manager (a copy of its seed), outside the
/// node's DI graph, so the invariant S1 mark of that signature never reaches the node's own signer and the channel can
/// move on (the node's own signer revokes the captured commitment normally afterwards).
/// </summary>
/// <remarks>
/// The commitment is built from the node's persisted state (the snapshot's <c>LocalCommit</c> spec, number and the
/// peer's signature, read from its database) exactly as the production fail-the-channel path builds it
/// (<see cref="LocalCommitmentBroadcastBuilder"/>). Nothing is published: the test sends the raw bytes later.
/// </remarks>
public static class StaleCommitmentCapture
{
    /// <summary>Signs <paramref name="cheater"/>'s current local commitment of <paramref name="channelId"/>.</summary>
    public static async Task<CapturedCommitment> CaptureAsync(NLightningTestNode cheater, ChannelId channelId)
    {
        ArgumentNullException.ThrowIfNull(cheater);

        using var scope = cheater.Services.CreateScope();
        var channel = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ChannelDbRepository
                                 .GetByIdAsync(channelId)
                   ?? throw new InvalidOperationException($"The cheater has no channel {channelId}");
        Assert.NotNull(channel.Commitments);

        var services = cheater.Services;
        var keyDerivationService = services.GetRequiredService<IKeyDerivationService>();
        var nodeOptions = services.GetRequiredService<IOptions<NodeOptions>>().Value;
        var copySigner = new LocalLightningSigner(services.GetRequiredService<IFundingOutputBuilder>(),
                                                  keyDerivationService, NullLogger<LocalLightningSigner>.Instance,
                                                  nodeOptions, cheater.SecureKeyManager,
                                                  services.GetRequiredService<IUtxoMemoryRepository>());
        copySigner.RegisterChannel(channelId, channel.GetSigningInfo());

        var modelFactory = new CommitmentTransactionModelFactory(
            new CommitmentKeyDerivationService(keyDerivationService, copySigner), copySigner);
        var builder = new LocalCommitmentBroadcastBuilder(modelFactory,
                                                          services.GetRequiredService<ICommitmentTransactionBuilder>(),
                                                          copySigner);
        var signed = builder.Build(channel);
        Console.WriteLine(
            $"[cheater] captured local commitment {signed.CommitmentNumber} of {channelId}: {signed.Transaction.TxId}");
        return new CapturedCommitment(signed.CommitmentNumber,
                                      Transaction.Load(signed.Transaction.RawTxBytes, Network.RegTest));
    }
}