namespace NovAcces.Infrastructure.Auth;

/// <summary>
/// Terminaux agents enrôlés, authentifiés par clé API (section "ApiKeys" de la
/// configuration, à tenir hors dépôt : user-secrets / variable d'environnement).
///
/// Incrément 1 : liste en configuration, suffisante pour le site pilote. À
/// remplacer au besoin par une table d'enrôlement (clés hachées, rotation,
/// révocation par terminal) quand le parc de terminaux grandira.
/// </summary>
public sealed class ApiKeyOptions
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    public List<EnrolledTerminal> Terminals { get; set; } = new();
}

public sealed class EnrolledTerminal
{
    /// <summary>Clé secrète présentée par le terminal dans l'en-tête X-Api-Key.</summary>
    public string Key { get; set; } = default!;

    /// <summary>Site auquel ce terminal est rattaché (ex. "sicopa").</summary>
    public string SiteId { get; set; } = default!;

    /// <summary>Libellé lisible pour la journalisation (ex. "Poste Entrée A").</summary>
    public string Label { get; set; } = "";
}
