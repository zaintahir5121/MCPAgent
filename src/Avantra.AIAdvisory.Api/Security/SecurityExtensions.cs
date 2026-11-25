using Avantra.AIAdvisory.Application.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace Avantra.AIAdvisory.Api.Security;

public static class Policies
{
    public const string AdvisoryRead = "Advisory.Read";
    public const string AdvisoryReview = "Advisory.Review";
    public const string KnowledgeAdmin = "Knowledge.Admin";
}

public static class SecurityExtensions
{
    /// <summary>
    /// Auth:Mode = "EntraId" (production: JWT bearer from Microsoft Entra ID, app roles) or "ApiKey" (development/demo).
    /// Both can be enabled together ("EntraId,ApiKey") for service clients that cannot yet obtain tokens.
    /// </summary>
    public static IServiceCollection AddAdvisorySecurity(this IServiceCollection services, IConfiguration config)
    {
        var mode = config["Auth:Mode"] ?? "ApiKey";
        var schemes = new List<string>();
        var auth = services.AddAuthentication();

        if (mode.Contains("EntraId", StringComparison.OrdinalIgnoreCase))
        {
            auth.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var tenant = config["Auth:EntraId:TenantId"];
                o.Authority = $"{config["Auth:EntraId:Instance"] ?? "https://login.microsoftonline.com/"}{tenant}/v2.0";
                o.Audience = config["Auth:EntraId:Audience"];
                o.TokenValidationParameters.RoleClaimType = "roles";
                o.TokenValidationParameters.ValidAudiences = config.GetSection("Auth:EntraId:ValidAudiences").Get<string[]>();
                o.MapInboundClaims = false;
            });
            schemes.Add(JwtBearerDefaults.AuthenticationScheme);
        }
        if (mode.Contains("ApiKey", StringComparison.OrdinalIgnoreCase))
        {
            auth.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationOptions.Scheme,
                o => config.GetSection("Auth:ApiKey").Bind(o));
            schemes.Add(ApiKeyAuthenticationOptions.Scheme);
        }

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(new AuthorizationPolicyBuilder(schemes.ToArray()).RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.AdvisoryRead, p => p.AddAuthenticationSchemes([.. schemes]).RequireAuthenticatedUser()
                .RequireAssertion(c => HasRole(c, Roles.AdvisoryUser) || HasRole(c, Roles.AdvisoryReviewer) || HasRole(c, Roles.KnowledgeAdmin)))
            .AddPolicy(Policies.AdvisoryReview, p => p.AddAuthenticationSchemes([.. schemes]).RequireAssertion(c => HasRole(c, Roles.AdvisoryReviewer)))
            .AddPolicy(Policies.KnowledgeAdmin, p => p.AddAuthenticationSchemes([.. schemes]).RequireAssertion(c => HasRole(c, Roles.KnowledgeAdmin)));

        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, CompositeUserContext>();
        return services;
    }

    private static bool HasRole(AuthorizationHandlerContext c, string role) =>
        c.User.IsInRole(role) || c.User.FindAll("roles").Any(r => r.Value == role);
}
