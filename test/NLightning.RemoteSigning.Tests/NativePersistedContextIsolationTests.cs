using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Daemon.Extensions;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Constants;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Payments.Enums;
using NLightning.Domain.Payments.Models;
using NLightning.Domain.Persistence.Interfaces;
using NLightning.Domain.Signing;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Signing.Contracts;
using StoredRequest = NLightning.Domain.Signing.Recovery.SigningRequest;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativePersistedContextIsolationTests
{
    [Fact]
    public async Task CollidingPrivateRecordsRemainIsolatedAcrossNodeAndSignerRestart()
    {
        await using var supervisor = new HostedNativeSignerSupervisor();
        var first = await supervisor.AddAsync("persisted-first", new string('a', 64));
        var second = await supervisor.AddAsync("persisted-second", new string('b', 64));
        var firstDatabase = Path.Combine(first.DirectoryPath, "node.db");
        var secondDatabase = Path.Combine(second.DirectoryPath, "node.db");
        var hash = new Hash(SHA256.HashData("colliding-payment"u8));
        var workflowId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var reservationId = Guid.NewGuid();
        var channelId = new ChannelId(SHA256.HashData("colliding-local-channel"u8));
        byte[] secondSnapshot;
        byte[] firstReceipt;
        byte[] secondReceipt;
        WireRequest firstRequest;
        WireRequest secondRequest;

        await using (var a = await NodeComposition.CreateAsync(first, firstDatabase))
        await using (var b = await NodeComposition.CreateAsync(second, secondDatabase))
        {
            Assert.NotEqual(a.Connection.Context, b.Connection.Context);
            Assert.NotEqual(a.Keys.GetNodePubKey(), b.Keys.GetNodePubKey());
            // Advance only B so identical request IDs produce distinguishable genuine allocation receipts.
            b.Keys.ReserveChannelKeyIndex();
            firstRequest = a.Connection.PrepareForContext(SignerOperations.ReserveChannelKeyIndex);
            secondRequest = b.Connection.PrepareForContext(SignerOperations.ReserveChannelKeyIndex);
            firstRequest.RequestId = requestId.ToString("N");
            secondRequest.RequestId = requestId.ToString("N");
            Assert.Throws<ArgumentException>(() => b.Connection.Execute(firstRequest));
            Assert.Throws<ArgumentException>(() => a.Connection.Reconcile(secondRequest));

            await SaveRecordsAsync(a, hash, workflowId, reservationId, channelId, firstRequest, "first", 111_123);
            await SaveRecordsAsync(b, hash, workflowId, reservationId, channelId, secondRequest, "second", 222_321);
            firstReceipt = await ExecuteAndSaveReceiptAsync(a, workflowId, firstRequest);
            secondReceipt = await ExecuteAndSaveReceiptAsync(b, workflowId, secondRequest);
            Assert.NotEqual(firstReceipt, secondReceipt);

            var aRecords = await ReadAsync(a, hash, workflowId, reservationId);
            var bRecords = await ReadAsync(b, hash, workflowId, reservationId);
            Assert.Equal(aRecords.Invoice.PaymentHash, bRecords.Invoice.PaymentHash);
            Assert.Equal(aRecords.Invoice.AddIndex, bRecords.Invoice.AddIndex);
            Assert.NotNull(aRecords.Invoice.AddIndex);
            Assert.Equal(aRecords.Payment.PaymentHash, bRecords.Payment.PaymentHash);
            Assert.Equal(aRecords.Payment.PaymentIndex, bRecords.Payment.PaymentIndex);
            Assert.NotNull(aRecords.Payment.PaymentIndex);
            Assert.Equal(aRecords.Reservation!.Id, bRecords.Reservation!.Id);
            Assert.Equal(aRecords.Reservation.Inputs[0].TxId, bRecords.Reservation.Inputs[0].TxId);
            Assert.Equal(aRecords.Reservation.Inputs[0].Index, bRecords.Reservation.Inputs[0].Index);
            Assert.Equal(aRecords.Workflow.WorkflowId, bRecords.Workflow.WorkflowId);
            Assert.Equal(aRecords.Workflow.ChannelId, bRecords.Workflow.ChannelId);
            Assert.Equal(aRecords.Request.RequestId, bRecords.Request.RequestId);
            Assert.Equal(aRecords.Address.Index, bRecords.Address.Index);
            Assert.NotEqual(aRecords.Address.Address, bRecords.Address.Address);
            Assert.NotEqual(aRecords.Invoice.PaymentSecret, bRecords.Invoice.PaymentSecret);
            secondSnapshot = await b.SnapshotPersistedRowsAsync();

            await a.InScopeAsync(async unit =>
            {
                var invoice = (await unit.InvoiceDbRepository.GetByPaymentHashAsync(hash))!;
                invoice.Cancel();
                await unit.InvoiceDbRepository.UpdateAsync(invoice);
                var payment = (await unit.PaymentDbRepository.GetByPaymentHashAsync(hash))!;
                payment.Fail(null, null, "first-only-failure", DateTimeOffset.UtcNow);
                await unit.PaymentDbRepository.UpdateAsync(payment);
                Assert.True(await unit.FeeInputReservationDbRepository.DeleteAsync(reservationId));
                await unit.WalletAddressesDbRepository.ReserveAsync(aRecords.Address);
                await unit.SigningWorkflowDbRepository.ConsumeWorkflowAsync(workflowId);
                await unit.SaveChangesAsync();
                return 0;
            });
            Assert.Equal(secondSnapshot, await b.SnapshotPersistedRowsAsync());

            // A valid B signer cannot adopt A's existing private database enrollment.
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await using var rejected = await NodeComposition.CreateAsync(second, firstDatabase);
            });
            Assert.Equal(secondSnapshot, await b.SnapshotPersistedRowsAsync());
        }

        await first.RestartAsync();
        await second.RestartAsync();
        await using var restartedA = await NodeComposition.CreateAsync(first, firstDatabase);
        await using var restartedB = await NodeComposition.CreateAsync(second, secondDatabase);
        var restoredA = await ReadAsync(restartedA, hash, workflowId, reservationId);
        Assert.Equal(InvoiceStatus.Canceled, restoredA.Invoice.Status);
        Assert.Equal(PaymentStatus.Failed, restoredA.Payment.Status);
        Assert.Equal("first-only-failure", restoredA.Payment.FailureReason);
        Assert.Null(restoredA.Reservation);
        Assert.True(restoredA.Address.IsReserved);
        Assert.Equal(SigningWorkflowState.Consumed, restoredA.Workflow.State);
        Assert.Equal(SigningRequestState.Consumed, restoredA.Request.State);
        Assert.Equal(firstReceipt, restoredA.Request.Response);
        Assert.Equal(secondSnapshot, await restartedB.SnapshotPersistedRowsAsync());
        Assert.Equal(firstReceipt, restartedA.Connection.Reconcile(firstRequest).Response.Payload.ToByteArray());
        Assert.Equal(secondReceipt, restartedB.Connection.Reconcile(secondRequest).Response.Payload.ToByteArray());
        Assert.Throws<ArgumentException>(() => restartedB.Connection.Reconcile(firstRequest));
    }

    private static async Task SaveRecordsAsync(NodeComposition node, Hash hash, Guid workflowId, Guid reservationId,
                                              ChannelId channelId, WireRequest request, string name, ulong amountMsat)
    {
        var pubKey = new PubKey((byte[])node.Keys.GetWalletPublicKey(0, false, AddressType.P2Wpkh));
        var address = new WalletAddressModel(AddressType.P2Wpkh, 0, false,
            pubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString());
        var input = new WalletInput(new TxId(SHA256.HashData("colliding-reserved-outpoint"u8)), 0,
            LightningMoney.Satoshis(50_000), AddressType.P2Wpkh, new BitcoinScript(pubKey.WitHash.ScriptPubKey.ToBytes()),
            WalletWeights.P2WpkhInputWeight);
        var ticks = DateTime.UtcNow.Ticks;
        var workflow = new SigningWorkflow(workflowId, channelId, SigningWorkflowKind.Opening, 0, 0,
            SHA256.HashData(request.Payload.Span), ((byte[])node.Keys.GetNodePubKey()).ToArray(), "regtest", 1,
            SigningWorkflowState.Pending, ticks, ticks);
        var stored = new StoredRequest(Guid.ParseExact(request.RequestId, "N"), workflowId, 0, request.Operation,
            request.ToByteArray(), ArgumentFingerprint(request), SigningRequestState.Prepared, null, ticks, ticks);
        await node.InScopeAsync(async unit =>
        {
            // Repository isolation records do not claim a paid invoice or confirmed wallet funding.
            await unit.InvoiceDbRepository.AddAsync(new InvoiceModel(hash, null,
                new Secret(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))),
                LightningMoney.MilliSatoshis(amountMsat), name, "isolation-invoice-" + name,
                DateTimeOffset.UtcNow, 3600, 40));
            await unit.PaymentDbRepository.AddAsync(new PaymentModel(hash, null, node.Keys.GetNodePubKey(),
                LightningMoney.MilliSatoshis(amountMsat), LightningMoney.MilliSatoshis(123), DateTimeOffset.UtcNow));
            unit.WalletAddressesDbRepository.AddRange([address]);
            unit.FeeInputReservationDbRepository.Add(new FeeInputReservation(reservationId, name, [input],
                LightningMoney.Satoshis(1_000), LightningMoney.Zero, null), DateTimeOffset.UtcNow);
            await unit.SigningWorkflowDbRepository.AddWorkflowAsync(workflow);
            await unit.SigningWorkflowDbRepository.AddRequestAsync(stored);
            await unit.SaveChangesAsync();
            return 0;
        });
    }

    private static async Task<byte[]> ExecuteAndSaveReceiptAsync(NodeComposition node, Guid workflowId,
                                                                WireRequest request)
    {
        node.Connection.Execute(request);
        var reconciled = node.Connection.Reconcile(request);
        Assert.Equal(RequestOutcome.Completed, reconciled.Outcome);
        var receipt = reconciled.Response.Payload.ToByteArray();
        await node.InScopeAsync(async unit =>
        {
            var saved = Assert.Single(await unit.SigningWorkflowDbRepository.GetRequestsAsync(workflowId));
            Assert.Equal(request.ToByteArray(), saved.Envelope);
            await unit.SigningWorkflowDbRepository.UpdateRequestAsync(saved with
            {
                State = SigningRequestState.Completed,
                Response = receipt,
                UpdatedAtTicks = DateTime.UtcNow.Ticks
            });
            await unit.SaveChangesAsync();
            return 0;
        });
        return receipt;
    }

    private static byte[] ArgumentFingerprint(WireRequest request)
    {
        var material = new byte[sizeof(uint) + request.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, request.Operation);
        request.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        return SHA256.HashData(material);
    }

    private static Task<PrivateRecords> ReadAsync(NodeComposition node, Hash hash, Guid workflowId, Guid reservationId)
        => node.InScopeAsync(async unit => new PrivateRecords(
            (await unit.InvoiceDbRepository.GetByPaymentHashAsync(hash))!,
            (await unit.PaymentDbRepository.GetByPaymentHashAsync(hash))!,
            await unit.FeeInputReservationDbRepository.GetByIdAsync(reservationId),
            (await unit.SigningWorkflowDbRepository.GetAsync(workflowId))!,
            Assert.Single(await unit.SigningWorkflowDbRepository.GetRequestsAsync(workflowId)),
            Assert.Single(unit.WalletAddressesDbRepository.GetAllAddresses())));

    private sealed record PrivateRecords(InvoiceModel Invoice, PaymentModel Payment, FeeInputReservation? Reservation,
                                         SigningWorkflow Workflow, StoredRequest Request, WalletAddressModel Address);

    private sealed class NodeComposition(RemoteSignerConnection connection, RemoteSecureKeyManager keys,
                                         ServiceProvider services) : IAsyncDisposable
    {
        public RemoteSignerConnection Connection { get; } = connection;
        public RemoteSecureKeyManager Keys { get; } = keys;

        public static async Task<NodeComposition> CreateAsync(HostedNativeSignerSupervisor.HostedSigner signer,
                                                              string databasePath)
        {
            var options = signer.Options();
            var connection = new RemoteSignerConnection(options);
            var keys = new RemoteSecureKeyManager(connection);
            ServiceProvider? provider = null;
            try
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Node:Network"] = options.Network,
                    ["Database:Provider"] = "Sqlite",
                    ["Database:ConnectionString"] = $"Data Source={databasePath};Pooling=False",
                    ["Database:UseCompiledModel"] = "true",
                    ["Signing:Mode"] = "RemoteNative",
                    ["Signing:NodeId"] = options.NodeId,
                    ["Signing:OwnerId"] = options.OwnerId,
                    ["Signing:SignerId"] = options.SignerId,
                    ["Signing:SocketPath"] = options.SocketPath,
                    ["Signing:AuthTokenFile"] = Path.Combine(signer.DirectoryPath, "token")
                }).Build();
                var collection = new ServiceCollection();
                collection.AddLogging();
                collection.AddNltgNodeServices(configuration, keys, connection);
                provider = collection.BuildServiceProvider();
                await using var scope = provider.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
                await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
                await NodeSigningEnrollmentExtensions.ValidateNodeSigningEnrollmentAsync(database,
                    scope.ServiceProvider.GetRequiredService<NodeSigningContext>(), TestContext.Current.CancellationToken);
                return new NodeComposition(connection, keys, provider);
            }
            catch
            {
                if (provider is not null) await provider.DisposeAsync();
                connection.Dispose();
                throw;
            }
        }

        public async Task<T> InScopeAsync<T>(Func<IUnitOfWork, Task<T>> action)
        {
            await using var scope = services.CreateAsyncScope();
            return await action(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        }

        public async Task<byte[]> SnapshotPersistedRowsAsync()
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
            await using var transaction = await database.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var connection = database.Database.GetDbConnection();
            var tables = database.Model.GetEntityTypes().Select(entity => entity.GetTableName()).OfType<string>()
                                 .Distinct().Order(StringComparer.Ordinal).ToArray();
            using var snapshot = new MemoryStream();
            using var writer = new BinaryWriter(snapshot, Encoding.UTF8, true);
            writer.Write(tables.Length);
            foreach (var table in tables)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                var columns = Enumerable.Range(0, reader.FieldCount).OrderBy(reader.GetName, StringComparer.Ordinal).ToArray();
                writer.Write(table);
                writer.Write(columns.Length);
                foreach (var column in columns) writer.Write(reader.GetName(column));
                var rows = new List<byte[]>();
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    using var row = new MemoryStream();
                    using var rowWriter = new BinaryWriter(row, Encoding.UTF8, true);
                    foreach (var column in columns)
                    {
                        // Read provider scalars directly: no domain serialization can omit or reinterpret safety fields.
                        switch (reader.GetValue(column))
                        {
                            case DBNull _:
                                rowWriter.Write((byte)0);
                                break;
                            case byte[] bytes:
                                rowWriter.Write((byte)1);
                                rowWriter.Write(bytes.Length);
                                rowWriter.Write(bytes);
                                break;
                            case string value:
                                rowWriter.Write((byte)2);
                                rowWriter.Write(value);
                                break;
                            case long value:
                                rowWriter.Write((byte)3);
                                rowWriter.Write(value);
                                break;
                            case double value:
                                rowWriter.Write((byte)4);
                                rowWriter.Write(BitConverter.DoubleToInt64Bits(value));
                                break;
                            default:
                                throw new InvalidOperationException("Unsupported SQLite snapshot scalar type: "
                                    + reader.GetValue(column).GetType().FullName);
                        }
                    }
                    rowWriter.Flush();
                    rows.Add(row.ToArray());
                }
                rows.Sort((left, right) => left.AsSpan().SequenceCompareTo(right));
                writer.Write(rows.Count);
                foreach (var row in rows) { writer.Write(row.Length); writer.Write(row); }
            }
            writer.Flush();
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
            return snapshot.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            Connection.Dispose();
        }
    }
}