namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Registre des sites provisionnés (table partagée, schéma « identity »).
/// Distinct de l'existence du schéma PostgreSQL « site_&lt;id&gt; » (source de
/// vérité pour <c>ISiteCatalog.ExistsAsync</c>/<c>GetSiteIdsAsync</c>, qui
/// restent inchangés) : cette table ne porte QUE le statut actif/inactif —
/// désactiver un site ne détruit ni son schéma ni ses données, seulement
/// l'accès (voir TenantResolutionMiddleware). Un site désactivé reste
/// « provisionné » ; il cesse seulement d'être « actif ».
/// </summary>
public sealed class SiteRegistration
{
    public string SiteId { get; private set; } = default!;
    public DateTimeOffset ProvisionedAt { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public string? DeactivatedBy { get; private set; }
    public string? DeactivationReason { get; private set; }

    private SiteRegistration() { }

    public static SiteRegistration Create(string siteId, DateTimeOffset now) => new()
    {
        SiteId = siteId,
        ProvisionedAt = now,
        IsActive = true,
    };

    public void Deactivate(DateTimeOffset now, string actor, string reason)
    {
        IsActive = false;
        DeactivatedAt = now;
        DeactivatedBy = actor;
        DeactivationReason = reason;
    }

    public void Reactivate()
    {
        IsActive = true;
        DeactivatedAt = null;
        DeactivatedBy = null;
        DeactivationReason = null;
    }
}
