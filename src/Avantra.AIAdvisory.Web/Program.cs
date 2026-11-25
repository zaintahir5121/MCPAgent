using Avantra.AIAdvisory.Web.Components;
using Avantra.AIAdvisory.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient<AdvisoryApiClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["AdvisoryApi:BaseUrl"] ?? "http://localhost:5080/");
    // Demo: service API key. Production: Entra ID (OIDC sign-in + on-behalf-of token to the API).
    if (builder.Configuration["AdvisoryApi:ApiKey"] is { Length: > 0 } key) c.DefaultRequestHeaders.Add("X-Api-Key", key);
    c.DefaultRequestHeaders.Add("X-Client-Channel", "web");
    c.Timeout = TimeSpan.FromSeconds(120);
}).AddStandardResilienceHandler(o =>
{
    o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
    o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(180);
    o.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(180);
    o.Retry.ShouldHandle = args => ValueTask.FromResult(args.Outcome.Result?.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.BadGateway);
});

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
