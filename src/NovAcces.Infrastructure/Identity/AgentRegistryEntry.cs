namespace NovAcces.Infrastructure.Identity;

/// <summary>
/// Registre global (schéma partagé « identity ») du site qui détient
/// actuellement un matricule d'agent ACTIF. Source de vérité unique pour
/// garantir qu'un même matricule ne peut jamais être actif sur deux sites en
/// même temps — les données de l'agent lui-même (PIN, nom) restent une
/// donnée PAR TENANT (voir Domain.Entities.Agent / AgentDirectory).
///
/// La clé primaire sur Matricule fait porter la garantie d'unicité sur une
/// contrainte PostgreSQL, pas sur une vérification applicative : une
/// réclamation (AgentRegistry.TryClaimAsync) est un simple INSERT qui échoue
/// ou réussit, sans fenêtre entre « vérifier » et « agir » où deux sites
/// pourraient réclamer le même matricule en même temps.
/// </summary>
public sealed class AgentRegistryEntry
{
    public string Matricule { get; private set; } = default!;
    public string SiteId { get; private set; } = default!;
    public DateTimeOffset ClaimedAt { get; private set; }

    private AgentRegistryEntry() { }

    public static AgentRegistryEntry Create(string matricule, string siteId, DateTimeOffset now) => new()
    {
        Matricule = matricule,
        SiteId = siteId,
        ClaimedAt = now,
    };
}
