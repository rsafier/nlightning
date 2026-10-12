using MessagePack;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Persistence.Interfaces;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// <c>accounting classify</c> over IPC (ClientCommand 45, action 5, NL-602 A3-T3): a rule with every match field, a
/// test with its lines, an override and an unclassified page cross the envelope both ways; bad fields and a missing
/// classify part are <c>invalid_operation</c>.
/// </summary>
public class AccountingClassifyIpcHandlerTests
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 30, 15, 123, TimeSpan.Zero);
    private static readonly ChannelId s_channel = new(Enumerable.Repeat((byte)0x33, 32).ToArray());
    private static readonly Hash s_offer = new(Enumerable.Repeat((byte)0x44, 32).ToArray());

    private static readonly CompactPubKey s_peer =
        new(Convert.FromHexString("02eec7245d6b7d2ccb30380bfbe2a3648cd7a942653f5aa340edcea1f283686619"));

    private readonly Mock<IAccountingClassificationAdmin> _admin = new();
    private AccountingClassifyClientRequest? _received;

    public AccountingClassifyIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _admin.Setup(a => a.HandleAsync(It.IsAny<AccountingClassifyClientRequest>(), It.IsAny<CancellationToken>()))
              .Callback<AccountingClassifyClientRequest, CancellationToken>((r, _) => _received = r)
              .ReturnsAsync((AccountingClassifyClientRequest r, CancellationToken _) => Answer(r));
    }

    [Fact]
    public async Task Given_ARuleWithEveryMatchField_When_Added_Then_ItCrossesTheEnvelopeBothWays()
    {
        // Arrange
        var rule = new AccountingRuleIpcModel
        {
            Priority = -5,
            Kinds = [(int)AccountingEventKind.InvoiceSettled, (int)AccountingEventKind.PushReceived],
            LabelPattern = "^inv-[0-9]+$",
            TagKey = "customer",
            TagValue = "acme*",
            Counterparty = s_peer.ToString(),
            OfferId = s_offer.ToString().ToUpperInvariant(),
            ChannelId = s_channel.ToString(),
            TargetAccount = " income:consulting ",
            Enabled = false,
            Description = "consulting"
        };

        // Act
        var response = await ClassifyAsync(new AccountingClassifyIpcRequest
        {
            Action = (int)AccountingClassifyAction.RuleAdd,
            Rule = rule
        });

        // Assert
        var sent = _received!.Rule!;
        Assert.Equal(AccountingClassifyAction.RuleAdd, _received.Action);
        Assert.Equal(-5, sent.Priority);
        Assert.Equal([AccountingEventKind.InvoiceSettled, AccountingEventKind.PushReceived], sent.Kinds);
        Assert.Equal("^inv-[0-9]+$", sent.LabelPattern);
        Assert.Equal("customer", sent.TagKey);
        Assert.Equal("acme*", sent.TagValue);
        Assert.Equal(s_peer, sent.Counterparty);
        Assert.Equal(s_offer, sent.OfferId);
        Assert.Equal(s_channel, sent.ChannelId);
        Assert.Equal("income:consulting", sent.TargetAccount);
        Assert.False(sent.Enabled);
        Assert.Equal("consulting", sent.Description);

        var classify = response.Classify!;
        Assert.Equal((int)AccountingAdminAction.Classify, response.Action);
        Assert.Equal((int)AccountingProfile.Financial, classify.Profile);
        var back = Assert.Single(classify.Rules!);
        Assert.Equal(42, back.Id);
        Assert.Equal(s_peer.ToString(), back.Counterparty);
        Assert.Equal(s_offer.ToString(), back.OfferId);
        Assert.Equal(s_channel.ToString(), back.ChannelId);
        Assert.Equal(s_at.ToUnixTimeMilliseconds(), back.CreatedAtUnixMilliseconds);
        Assert.Equal(["check this"], classify.Warnings);
    }

    [Fact]
    public async Task Given_ATestAnOverrideAndAnUnclassifiedPage_When_Answered_Then_EveryFieldCrossesTheEnvelope()
    {
        // Act
        var test = (await ClassifyAsync(new AccountingClassifyIpcRequest
        {
            Action = (int)AccountingClassifyAction.RuleTest,
            EventKey = " inv:1 "
        })).Classify!;
        var set = (await ClassifyAsync(new AccountingClassifyIpcRequest
        {
            Action = (int)AccountingClassifyAction.Set,
            EventKey = "inv:1",
            Account = "income:gifts",
            Note = "birthday"
        })).Classify!;
        var page = (await ClassifyAsync(new AccountingClassifyIpcRequest
        {
            Action = (int)AccountingClassifyAction.ListUnclassified,
            AfterLedgerSeq = 7,
            Limit = 2
        })).Classify!;

        // Assert
        Assert.Equal(7, _received!.AfterLedgerSeq);
        Assert.Equal(2, _received.Limit);
        var result = test.Test!;
        Assert.Equal(3, result.LedgerSeq);
        Assert.Equal("inv:1", result.EventKey);
        Assert.Equal((int)AccountingEventKind.InvoiceSettled, result.Kind);
        Assert.Equal("income:consulting", result.Account);
        Assert.Equal((int)AccountingClassificationSource.Rule, result.Source);
        Assert.Equal(9, result.RuleId);
        Assert.Equal([4L], result.TimedOutRuleIds);
        Assert.True(result.CandidateMatches);
        Assert.Equal(["assets:lightning:channels", "income:consulting"], result.Lines.Select(l => l.AccountName));
        Assert.Equal((int)AccountRole.Received, result.Lines[1].Role);
        Assert.Equal("income:gifts", set.Override!.Account);
        Assert.Equal("birthday", set.Override.Note);
        var item = Assert.Single(page.Unclassified!);
        Assert.Equal("push:1", item.EventKey);
        Assert.Equal(-2_000, item.AmountMsat);
        Assert.Equal("gift", item.Label);
        Assert.Equal(11, page.NextAfter);
        Assert.True(page.HasMore);
        Assert.Equal(4, page.Scanned);
    }

    [Theory]
    [InlineData(99, null, "Unknown classify action")]
    [InlineData((int)AccountingClassifyAction.RuleAdd, "zz", "Invalid channel id")]
    public async Task Given_ABadClassifyRequest_When_Sent_Then_InvalidOperation(int action, string? channel,
                                                                                string expected)
    {
        // Arrange
        var request = new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Classify,
            Classify = new AccountingClassifyIpcRequest
            {
                Action = action,
                Rule = new AccountingRuleIpcModel { TargetAccount = "income:x", ChannelId = channel }
            }
        };

        // Act
        var response = await GetHandler().HandleAsync(CreateEnvelope(Serialize(request)),
                                                      TestContext.Current.CancellationToken);

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public async Task Given_TheClassifyActionWithoutItsPart_When_Sent_Then_InvalidOperation()
    {
        // Act
        var response = await GetHandler().HandleAsync(
                           CreateEnvelope(Serialize(new AccountingAdminIpcRequest
                           {
                               Action = (int)AccountingAdminAction.Classify
                           })), TestContext.Current.CancellationToken);

        // Assert
        var error = ReadError(response);
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains("classify action is required", error.Message);
    }

    [Fact]
    public void Given_TheWireValues_When_Read_Then_TheyAreStable()
    {
        // Assert (append-only, never renumber)
        Assert.Equal(5, (int)AccountingAdminAction.Classify);
        Assert.Equal(1, (int)AccountingClassifyAction.RuleAdd);
        Assert.Equal(4, (int)AccountingClassifyAction.RuleTest);
        Assert.Equal(7, (int)AccountingClassifyAction.Set);
        Assert.Equal(10, (int)AccountingClassifyAction.ListUnclassified);
        Assert.Equal(0, (int)AccountingProfile.Operational);
        Assert.Equal(1, (int)AccountingProfile.Financial);
    }

    private static AccountingClassifyClientResponse Answer(AccountingClassifyClientRequest request)
    {
        var response = new AccountingClassifyClientResponse(request.Action, AccountingProfile.Financial)
        {
            Warnings = ["check this"]
        };
        return request.Action switch
        {
            AccountingClassifyAction.RuleAdd => response with { Rules = [request.Rule! with { Id = 42, CreatedAt = s_at }] },
            AccountingClassifyAction.RuleTest => response with
            {
                Test = new AccountingClassifyTestResult(
                    3, request.EventKey!, AccountingEventKind.InvoiceSettled, s_at,
                    new AccountingClassification("income:consulting", AccountingClassificationSource.Rule, 9,
                                                 AccountRole.Received, false, "rule 9")
                    {
                        TimedOutRuleIds = [4]
                    },
                    [
                        new AccountingPosting(AccountRole.Channels, 1_000) { AccountName = "assets:lightning:channels" },
                        new AccountingPosting(AccountRole.Received, -1_000) { AccountName = "income:consulting" }
                    ])
                { CandidateMatches = true }
            },
            AccountingClassifyAction.Set => response with
            {
                Override = new AccountingOverride(request.EventKey!, request.Account!, request.Note, s_at)
            },
            _ => response with
            {
                Unclassified = new AccountingUnclassifiedPage(
                    [
                        new AccountingUnclassifiedItem(11, "push:1", AccountingEventKind.PushReceived, s_at, -2_000,
                                                       "income:unclassified", "default for PushReceived", "gift")
                    ], 11, true, 4)
            }
        };
    }

    private async Task<AccountingAdminIpcResponse> ClassifyAsync(AccountingClassifyIpcRequest classify)
    {
        var request = new AccountingAdminIpcRequest
        {
            Action = (int)AccountingAdminAction.Classify,
            Classify = classify
        };
        var response = await GetHandler().HandleAsync(CreateEnvelope(Serialize(request)),
                                                      TestContext.Current.CancellationToken);
        Assert.Equal(IpcEnvelopeKind.Response, response.Kind);
        return MessagePackSerializer.Deserialize<AccountingAdminIpcResponse>(response.Payload, s_options,
                                                                            TestContext.Current.CancellationToken);
    }

    private IIpcCommandHandler GetHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new Mock<IUnitOfWork>().Object);
        services.AddSingleton(_admin.Object);
        services.AddAccountingIpcServices();
        return services.BuildServiceProvider().GetServices<IIpcCommandHandler>()
                       .Single(h => h.Command == ClientCommand.AccountingAdmin);
    }

    private static byte[] Serialize<T>(T request) =>
        MessagePackSerializer.Serialize(request, s_options, TestContext.Current.CancellationToken);

    private static IpcError ReadError(IpcEnvelope response)
    {
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private static IpcEnvelope CreateEnvelope(byte[] payload) => new()
    {
        Version = 1,
        Command = ClientCommand.AccountingAdmin,
        CorrelationId = Guid.NewGuid(),
        Kind = IpcEnvelopeKind.Request,
        Payload = payload
    };
}