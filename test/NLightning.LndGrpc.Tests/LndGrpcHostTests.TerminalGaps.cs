using Moq;

namespace NLightning.LndGrpc.Tests;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Payments.Enums;
using Domain.Payments.Models;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;

/// <summary>
/// The calls Lightning Terminal makes through the LNC bridge (NL-1239): <c>FeeReport</c> and the LND semantics its
/// dashboard numbers come from.
/// </summary>
public sealed partial class LndGrpcHostTests
{
    [Fact]
    public async Task Given_OpenChannelsAndForwards_When_FeeReport_Then_PoliciesAndLndsDayWeekMonthSumsComeBack()
    {
        // Arrange: one open channel with an overridden policy, one pending channel (not reported); forwards 1 h,
        // 3 days, 20 days and 40 days ago, and a failed one an hour ago (not counted)
        _channels.Add(CreateChannel(7, ChannelState.Open));
        _channels.Add(CreateChannel(8, ChannelState.V1FundingSigned));
        var channelId = _channels[0].ChannelId;
        _policies.Setup(x => x.GetAsync(channelId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new EffectiveChannelPolicy(channelId, 1_500, 250, 80, 1_000, 990_000_000));
        var now = DateTimeOffset.UtcNow;
        AddForward(now.AddHours(-1), 1_999, ForwardCircuitStatus.Fulfilled);
        AddForward(now.AddHours(-1), 50_000, ForwardCircuitStatus.Failed);
        AddForward(now.AddDays(-3), 5_000, ForwardCircuitStatus.Fulfilled);
        AddForward(now.AddDays(-20), 10_000, ForwardCircuitStatus.Fulfilled);
        AddForward(now.AddDays(-40), 7_000, ForwardCircuitStatus.Fulfilled);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var report = await connection.LightningClient.FeeReportAsync(new FeeReportRequest(), cancellationToken: Ct);

        // Assert: LND sums msat and truncates to sat
        var fees = Assert.Single(report.ChannelFees);
        Assert.Equal(new ShortChannelId(150, 7, 0), new ShortChannelId(fees.ChanId));
        Assert.Equal($"{new TxId(Enumerable.Repeat((byte)7, 32).ToArray())}:0", fees.ChannelPoint);
        Assert.Equal(1_500, fees.BaseFeeMsat);
        Assert.Equal(250, fees.FeePerMil);
        Assert.Equal(0.00025, fees.FeeRate, 12);
        Assert.Equal(0, fees.InboundBaseFeeMsat);
        Assert.Equal(0, fees.InboundFeePerMil);
        Assert.Equal(1ul, report.DayFeeSum);
        Assert.Equal(6ul, report.WeekFeeSum);
        Assert.Equal(16ul, report.MonthFeeSum);
    }

    [Fact]
    public async Task Given_NoPolicyOverride_When_FeeReport_Then_TheNodeWideRoutingPolicyIsReported()
    {
        // Arrange: the policy service answers the node-wide values for a channel without an override
        _channels.Add(CreateChannel(9, ChannelState.Open));
        var channelId = _channels[0].ChannelId;
        _policies.Setup(x => x.GetAsync(channelId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new EffectiveChannelPolicy(channelId, 1_000, 1, 40, 1_000, 990_000_000));
        using var connection = await ConnectAsync(LndMacaroonFiles.AdminFileName);

        // Act
        var report = await connection.LightningClient.FeeReportAsync(new FeeReportRequest(), cancellationToken: Ct);

        // Assert
        var fees = Assert.Single(report.ChannelFees);
        Assert.Equal(1_000, fees.BaseFeeMsat);
        Assert.Equal(1, fees.FeePerMil);
        Assert.Equal(0.000001, fees.FeeRate, 12);
        Assert.Equal(0ul, report.DayFeeSum);
        Assert.Equal(0ul, report.MonthFeeSum);
    }

    [Fact]
    public async Task Given_MoreThanAHundredPayments_When_ListPaymentsWithoutMax_Then_AllComeBackLikeLndsRpc()
    {
        // Arrange
        var start = DateTimeOffset.UtcNow.AddDays(-1);
        for (var i = 1; i <= 150; i++)
            AddSucceededPayment(i, start.AddSeconds(i), LightningMoney.Satoshis(1_000), LightningMoney.Zero);
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var all = await connection.LightningClient.ListPaymentsAsync(
                      new ListPaymentsRequest { IncludeIncomplete = true }, cancellationToken: Ct);
        var reversed = await connection.LightningClient.ListPaymentsAsync(
                           new ListPaymentsRequest { IncludeIncomplete = true, Reversed = true }, cancellationToken: Ct);
        var page = await connection.LightningClient.ListPaymentsAsync(
                       new ListPaymentsRequest { MaxPayments = 100 }, cancellationToken: Ct);

        // Assert
        Assert.Equal(150, all.Payments.Count);
        Assert.Equal(150, reversed.Payments.Count);
        Assert.Equal(1ul, all.FirstIndexOffset);
        Assert.Equal(150ul, all.LastIndexOffset);
        Assert.Equal(100, page.Payments.Count);
    }

    [Fact]
    public async Task Given_PaymentsAndInvoicesOnThreeDays_When_ListedWithCreationDates_Then_OnlyTheRangeComesBack()
    {
        // Arrange
        var day0 = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++)
        {
            AddSucceededPayment(i + 1, day0.AddDays(i), LightningMoney.Satoshis(100), LightningMoney.Zero);
            _invoices.Add(CreateInvoice((byte)(i + 1), day0.AddDays(i), InvoiceStatus.Open));
        }

        var from = (ulong)day0.AddDays(1).ToUnixTimeSeconds();
        var until = from + 3_600;
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var payments = await connection.LightningClient.ListPaymentsAsync(
                           new ListPaymentsRequest
                           {
                               IncludeIncomplete = true,
                               CreationDateStart = from,
                               CreationDateEnd = until
                           }, cancellationToken: Ct);
        var since = await connection.LightningClient.ListPaymentsAsync(
                        new ListPaymentsRequest { CreationDateStart = from }, cancellationToken: Ct);
        var invoices = await connection.LightningClient.ListInvoicesAsync(
                           new ListInvoiceRequest { CreationDateStart = from, CreationDateEnd = until },
                           cancellationToken: Ct);
        var exactEnd = await connection.LightningClient.ListInvoicesAsync(
                           new ListInvoiceRequest { CreationDateEnd = from }, cancellationToken: Ct);

        // Assert: the end is inclusive (LND compares Unix seconds)
        Assert.Equal(2ul, Assert.Single(payments.Payments).PaymentIndex);
        Assert.Equal([2ul, 3ul], since.Payments.Select(p => p.PaymentIndex));
        Assert.Equal(2, (int)Assert.Single(invoices.Invoices).RHash[0]);
        Assert.Equal([1, 2], exactEnd.Invoices.Select(i => (int)i.RHash[0]));
    }

    [Fact]
    public async Task Given_SubSatoshiAmounts_When_ListPayments_Then_SatFieldsAreTruncatedLikeLnd()
    {
        // Arrange: 10,000.999 sat to the payee and 1.6 sat of fees
        AddSucceededPayment(1, DateTimeOffset.UtcNow, LightningMoney.MilliSatoshis(10_000_999),
                            LightningMoney.MilliSatoshis(1_600));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var list = await connection.LightningClient.ListPaymentsAsync(new ListPaymentsRequest(),
                                                                       cancellationToken: Ct);

        // Assert
        var payment = Assert.Single(list.Payments);
        Assert.Equal(10_000, payment.ValueSat);
#pragma warning disable CS0612 // the deprecated sat fields Terminal may still read
        Assert.Equal(10_000, payment.Value);
        Assert.Equal(10_000_999, payment.ValueMsat);
        Assert.Equal(1, payment.FeeSat);
        Assert.Equal(1, payment.Fee);
        Assert.Equal(1_600, payment.FeeMsat);
        var route = Assert.Single(payment.Htlcs).Route;
        Assert.Equal(10_002, route.TotalAmt);
        Assert.Equal(1, route.TotalFees);
#pragma warning restore CS0612
    }

    [Fact]
    public async Task Given_AChannelClosingCooperatively_When_GetInfo_Then_ItCountsAsInactiveLikeListChannels()
    {
        // Arrange: ListChannels lists a ShuttingDown channel as inactive
        _channels.Add(CreateChannel(3, ChannelState.ShuttingDown));
        using var connection = await ConnectAsync(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var info = await connection.LightningClient.GetInfoAsync(new GetInfoRequest(), cancellationToken: Ct);
        var list = await connection.LightningClient.ListChannelsAsync(new ListChannelsRequest(), cancellationToken: Ct);

        // Assert
        Assert.False(Assert.Single(list.Channels).Active);
        Assert.Equal(0u, info.NumActiveChannels);
        Assert.Equal(1u, info.NumInactiveChannels);
    }

    private void AddForward(DateTimeOffset createdAt, ulong feeMsat, ForwardCircuitStatus status)
    {
        var tag = (byte)(_forwards.Count + 1);
        var fulfilled = status == ForwardCircuitStatus.Fulfilled;
        _forwards.Add(ForwardCircuitModel.Restore(new ChannelId(Enumerable.Repeat(tag, 32).ToArray()), tag,
                                                  LightningMoney.MilliSatoshis(2_000_000 + feeMsat), 300,
                                                  new Hash(Enumerable.Repeat(tag, 32).ToArray()),
                                                  new Secret(new byte[32]), new ShortChannelId(170, tag, 0),
                                                  LightningMoney.MilliSatoshis(2_000_000), 250, createdAt, status,
                                                  fulfilled ? new ChannelId(Enumerable.Repeat((byte)0x70, 32).ToArray())
                                                            : (ChannelId?)null,
                                                  fulfilled ? tag : (ulong?)null, createdAt.AddSeconds(2)));
    }

    private void AddSucceededPayment(int index, DateTimeOffset createdAt, LightningMoney amount, LightningMoney fee)
    {
        var hash = new byte[32];
        BitConverter.GetBytes(index).CopyTo(hash, 0);
        var payee = CreatePubKey(9);
        var payment = new PaymentModel(new Hash(hash), "lnbcrt" + index, payee, amount, fee, createdAt,
                                       [
                                           new PaymentHop(payee, new ShortChannelId(150, 9, 0), amount + fee, 200,
                                                          new Secret(new byte[32]))
                                       ])
        {
            PaymentIndex = (ulong)index
        };
        payment.Succeed(new Secret(Enumerable.Repeat((byte)2, 32).ToArray()), createdAt.AddSeconds(1));
        _payments.Add(payment);
    }
}