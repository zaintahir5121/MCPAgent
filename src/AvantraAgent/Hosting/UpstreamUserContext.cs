using Avantra.AIAdvisory.Application.Abstractions;

namespace AvantraAgent.Hosting;

/// <summary>
/// Access management is handled by the existing platform (API gateway / Nexa) in front of this service.
/// The service trusts the identity it is given: optional headers X-Tenant-Id and X-User-Id, otherwise defaults.
/// Do not expose this service directly to the internet without that platform in front of it.
/// </summary>
public sealed class UpstreamUserContext(IHttpContextAccessor accessor) : IUserContext
{
    public string TenantId => Header("X-Tenant-Id") ?? "avantra";
    public string UserId => Header("X-User-Id") ?? (accessor.HttpContext is null ? "system" : "anonymous");
    public string Channel => accessor.HttpContext?.Request.Path.StartsWithSegments("/mcp") == true ? "mcp"
                           : accessor.HttpContext is null ? "system" : "web";
    public bool IsInRole(string role) => true;

    private string? Header(string name)
    {
        var v = accessor.HttpContext?.Request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(v) ? null : v[..Math.Min(100, v.Length)];
    }
}
