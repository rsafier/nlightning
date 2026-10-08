using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Channels.ValueObjects;
using Domain.Signing.Recovery;
using Domain.Signing.Vls;
using Infrastructure.Repositories.Database.Node;

public sealed class VlsSigningPersistenceTests
{
    [Fact]
    public async Task AllocationIdentityIsDurableBeforeCompletionAndCannotBeRebound()
    {
        using var database = new SqliteTestDatabase();
        var id = Guid.NewGuid();
        var mapping = Reserved(id);
        using (var context = database.CreateContext())
        {
            await new VlsChannelMappingDbRepository(context).AddAsync(mapping);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var vlsId = Identity(mapping);
        var response = Encoding.UTF8.GetBytes("{\"channel\":\"" + Convert.ToHexString(vlsId).ToLowerInvariant() + "\"}");
        var channel = new ChannelId(Enumerable.Repeat((byte)7, 32).ToArray());
        using (var context = database.CreateContext())
        {
            var repository = new VlsChannelMappingDbRepository(context);
            var reserved = Assert.IsType<VlsChannelMapping>(await repository.GetByKeyIndexAsync(0));
            Assert.Equal(id, reserved.AllocationRequestId);
            Assert.Equal(mapping.AllocationEnvelope, reserved.AllocationEnvelope);
            Assert.Null(reserved.VlsChannelId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.BindChannelAsync(0, channel));
            await repository.CompleteAllocationAsync(0, response, vlsId);
            await repository.BindChannelAsync(0, channel);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using (var context = database.CreateContext())
        {
            var repository = new VlsChannelMappingDbRepository(context);
            var completed = Assert.IsType<VlsChannelMapping>(await repository.GetByChannelIdAsync(channel));
            Assert.Equal(response, completed.AllocationResponse);
            Assert.Equal(vlsId, completed.VlsChannelId);
            await repository.CompleteAllocationAsync(0, response, vlsId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CompleteAllocationAsync(0, "{}"u8.ToArray(), vlsId));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.BindChannelAsync(0,
                new ChannelId(Enumerable.Repeat((byte)8, 32).ToArray())));
            var wrongDbId = vlsId.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(wrongDbId.AsSpan(33), 2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CompleteAllocationAsync(0, response, wrongDbId));
        }
    }

    [Fact]
    public async Task CompetingAllocationCannotReuseDatabaseIdentity()
    {
        using var database = new SqliteTestDatabase();
        using (var context = database.CreateContext())
        {
            await new VlsChannelMappingDbRepository(context).AddAsync(Reserved(Guid.NewGuid()));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var competing = database.CreateContext();
        await new VlsChannelMappingDbRepository(competing).AddAsync(Reserved(Guid.NewGuid()) with { KeyIndex = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => competing.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HolderConsumptionAndReleaseIntentShareOneTransaction()
    {
        using var database = new SqliteTestDatabase();
        var channel = new ChannelId(Enumerable.Repeat((byte)6, 32).ToArray());
        var ticks = DateTime.UtcNow.Ticks;
        var validation = new SigningWorkflow(Guid.NewGuid(), channel, SigningWorkflowKind.ValidateHolder,
            1, 1, new byte[32], (byte[])SqliteDbTestContext.LocalFundingPubKey, "regtest", 2,
            SigningWorkflowState.Pending, ticks, ticks);
        var release = validation with { WorkflowId = Guid.NewGuid(), Kind = SigningWorkflowKind.ReleaseRevoke };
        var request = new SigningRequest(Guid.NewGuid(), validation.WorkflowId, 0, 2007, "{}"u8.ToArray(),
            new byte[32], SigningRequestState.Prepared, null, ticks, ticks);
        using (var context = database.CreateContext())
        {
            var repository = new SigningWorkflowDbRepository(context);
            await repository.AddWorkflowAsync(validation);
            await repository.AddRequestAsync(request);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await repository.UpdateRequestAsync(request with { State = SigningRequestState.Completed, Response = "{}"u8.ToArray() });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using (var context = database.CreateContext())
        {
            var repository = new SigningWorkflowDbRepository(context);
            await repository.ConsumeWorkflowAsync(validation.WorkflowId);
            Assert.Empty(await repository.GetPendingForChannelAsync(channel));
            await repository.AddWorkflowAsync(release);
            Assert.Equal(release.WorkflowId, Assert.Single(await repository.GetPendingForChannelAsync(channel)).WorkflowId);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using (var context = database.CreateContext())
        {
            var repository = new SigningWorkflowDbRepository(context);
            Assert.Equal(SigningWorkflowState.Consumed, (await repository.GetAsync(validation.WorkflowId))!.State);
            Assert.Equal(SigningRequestState.Consumed, Assert.Single(await repository.GetRequestsAsync(validation.WorkflowId)).State);
            Assert.Equal(release.WorkflowId, Assert.Single(await repository.GetPendingForChannelAsync(channel)).WorkflowId);
        }
    }

    private static VlsChannelMapping Reserved(Guid id)
    {
        var ticks = DateTime.UtcNow.Ticks;
        return new(0, 1, SqliteDbTestContext.RemoteNodeId, null,
            (byte[])SqliteDbTestContext.LocalFundingPubKey, "regtest", id,
            Encoding.UTF8.GetBytes($"{{\"id\":\"{id:N}\",\"command\":{{\"op\":\"allocate\",\"peer\":\"{SqliteDbTestContext.RemoteNodeId}\",\"dbid\":1}}}}"),
            null, null, ticks, ticks);
    }
    private static byte[] Identity(VlsChannelMapping mapping)
    {
        var bytes = new byte[41];
        ((byte[])mapping.PeerId).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(33), mapping.DbId);
        return bytes;
    }
}