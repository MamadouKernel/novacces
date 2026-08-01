using System.Globalization;

namespace NovAcces.Web.Services;

/// <summary>
/// Empreinte des ressources statiques, apposée en query string sur les liens
/// CSS (voir App.razor).
///
/// Pourquoi : après un déploiement, un navigateur qui a mis en cache l'ancien
/// <c>app.css</c> continue de l'appliquer. Le HTML est alors à jour mais pas les
/// styles, ce qui produit un rendu incohérent — des éléments neufs héritant de
/// règles anciennes — sans le moindre message d'erreur. En faisant varier l'URL
/// à chaque version du fichier, on force la relecture, et uniquement quand le
/// fichier a réellement changé (une valeur aléatoire au démarrage casserait le
/// cache inutilement à chaque redémarrage).
/// </summary>
public static class AssetVersion
{
    /// <summary>Empreinte de wwwroot/app.css, calculée une seule fois au démarrage.</summary>
    public static string Css { get; private set; } = "0";

    /// <summary>
    /// À appeler au démarrage avec <c>app.Environment.WebRootPath</c>. On ne
    /// peut pas dériver le chemin de <c>AppContext.BaseDirectory</c> : celui-ci
    /// désigne <c>bin/</c>, où wwwroot n'est pas copié — les ressources
    /// statiques sont servies depuis le projet via le manifeste d'assets.
    /// </summary>
    public static void Initialize(string? webRootPath) => Css = Compute(webRootPath, "app.css");

    private static string Compute(string? webRootPath, string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(webRootPath))
                return "0";

            var path = Path.Combine(webRootPath, relativePath);
            if (!File.Exists(path))
                return "0";

            var info = new FileInfo(path);

            // Date de dernière écriture + taille : suffisant pour distinguer
            // deux versions, et sans coût de lecture du fichier au démarrage.
            return (info.LastWriteTimeUtc.Ticks ^ info.Length)
                .ToString("x", CultureInfo.InvariantCulture);
        }
        catch
        {
            // Une empreinte indisponible ne doit jamais empêcher le portail de
            // servir : on retombe sur une valeur fixe (comportement d'avant).
            return "0";
        }
    }
}
