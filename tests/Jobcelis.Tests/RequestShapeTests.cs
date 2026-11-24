using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Jobcelis.Tests;

public class RequestShapeTests
{
    /// <summary>Answers every request with 200 {} and records the last one.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }
        public HttpMethod? LastMethod { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastMethod = request.Method;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class Payload
    {
        public string OrderId { get; set; } = "1";
    }

    private static JsonElement Body(RecordingHandler handler)
    {
        using var doc = JsonDocument.Parse(handler.LastBody!);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task RestrictProcessingSendsThePassword()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.RestrictProcessingAsync("secret");

        Assert.Equal("/api/v1/me/restrict", handler.LastPath);
        Assert.Equal("secret", Body(handler).GetProperty("password").GetString());
    }

    [Fact]
    public async Task RetentionPolicyIsWrapped()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.UpdateRetentionPolicyAsync(new { events_days = 30 });

        Assert.Equal(30, Body(handler).GetProperty("retention_policy").GetProperty("events_days").GetInt32());
    }

    [Fact]
    public async Task TestPipelineWrapsThePayloadAndKeepsItsPropertyNames()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.TestPipelineAsync("pipe_1", new Payload());

        Assert.Equal("1", Body(handler).GetProperty("payload").GetProperty("OrderId").GetString());
    }

    [Fact]
    public async Task CreatePipelineSendsTheWebhookId()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.CreatePipelineAsync("p", "wh_1", new[] { "order.created" }, new object[] { new { type = "filter" } });

        var body = Body(handler);
        Assert.Equal("wh_1", body.GetProperty("webhook_id").GetString());
        Assert.Equal("filter", body.GetProperty("steps")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task StatusCallsHealth()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.StatusAsync();

        Assert.Equal("/health", handler.LastPath);
    }

    [Fact]
    public async Task AddMemberDefaultsToViewer()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.AddMemberAsync("prj_1", "a@example.com");

        Assert.Equal("viewer", Body(handler).GetProperty("role").GetString());
    }

    [Fact]
    public async Task PathParametersAreEscaped()
    {
        var handler = new RecordingHandler();
        using var client = new JobcelisClient("k", 0, "https://api.test", new HttpClient(handler));

        await client.GetEventAsync("a/b");

        Assert.Equal("/api/v1/events/a%2Fb", handler.LastPath);
    }

    [Fact]
    public async Task InjectedHttpClientIsNotModifiedOrDisposed()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        using (var client = new JobcelisClient("k", 0, "https://api.test", http))
        {
            await client.HealthAsync();
        }

        Assert.Null(http.BaseAddress);
        // Still usable after the Jobcelis client was disposed
        using var response = await http.GetAsync("https://api.test/other");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
