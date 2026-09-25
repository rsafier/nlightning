using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Transactions.Outputs;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Domain.Protocol.Payloads;
using Domain.Serialization.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Persistence.Enums;
using Infrastructure.Persistence.Providers;
using Infrastructure.Protocol.Factories;
using Infrastructure.Serialization;

/// <summary>
/// An in-memory SQLite database with the real Sqlite migrations applied, plus the real message serializer.
/// </summary>
internal sealed class SqliteDbTestContext : IAsyncDisposable
{
    // BOLT 3 Appendix C keys (valid compressed secp256k1 points)
    internal static readonly CompactPubKey LocalFundingPubKey =
        Convert.FromHexString("023da092f6980e58d2c037173180e9a465476026ee50f96695963e8efe436f54eb");

    internal static readonly CompactPubKey RemoteFundingPubKey =
        Convert.FromHexString("030e9f7b623d2ccc7c9bd44d66d5ce21ce504c0acf6385a132cec6d3c39fa711c1");

    internal static readonly CompactPubKey LocalPaymentBasepoint =
        Convert.FromHexString("034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa");

    internal static readonly CompactPubKey RemotePaymentBasepoint =
        Convert.FromHexString("032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991");

    internal static readonly CompactPubKey RemoteNodeId =
        Convert.FromHexString("02466d7fcae563e5cb09a0d1870bb580344804617879a14949cf22285f1bae3f27");

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _serviceProvider;
    private readonly DbContextOptions<NLightningDbContext> _options;

    public IMessageSerializer MessageSerializer { get; }
    public Sha256 Sha256 { get; } = new();

    private SqliteDbTestContext(SqliteConnection connection, ServiceProvider serviceProvider)
    {
        _connection = connection;
        _serviceProvider = serviceProvider;
        _options = new DbContextOptionsBuilder<NLightningDbContext>()
                  .UseSqlite(connection, o => o.MigrationsAssembly("NLightning.Infrastructure.Persistence.Sqlite"))
                  .Options;
        MessageSerializer = serviceProvider.GetRequiredService<IMessageSerializer>();
    }

    public static async Task<SqliteDbTestContext> CreateAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITlvConverterFactory, TlvConverterFactory>();
        services.AddSerializationInfrastructureServices();

        var testContext = new SqliteDbTestContext(connection, services.BuildServiceProvider());

        await using var context = testContext.CreateDbContext();
        await context.Database.MigrateAsync(cancellationToken);

        return testContext;
    }

    /// <summary>
    /// Every call returns a fresh DbContext over the same in-memory database, so reads never hit the change tracker.
    /// </summary>
    public NLightningDbContext CreateDbContext()
    {
        return new NLightningDbContext(_options, new DatabaseTypeProvider(DatabaseType.Sqlite));
    }

    public static ChannelModel CreateChannel(bool isInitiator, ICollection<Htlc>? localOffered = null,
                                             ICollection<Htlc>? localFulfilled = null,
                                             ICollection<Htlc>? localOld = null,
                                             ICollection<Htlc>? remoteOffered = null,
                                             ICollection<Htlc>? remoteFulfilled = null,
                                             ICollection<Htlc>? remoteOld = null,
                                             WalletAddressModel? changeAddress = null,
                                             ChannelState state = ChannelState.Open,
                                             FeatureSupport useScidAlias = FeatureSupport.No,
                                             ulong localCommitmentNumber = 0, ulong remoteCommitmentNumber = 0)
    {
        var sha256 = new Sha256();
        var config = new ChannelConfig(LightningMoney.Satoshis(1_000), LightningMoney.Satoshis(253),
                                       LightningMoney.MilliSatoshis(1_000), LightningMoney.Satoshis(546), 483,
                                       LightningMoney.Satoshis(100_000), 3, false, LightningMoney.Satoshis(546), 144,
                                       useScidAlias);

        var localKeySet = new ChannelKeySetModel(0, LocalFundingPubKey, LocalFundingPubKey, LocalPaymentBasepoint,
                                                 LocalFundingPubKey, LocalFundingPubKey, LocalFundingPubKey);
        var remoteKeySet = new ChannelKeySetModel(0, RemoteFundingPubKey, RemoteFundingPubKey,
                                                  RemotePaymentBasepoint, RemoteFundingPubKey, RemoteFundingPubKey,
                                                  RemoteFundingPubKey);

        // BOLT 3: the obscuring factor is SHA256(opener payment_basepoint || accepter payment_basepoint)
        var commitmentNumber = isInitiator
                                   ? new CommitmentNumber(LocalPaymentBasepoint, RemotePaymentBasepoint, sha256)
                                   : new CommitmentNumber(RemotePaymentBasepoint, LocalPaymentBasepoint, sha256);

        var fundingTxId = new byte[32];
        fundingTxId[0] = 0xAB;
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), LocalFundingPubKey,
                                                  RemoteFundingPubKey, fundingTxId, 1);

        var channelIdBytes = new byte[32];
        channelIdBytes[31] = isInitiator ? (byte)1 : (byte)2;
        var channelId = new ChannelId(channelIdBytes);

        // In steady state a side's current commitment number equals the number of commitments it has revoked
        return new ChannelModel(config, channelId, commitmentNumber, fundingOutput, isInitiator, null, null,
                                LightningMoney.Satoshis(600_000), localKeySet, 5, localCommitmentNumber,
                                LightningMoney.Satoshis(400_000), remoteKeySet, 7, RemoteNodeId,
                                remoteCommitmentNumber, state, ChannelVersion.V1, localOffered, localFulfilled,
                                localOld, null, remoteOffered, remoteFulfilled, remoteOld,
                                localCommitmentNumber: localCommitmentNumber,
                                remoteCommitmentNumber: remoteCommitmentNumber)
        {
            ChangeAddress = changeAddress
        };
    }

    public static Htlc CreateHtlc(ChannelId channelId, ulong id, HtlcDirection direction, HtlcState state,
                                  CompactSignature? signature = null)
    {
        var paymentHash = new byte[32];
        paymentHash[0] = (byte)(id + 1);
        var onion = new byte[1366];
        var amount = LightningMoney.MilliSatoshis(10_000 + id);
        var payload = new UpdateAddHtlcPayload(amount, channelId, 500 + (uint)id, id, paymentHash, onion);

        return new Htlc(amount, new UpdateAddHtlcMessage(payload), direction, 500 + (uint)id, id, 42 + id,
                        paymentHash, state, null, signature);
    }

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _connection.DisposeAsync();
    }
}