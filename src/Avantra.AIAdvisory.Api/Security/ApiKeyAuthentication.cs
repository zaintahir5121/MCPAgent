using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Avantra.AIAdvisory.Api.Security;

public sealed class ApiKeyClient
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string TenantId { get; set; } = "avantra";
    public string[] Roles { get; set; } = [];
}

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public List<ApiKeyClient> Clients { get; set; } = [];
}

/// <summary>
/// Development / demo / service-to-service fallback authentication. Production uses Microsoft Entra ID
/// (OAuth 2.0 bearer tokens with app roles); API keys are stored in Key Vault and compared in constant time.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? provided = Request.Headers[ApiKeyAuthenticationOptions.HeaderName];
        if (string.IsNullOrEmpty(provided))
        {
            var auth = Request.Headers.Authorization.ToString();
            if (auth.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase)) provided = auth[7..].Trim();
        }
        if (string.IsNullOrEmpty(provided)) return Task.FromResult(AuthenticateResult.NoResult());

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var client = Options.Clients.FirstOrDefault(c => c.Key.Length > 0 &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(c.Key), providedBytes));
        if (client is null) return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"apikey:{client.Name}"),
            new(ClaimTypes.Name, client.Name),
            new("tid", client.TenantId),
        };
        claims.AddRange(client.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
