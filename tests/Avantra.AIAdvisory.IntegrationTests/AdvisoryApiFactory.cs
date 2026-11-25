using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Avantra.AIAdvisory.IntegrationTests;

/// <summary>Hosts the real API in-memory with an isolated SQLite file. Optionally swaps in a fake chat model.</summary>
public class AdvisoryApiFactory : WebApplicationFactory<Program>
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"advisory-test-{Guid.NewGuid():N}.db");

    protected virtual IChatClient? ChatClient => null;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Advisory", $"Data Source={_db}");
        // A second tenant, to prove tenant isolation.
        builder.UseSetting("Auth:ApiKey:Clients:3:Name", "other-tenant");
        builder.UseSetting("Auth:ApiKey:Clients:3:Key", "other-tenant-key");
        builder.UseSetting("Auth:ApiKey:Clients:3:TenantId", "fabrikam");
        builder.UseSetting("Auth:ApiKey:Clients:3:Roles:0", "Advisory.User");
        if (ChatClient is not null)
        {
            builder.UseSetting("AI:Endpoint", "http://fake-model.local/v1");
            builder.UseSetting("AI:ApiKey", "test");
            builder.UseSetting("AI:ChatModel", "fake-model");
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton(ChatClient);
            });
        }
    }

    public HttpClient CreateClient(string apiKey)
    {
        var c = CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_db); } catch (IOException) { }
    }
}

/// <summary>Deterministic stand-in for an OpenAI-compatible model.</summary>
public sealed class FakeChatClient(string summary) : IChatClient
{
    public int Calls { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var system = messages.First(m => m.Role == ChatRole.System).Text;
        var text = system.Contains("requirements analyst")
            ? """
              ```json
              {"organization":{"industry":"Financial services","employees":99999,"methodology":"hybrid","existingSystems":["Azure DevOps"]},
               "requirements":[{"statement":"Portfolio prioritisation","capabilityCode":"portfolio-management","priority":"Must","origin":"Stated"},
                               {"statement":"Capacity by skill","capabilityCode":"capacity-planning","priority":"Must","origin":"Stated"},
                               {"statement":"Teleportation","capabilityCode":"teleportation","priority":"Must","origin":"Stated"}],
               "unmapped":[]}
              ```
              """
            : summary;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public sealed class GroundedAiFactory : AdvisoryApiFactory
{
    public FakeChatClient Fake { get; } = new("Planview Portfolios is recommended because it covers the must-have portfolio and capacity requirements [C1]. Licensing must be confirmed with Planview.");
    protected override IChatClient ChatClient => Fake;
}

public sealed class HallucinatingAiFactory : AdvisoryApiFactory
{
    protected override IChatClient ChatClient => new FakeChatClient("Buy Planview Portfolios Platinum Edition for $45 per user per month.");
}
