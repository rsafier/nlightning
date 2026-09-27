using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NLightning.Tests.Utils.Channels;

namespace NLightning.Application.Tests.Channels.RoutingPolicies;

using Application.Channels.RoutingPolicies;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.ValueObjects;

/// <summary>
/// Channels, options and an in-memory policy table for the per-channel routing policy tests (wave sp1 lane SP1-G).
/// </summary>
internal static class ChannelPolicyTestKit
{
    internal static readonly CompactPubKey OurNodeId =
        new(Convert.FromHexString("02" + new string('1', 64)));

    internal static readonly CompactPubKey PeerNodeId =
        new(Convert.FromHexString("03" + new string('2', 64)));

    internal static readonly ShortChannelId ShortChannelId = new(500, 3, 1);

    /// <summary>Node:Routing of the tests: 1,000 msat + 1 ppm, delta 40, min 1,000 msat, no maximum.</summary>
    internal static NodeOptions CreateNodeOptions() => new()
    {
        BitcoinNetwork = BitcoinNetwork.Regtest,
        Routing = new RoutingOptions
        {
            FeeBaseMsat = 1_000,
            FeeProportionalMillionths = 1,
            CltvExpiryDelta = 40,
            HtlcMinimumMsat = 1_000
        }
    };

    /// <summary>
    /// An open channel of <paramref name="capacity"/> (1,000,000 sat by default) whose peer requires
    /// <paramref name="peerHtlcMinimumMsat"/> and allows <paramref name="peerMaxInFlightMsat"/> in flight.
    /// </summary>
    internal static ChannelModel CreateChannel(byte id = 1, LightningMoney? capacity = null,
                                               ulong peerHtlcMinimumMsat = 5_000,
                                               ulong peerMaxInFlightMsat = 800_000_000,
                                               ChannelState state = ChannelState.Open)
    {
        var channelIdBytes = new byte[32];
        channelIdBytes[0] = id;
        var channelParams = TestChannelParams.Create(LightningMoney.Zero, LightningMoney.Satoshis(2_500),
                                                     LightningMoney.MilliSatoshis(peerHtlcMinimumMsat),
                                                     LightningMoney.Satoshis(354), 30,
                                                     LightningMoney.MilliSatoshis(peerMaxInFlightMsat), 3, false,
                                                     LightningMoney.Satoshis(354), 144, FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, OurNodeId, OurNodeId, OurNodeId, OurNodeId, OurNodeId, OurNodeId);
        var fundingOutput = new FundingOutputInfo(capacity ?? LightningMoney.Satoshis(1_000_000), OurNodeId,
                                                  PeerNodeId);
        return new ChannelModel(channelParams, new ChannelId(channelIdBytes), null, fundingOutput, true, null, null,
                                LightningMoney.Zero, keySet, 0, 0, LightningMoney.Zero, keySet, 0, PeerNodeId, 0,
                                state, ChannelVersion.V1)
        {
            ShortChannelId = ShortChannelId
        };
    }

    /// <summary>
    /// A service provider whose scoped <see cref="IUnitOfWork"/> reads and writes <paramref name="table"/> (null: a
    /// unit of work without the repository, whose default throws <see cref="NotSupportedException"/>).
    /// </summary>
    internal static ServiceProvider CreateProvider(InMemoryChannelPolicyTable? table)
    {
        var services = new ServiceCollection();
        services.AddScoped<IUnitOfWork>(_ =>
        {
            var unitOfWork = new Mock<IUnitOfWork>();
            if (table is null)
            {
                // The contract's default: a unit of work that does not store overrides
                unitOfWork.Setup(u => u.ChannelPolicyDbRepository)
                          .Throws(new NotSupportedException("This unit of work does not store channel policy "
                                                          + "overrides."));
            }
            else
            {
                var repository = table.CreateRepository();
                unitOfWork.Setup(u => u.ChannelPolicyDbRepository).Returns(repository);
                unitOfWork.Setup(u => u.SaveChangesAsync()).Returns(() =>
                {
                    repository.Commit();
                    return Task.CompletedTask;
                });
            }

            return unitOfWork.Object;
        });
        return services.BuildServiceProvider();
    }

    internal static ChannelPolicyStore CreateStore(IServiceProvider provider, NodeOptions nodeOptions) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), Options.Create(nodeOptions),
            NullLogger<ChannelPolicyStore>.Instance);

    /// <summary>A channel memory repository over <paramref name="channels"/>.</summary>
    internal static Mock<IChannelMemoryRepository> CreateMemory(List<ChannelModel> channels)
    {
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(r => r.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
              .Returns((Func<ChannelModel, bool> predicate) => channels.Where(predicate).ToList());
        memory.Setup(r => r.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
              .Returns(new TryGetChannelCallback((ChannelId channelId, out ChannelModel? channel) =>
               {
                   channel = channels.FirstOrDefault(c => c.ChannelId == channelId);
                   return channel is not null;
               }));
        return memory;
    }

    internal delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);
}

/// <summary>
/// The <c>ChannelPolicies</c> table in memory: each unit of work stages its writes, its save commits them (so a
/// failed or missing save changes nothing), and every unit of work reads the committed rows.
/// </summary>
internal sealed class InMemoryChannelPolicyTable
{
    private readonly Dictionary<ChannelId, ChannelPolicyOverride> _rows = new();
    private readonly Lock _lock = new();

    internal int Saves { get; private set; }
    internal int Reads { get; private set; }

    internal IReadOnlyDictionary<ChannelId, ChannelPolicyOverride> Rows
    {
        get
        {
            lock (_lock)
                return new Dictionary<ChannelId, ChannelPolicyOverride>(_rows);
        }
    }

    internal Repository CreateRepository() => new(this);

    internal sealed class Repository(InMemoryChannelPolicyTable table) : IChannelPolicyDbRepository
    {
        private readonly List<Action> _staged = [];

        public Task<ChannelPolicyOverride?> GetAsync(ChannelId channelId)
        {
            lock (table._lock)
                return Task.FromResult(table._rows.GetValueOrDefault(channelId));
        }

        public Task<IReadOnlyList<ChannelPolicyOverride>> GetAllAsync()
        {
            lock (table._lock)
            {
                table.Reads++;
                return Task.FromResult<IReadOnlyList<ChannelPolicyOverride>>(table._rows.Values.ToList());
            }
        }

        public Task UpsertAsync(ChannelPolicyOverride policyOverride)
        {
            _staged.Add(() => table._rows[policyOverride.ChannelId] = policyOverride);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ChannelId channelId)
        {
            _staged.Add(() => table._rows.Remove(channelId));
            return Task.CompletedTask;
        }

        internal void Commit()
        {
            lock (table._lock)
            {
                foreach (var write in _staged)
                    write();
                if (_staged.Count > 0)
                    table.Saves++;
            }

            _staged.Clear();
        }
    }
}