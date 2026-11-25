using Avantra.AIAdvisory.Domain.Advisory;

namespace Avantra.AIAdvisory.Domain.Governance;

/// <summary>Persisted advisory session. Scoped to a tenant and owner for data isolation.</summary>
public sealed class AdvisorySession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TenantId { get; set; } = string.Empty;
    public string OwnerId { get; set; } = string.Empty;
    public string VendorCode { get; set; } = string.Empty;
    public string Channel { get; set; } = "api";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<SessionTurn> Turns { get; set; } = [];
    /// <summary>Serialized latest <see cref="AdvisoryResult"/>.</summary>
    public string? LatestResultJson { get; set; }
    public ReviewStatus Review { get; set; } = ReviewStatus.Draft;
    public string? ReviewedBy { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewComment { get; set; }
}

public sealed class SessionTurn
{
    public int Ordinal { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string TenantId { get; set; } = string.Empty;
    public string ActorId { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? ResourceId { get; set; }
    public string? Detail { get; set; }
    public string? ModelUsed { get; set; }
    public string? CorrelationId { get; set; }
}
