using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AvantraAgent.Tests;

public class AgentFactory : WebApplicationFactory<Program>
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"avantra-agent-test-{Guid.NewGuid():N}");
    protected virtual IChatClient? Chat => null;
    protected virtual string? AdminKey => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dir);
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Advisory", $"Data Source={Path.Combine(_dir, "test.db")}");
        builder.UseSetting("Ingestion:StagingPath", Path.Combine(_dir, "staging"));
        builder.UseSetting("Ingestion:AllowedUrlHosts:0", "docs.example.com");
        builder.UseSetting("RateLimits:ChatPerMinute", "1000");
        builder.UseSetting("RateLimits:AdminPerMinute", "1000");
        if (AdminKey is not null) builder.UseSetting("Security:AdminApiKey", AdminKey);
        if (Chat is not null)
        {
            builder.UseSetting("AI:Endpoint", "http://fake-model.local/v1");
            builder.UseSetting("AI:ApiKey", "test");
            builder.UseSetting("AI:ChatModel", "fake");
            builder.ConfigureTestServices(s => s.AddSingleton(Chat));
        }
    }

    /// <summary>Client once the startup sync of the Planview knowledge base has finished.</summary>
    public async Task<HttpClient> ReadyClientAsync()
    {
        var c = CreateClient();
        await WaitAsync(async () =>
        {
            var jobs = await c.GetFromJsonAsync<JsonArray>("/api/knowledge-bases/planview/jobs");
            return jobs!.Count > 0 && jobs.All(j => j!["state"]!.GetValue<string>() is not ("Queued" or "Running"));
        });
        return c;
    }

    public static async Task<JsonObject> WaitForJobAsync(HttpClient c, string jobId)
    {
        JsonObject? job = null;
        await WaitAsync(async () =>
        {
            job = await c.GetFromJsonAsync<JsonObject>($"/api/jobs/{jobId}");
            return job!["state"]!.GetValue<string>() is not ("Queued" or "Running");
        });
        return job!;
    }

    public static async Task WaitAsync(Func<Task<bool>> condition, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(200);
        }
        throw new TimeoutException("Condition not met in time.");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public sealed class FakeChat(string reply) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public sealed class GoodAiFactory : AgentFactory { protected override IChatClient Chat => new FakeChat("Planview Portfolios fits your portfolio needs [C1]."); }
public sealed class PricingAiFactory : AgentFactory { protected override IChatClient Chat => new FakeChat("Planview Portfolios costs $50 per user per month."); }
public sealed class AdminKeyFactory : AgentFactory { protected override string AdminKey => "s3cret-admin-key"; }
