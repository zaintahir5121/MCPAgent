using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Avantra.AdvisoryAgent.Tests;

public class AgentFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"advisory-agent-test-{Guid.NewGuid():N}.db");
    protected virtual IChatClient? Chat => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Advisory", $"Data Source={_db}");
        builder.UseSetting("Connectors:Planview:RunIngestionOnStartup", "false");
        builder.UseSetting("Connectors:Planview:IngestionIntervalHours", "0");
        if (Chat is not null)
        {
            builder.UseSetting("AI:Endpoint", "http://fake-model.local/v1");
            builder.UseSetting("AI:ApiKey", "test");
            builder.UseSetting("AI:ChatModel", "fake");
            builder.ConfigureTestServices(s => s.AddSingleton(Chat));
        }
    }

    private bool _ingested;
    public async Task<HttpClient> ReadyClientAsync()
    {
        var c = CreateClient();
        if (!_ingested) { (await c.PostAsync("/api/connectors/planview/ingest", null)).EnsureSuccessStatusCode(); _ingested = true; }
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_db); } catch (IOException) { }
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
