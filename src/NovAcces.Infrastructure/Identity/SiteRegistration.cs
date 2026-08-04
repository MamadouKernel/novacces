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

    /// <summary>
    /// Suppression logique (archivage) : distincte de <see cref="IsActive"/>.
    /// CONTRAIREMENT à Agent.Matricule ou ApplicationUser.Email, le SiteId
    /// n'est PAS réutilisable après suppression — le schéma PostgreSQL
    /// « site_&lt;id&gt; » et ses données ne sont JAMAIS détruits par cette
    /// action (voir TenantProvisioningService), donc reprovisionner le même
    /// identifiant reconnecterait silencieusement à d'anciennes données. Le
    /// site disparaît seulement des listes (voir /api/admin/overview).
    /// </summary>
    public DateTimeOffset? DeletedAt { get; private set; }
    public string? DeletedBy { get; private set; }

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

    /// <summary>
    /// Suppression logique : n'est permise que sur un site déjà désactivé
    /// (discipline en deux temps, comme pour un agent ou un compte).
    /// </summary>
    public void Delete(DateTimeOffset now, string actor)
    {
        if (IsActive)
            throw new InvalidOperationException("Désactivez le site avant de le supprimer.");

        DeletedAt = now;
        DeletedBy = actor;
    }
}
