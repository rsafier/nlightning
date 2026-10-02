using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Tests.Utils.Mocks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Accounting.Prices;

using Bitcoin.Accounting.Prices;
using Domain.Accounting.Financial;
using Domain.Accounting.Prices;

/// <summary>
/// mempool.space's historical price as a price source (NL-602 A3-T2, D-A11), against a fake handler: the request,
/// the hourly point it answers, the USD conversion through the exchange rates, and failures answering null.
/// </summary>
public class HttpPriceSourceTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 34, 56, TimeSpan.Zero);

    [Fact]
    public async Task Given_AnHourlyPoint_When_APriceIsAsked_Then_ItIsThePointOfThatHourFromOneRequest()
    {
        // Arrange
        var handler = new FakePriceHttpHandler { PriceAt = _ => 86_048.123456789m };
        var source = CreateSource(handler);

        // Act
        var price = await source.GetPriceAsync("usd", s_at, TestContext.Current.CancellationToken);

        // Assert
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://mempool.space/api/v1/historical-price?currency=USD&timestamp={s_at.ToUnixTimeSeconds()}",
                     request.ToString());
        Assert.NotNull(price);
        Assert.Equal("USD", price.Currency);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero), price.Time);
        Assert.Equal(86_048.12345679m, price.Price);
        Assert.Equal(AccountingPriceSource.Http, price.Source);
        Assert.Equal(0, price.Id);
    }

    [Fact]
    public void Given_AnAnswerWithoutTheCurrency_When_Parsed_Then_UsdIsConvertedThroughTheExchangeRate()
    {
        // Arrange
        const string body = "{\"prices\":[{\"time\":1759406400,\"USD\":100000}],\"exchangeRates\":{\"USDBRL\":5.4}}";

        // Act
        var ok = HttpPriceSource.TryParse(body, "BRL", out var time, out var price, out var error);

        // Assert
        Assert.True(ok, error);
        Assert.Equal(1759406400, time.ToUnixTimeSeconds());
        Assert.Equal(540_000m, price);
    }

    [Theory]
    [InlineData("{\"prices\":[]}")]
    [InlineData("{\"prices\":[{\"time\":1759406400,\"USD\":-1}]}")]
    [InlineData("{\"prices\":[{\"time\":1759406400,\"USD\":0}]}")]
    [InlineData("{\"prices\":[{\"USD\":86000}]}")]
    [InlineData("{\"prices\":[{\"time\":5,\"USD\":86000}]}")]
    [InlineData("{\"nope\":1}")]
    [InlineData("[1,2]")]
    [InlineData("not json")]
    public void Given_AnAnswerWithoutAUsablePrice_When_Parsed_Then_ItIsNone(string body)
    {
        // Act
        var ok = HttpPriceSource.TryParse(body, "USD", out _, out _, out var error);

        // Assert
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Given_AServerError_When_APriceIsAsked_Then_ItIsNullNotAnException()
    {
        // Arrange
        var handler = new FakePriceHttpHandler { FailWith = HttpStatusCode.TooManyRequests };
        var source = CreateSource(handler);

        // Act
        var price = await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(price);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Given_NoDataForTheHour_When_APriceIsAsked_Then_ItIsNull()
    {
        // Arrange
        var source = CreateSource(new FakePriceHttpHandler { PriceAt = _ => null });

        // Act & Assert
        Assert.Null(await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AnUnreachableHost_When_APriceIsAsked_Then_ItIsNull()
    {
        // Arrange
        var source = CreateSource(new ThrowingHandler(new HttpRequestException("refused")));

        // Act & Assert
        Assert.Null(await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_OurCancellation_When_APriceIsAsked_Then_ItIsThrown()
    {
        // Arrange
        var source = CreateSource(new FakePriceHttpHandler());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GetPriceAsync("USD", s_at, cancellation.Token));
    }

    [Fact]
    public void Given_AUrlWithAQuery_When_TheRequestIsBuilt_Then_TheParametersAreAppended()
    {
        // Arrange
        var source = new HttpPriceSource(new HttpClient(new FakePriceHttpHandler()),
                                         MsOptions.Create(new AccountingPriceOptions
                                         {
                                             Url = "https://prices.example/api?key=1"
                                         }), NullLogger<HttpPriceSource>.Instance);

        // Act
        var uri = source.BuildRequestUri("EUR", DateTimeOffset.FromUnixTimeSeconds(1759406400));

        // Assert
        Assert.Equal("https://prices.example/api?key=1&currency=EUR&timestamp=1759406400", uri.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_AnAnswerOverTheCap_When_APriceIsAsked_Then_ItIsNullAndNotReadWhole(bool declared)
    {
        // Arrange - NL-678: a valid point padded past 64 KiB, with or without its Content-Length
        var body = "{\"prices\":[{\"time\":1759406400,\"USD\":86000}],\"pad\":\""
                 + new string('x', HttpPriceSource.MaxResponseBytes) + "\"}";
        var handler = new BodyHandler(body, declared);
        var source = CreateSource(handler);

        // Act
        var price = await source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(price);
        Assert.True(handler.BytesServed <= HttpPriceSource.MaxResponseBytes + 1);
    }

    [Fact]
    public async Task Given_AnAnswerWithinTheCap_When_APriceIsAsked_Then_ItIsRead()
    {
        // Arrange
        var handler = new BodyHandler("{\"prices\":[{\"time\":1759406400,\"USD\":86000}]}", false);

        // Act
        var price = await CreateSource(handler).GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(86_000m, price?.Price);
    }

    [Fact]
    public async Task Given_AServerThatStallsTheBody_When_APriceIsAsked_Then_ItTimesOutAsNone()
    {
        // Arrange (NL-732): headers at once, then a body that never ends
        var handler = new StallingBodyHttpHandler();
        var source = new HttpPriceSource(new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(200) },
                                         MsOptions.Create(new AccountingPriceOptions()),
                                         NullLogger<HttpPriceSource>.Instance);

        // Act
        var ask = source.GetPriceAsync("USD", s_at, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(ask, Task.Delay(TimeSpan.FromSeconds(30),
                                                          TestContext.Current.CancellationToken));

        // Assert
        Assert.Same(ask, finished);
        Assert.Null(await ask);
        Assert.True(handler.BodyReadCancelled);
    }

    private static HttpPriceSource CreateSource(HttpMessageHandler handler) =>
        new(new HttpClient(handler), MsOptions.Create(new AccountingPriceOptions()),
            NullLogger<HttpPriceSource>.Instance);

    /// <summary>Answers one body as a stream, counting the bytes the reader took.</summary>
    private sealed class BodyHandler(string body, bool declareLength) : HttpMessageHandler
    {
        private readonly byte[] _bytes = System.Text.Encoding.UTF8.GetBytes(body);
        private CountingStream? _stream;

        public long BytesServed => _stream?.Served ?? 0;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken)
        {
            _stream = new CountingStream(_bytes);
            var content = new StreamContent(_stream);
            content.Headers.ContentLength = declareLength ? _bytes.Length : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Every read path ends in <see cref="Read(byte[], int, int)"/> for a derived memory stream.</summary>
    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public long Served { get; private set; }

        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            Served += read;
            return read;
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }
}