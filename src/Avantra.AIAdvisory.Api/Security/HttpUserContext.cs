using System.Security.Claims;
using Avantra.AIAdvisory.Application.Abstractions;

namespace Avantra.AIAdvisory.Api.Security;

/// <summary>Resolves tenant, user and channel from the authenticated principal (Entra ID 'tid'/'oid' claims).</summary>
public sealed class HttpUserContext(IHttpContextAccessor accessor) : IUserContext
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public string TenantId => User?.FindFirst("tid")?.Value
                              ?? User?.FindFirst("http://schemas.microsoft.com/identity/claims/tenantid")?.Value
                              ?? "anonymous";

    public string UserId => User?.FindFirst("oid")?.Value
                            ?? User?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                            ?? User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                            ?? "anonymous";

    public string Channel
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx is null) return "system";
            if (ctx.Request.Path.StartsWithSegments("/mcp")) return "mcp";
            var declared = ctx.Request.Headers["X-Client-Channel"].ToString();
            return string.IsNullOrWhiteSpace(declared) ? "api" : declared[..Math.Min(30, declared.Length)].ToLowerInvariant();
        }
    }

    public bool IsInRole(string role) =>
        User?.IsInRole(role) == true || User?.FindAll("roles").Any(c => c.Value == role) == true;
}

/// <summary>
/// Identity of background jobs (startup knowledge seeding) where there is no HTTP request.
/// Least privilege: it may manage knowledge, but can never review/approve advisory output.
/// </summary>
public sealed class SystemUserContext : IUserContext
{
    public string TenantId => "system";
    public string UserId => "system";
    public string Channel => "system";
    public bool IsInRole(string role) => role == Roles.KnowledgeAdmin;
}

/// <summary>Chooses the HTTP context when there is a request, otherwise the system identity.</summary>
public sealed class CompositeUserContext(IHttpContextAccessor accessor) : IUserContext
{
    private readonly HttpUserContext _http = new(accessor);
    private static readonly SystemUserContext System = new();
    private IUserContext Current => accessor.HttpContext is null ? System : _http;
    public string TenantId => Current.TenantId;
    public string UserId => Current.UserId;
    public string Channel => Current.Channel;
    public bool IsInRole(string role) => Current.IsInRole(role);
}
