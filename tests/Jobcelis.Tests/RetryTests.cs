using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Jobcelis.Tests;

public class RetryTests
{
    /// <summary>Answers each request with the next queued response (or exception) and records what was sent.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();

        public List<RecordedRequest> Requests { get; } = new();

        public FakeHandler Respond(HttpStatusCode status, string body = "{}", TimeSpan? retryAfter = null)
        {
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                if (retryAfter != null) response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter.Value);
                return response;
            });
            return this;
        }

        public FakeHandler Fail()
        {
            _responses.Enqueue(() => throw new HttpRequestException("Connection refused"));
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            request.Headers.TryGetValues("X-Idempotency-Key", out var keys);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath,
                keys?.SingleOrDefault(), body));

            if (_responses.Count == 0) throw new InvalidOperationException("No response queued");
            return _responses.Dequeue()();
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string Path, string? IdempotencyKey, string? Body);

    private static JobcelisClient Client(FakeHandler handler, int maxRetries = JobcelisClient.DefaultMaxRetries)
        => new("test_key", maxRetries, "https://api.test", new HttpClient(handler))
        {
            RetryBaseDelay = TimeSpan.Zero
        };

    [Fact]
    public void DefaultsToSixRetries()
    {
        using var client = new JobcelisClient("test_key", httpClient: new HttpClient(new FakeHandler()));
        Assert.Equal(6, client.MaxRetries);
    }

    [Fact]
    public async Task GetIsRetriedOn503UntilItSucceeds()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.OK, """{"id":"evt_1"}""");
        using var client = Client(handler);

        var evt = await client.GetEventAsync("evt_1");

        Assert.Equal("evt_1", evt.GetProperty("id").GetString());
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task GetIsRetriedOnNetworkError()
    {
        var handler = new FakeHandler().Fail().Respond(HttpStatusCode.OK, """{"data":[]}""");
        using var client = Client(handler);

        await client.ListWebhooksAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact(Timeout = 10000)]
    public async Task TooManyRequestsHonoursRetryAfter()
    {
        var handler = new FakeHandler()
            .Respond((HttpStatusCode)429, retryAfter: TimeSpan.Zero)
            .Respond(HttpStatusCode.OK, """{"status":"ok"}""");
        using var client = Client(handler);
        // Without Retry-After the backoff would wait minutes, so finishing proves the header was used.
        client.RetryBaseDelay = TimeSpan.FromMinutes(5);

        await client.StatusAsync();

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendEventReusesTheSameIdempotencyKeyOnEveryAttempt()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.BadGateway)
            .Fail()
            .Respond(HttpStatusCode.Accepted, """{"id":"evt_1"}""");
        using var client = Client(handler);

        await client.SendEventAsync("order.created", new { order_id = "123" });

        Assert.Equal(3, handler.Requests.Count);
        var key = handler.Requests[0].IdempotencyKey;
        Assert.True(Guid.TryParse(key, out _));
        Assert.All(handler.Requests, r => Assert.Equal(key, r.IdempotencyKey));
    }

    [Fact]
    public async Task SendEventKeepsACallerSuppliedIdempotencyKey()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.Accepted, """{"id":"evt_1"}""");
        using var client = Client(handler);

        await client.SendEventAsync("order.created", new { order_id = "123" }, "order-123");

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("order-123", r.IdempotencyKey));
    }

    [Fact]
    public async Task SendEventsAddsIdempotencyKeysOnlyToItemsWithoutOne()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.Accepted, """{"accepted":2}""");
        using var client = Client(handler);

        await client.SendEventsAsync(new object[]
        {
            new { topic = "order.created", payload = new { order_id = "1" } },
            new { topic = "order.created", payload = new { order_id = "2" }, idempotency_key = "mine" },
        });

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(handler.Requests[0].Body, handler.Requests[1].Body);
        using var doc = JsonDocument.Parse(handler.Requests[0].Body!);
        var events = doc.RootElement.GetProperty("events");
        Assert.True(Guid.TryParse(events[0].GetProperty("idempotency_key").GetString(), out _));
        Assert.Equal("mine", events[1].GetProperty("idempotency_key").GetString());
    }

    [Fact]
    public async Task NonEventPostIsNotRetriedOnNetworkError()
    {
        var handler = new FakeHandler().Fail().Respond(HttpStatusCode.Created, """{"id":"wh_1"}""");
        using var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.CreateWebhookAsync("https://example.com/hook"));

        Assert.Single(handler.Requests);
        Assert.Null(handler.Requests[0].IdempotencyKey);
    }

    [Fact]
    public async Task NonEventPostIsRetriedOn503()
    {
        var handler = new FakeHandler()
            .Respond(HttpStatusCode.ServiceUnavailable)
            .Respond(HttpStatusCode.Created, """{"id":"wh_1"}""");
        using var client = Client(handler);

        var webhook = await client.CreateWebhookAsync("https://example.com/hook");

        Assert.Equal("wh_1", webhook.GetProperty("id").GetString());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ZeroMaxRetriesMakesExactlyOneRequest()
    {
        var handler = new FakeHandler().Respond(HttpStatusCode.ServiceUnavailable);
        using var client = Client(handler, maxRetries: 0);

        var ex = await Assert.ThrowsAsync<JobcelisException>(() => client.GetEventAsync("evt_1"));

        Assert.Equal(503, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GivesUpAfterMaxRetriesWithTheUsualError()
    {
        var handler = new FakeHandler();
        for (var i = 0; i < 3; i++) handler.Respond(HttpStatusCode.ServiceUnavailable);
        using var client = Client(handler, maxRetries: 2);

        var ex = await Assert.ThrowsAsync<JobcelisException>(() => client.GetEventAsync("evt_1"));

        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task NonRetryableStatusIsNotRetried(int status)
    {
        var handler = new FakeHandler().Respond((HttpStatusCode)status, """{"error":"nope"}""");
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<JobcelisException>(() => client.GetEventAsync("evt_1"));

        Assert.Equal(status, ex.StatusCode);
        Assert.Single(handler.Requests);
    }
}
