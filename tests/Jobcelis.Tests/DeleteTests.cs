using System.Net;
using System.Text;
using Xunit;

namespace Jobcelis.Tests;

public class DeleteTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static JobcelisClient Client(FakeHandler handler)
        => new("test_key", 0, "https://api.test", new HttpClient(handler));

    [Fact]
    public async Task DeleteThrowsWhenTheServerRejectsIt()
    {
        using var client = Client(new FakeHandler(HttpStatusCode.NotFound, """{"error":"Not found"}"""));

        var ex = await Assert.ThrowsAsync<JobcelisException>(() => client.DeleteWebhookAsync("wh_missing"));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task DeleteThrowsOnForbidden()
    {
        using var client = Client(new FakeHandler(HttpStatusCode.Forbidden, """{"error":"Insufficient scope"}"""));

        var ex = await Assert.ThrowsAsync<JobcelisException>(() => client.DeleteProjectAsync("prj_1"));

        Assert.Equal(403, ex.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"inactive"}""")]
    [InlineData(HttpStatusCode.NoContent, "")]
    public async Task DeleteSucceedsOnSuccessfulResponses(HttpStatusCode status, string body)
    {
        var handler = new FakeHandler(status, body);
        using var client = Client(handler);

        await client.DeleteEventAsync("evt_1");

        Assert.Equal(1, handler.Calls);
    }
}
