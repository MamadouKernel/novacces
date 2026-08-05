#!/usr/bin/env bash
# Redéploiement NovAcces/SigasAcces sur le VPS. À lancer depuis la racine du
# dépôt cloné sur le serveur (là où vivent docker-compose.yml et .env).
#
# Enchaîne, dans l'ordre : récupération du code, reconstruction/redémarrage
# des conteneurs, migrations du schéma partagé "identity", puis migrations de
# CHAQUE site déjà provisionné (idempotent — voir TenantProvisioningService),
# et enfin l'habilitation du rôle applicatif si Database:ApplicationRole est
# configuré. Voir docs/deploiement.md et docs/guide-premier-deploiement.md.
#
# Raison d'être : le 04/08/2026, une migration EF ajoutée au code n'avait pas
# été rejouée après déploiement — l'API plantait (500) sur toute tentative de
# connexion faute d'une colonne absente en base. Ce script existe pour que ce
# ne soit plus un geste manuel à se rappeler.

set -euo pipefail
cd "$(dirname "$0")"

if [ ! -f .env ]; then
    echo "Erreur : .env introuvable (voir .env.example)." >&2
    exit 1
fi

# Restreint la lecture/écriture du fichier de secrets au seul propriétaire
# (audit du 05/08/2026, finding F4) : sans cela, les permissions dépendent de
# l'umask du compte qui a créé le fichier, pas d'une garantie du dépôt.
chmod 600 .env

# Ce script n'a besoin QUE de ces 3 valeurs simples, jamais des secrets multi-
# lignes (clés PEM, etc.) — docker compose les lit lui-même nativement pour
# docker-compose.yml, avec son propre parseur qui gère les valeurs multi-
# lignes entre guillemets. Ici, à l'inverse, on va chercher CHAQUE variable
# par son nom exact, ligne par ligne, sans jamais essayer d'interpréter le
# fichier .env dans son ensemble : ni `source .env` (bash l'exécuterait comme
# du code shell — un espace non protégé par des guillemets, ex. un nom
# d'affichage "Mamadou Konate", casse le script), ni une boucle de lecture
# générique (elle trébuche sur les lignes de base64 d'une clé PEM multi-
# lignes, qui contiennent elles-mêmes des "="). Un simple grep ciblé sur la
# ligne exacte évite ces deux pièges d'un coup.
read_env_var() {
    local line
    line=$(grep -m1 -E "^${1}=" .env) || {
        echo "Erreur : ${1} absent de .env." >&2; exit 1; }
    printf '%s' "${line#*=}"
}
POSTGRES_USER=$(read_env_var POSTGRES_USER)
DOMAIN=$(read_env_var DOMAIN)
API_DOMAIN=$(read_env_var API_DOMAIN)

echo "==> git pull (échoue si le dépôt local a divergé, plutôt qu'un merge silencieux)"
git pull --ff-only

echo "==> docker compose up -d --build"
docker compose up -d --build

echo "==> Attente que l'API soit prête..."
api_ready=0
for _ in $(seq 1 30); do
    if docker compose exec -T api curl -fsS http://localhost:8080/health >/dev/null 2>&1; then
        api_ready=1
        break
    fi
    sleep 2
done
if [ "$api_ready" -ne 1 ]; then
    echo "Erreur : l'API n'est pas devenue saine à temps — voir 'docker compose logs api'." >&2
    exit 1
fi

echo "==> Migrations (schéma partagé identity)"
docker compose exec -T api dotnet NovAcces.Api.dll migrate

echo "==> Migrations de chaque site déjà provisionné"
site_ids=$(docker compose exec -T postgres psql -U "${POSTGRES_USER}" -d novacces -tAc \
    "SELECT schema_name FROM information_schema.schemata WHERE schema_name LIKE 'site\_%'" \
    | tr -d '\r' | sed 's/^site_//')

if [ -z "$site_ids" ]; then
    echo "   (aucun site provisionné pour l'instant — normal au tout premier déploiement)"
else
    for site in $site_ids; do
        echo "   -> $site"
        docker compose exec -T api dotnet NovAcces.Api.dll provision-site "$site"
    done
fi

echo "==> Habilitation du rôle applicatif (sans effet si Database:ApplicationRole n'est pas configuré)"
docker compose exec -T api dotnet NovAcces.Api.dll grant-app-role || true

echo "==> Vérification"
curl -fsS "https://${API_DOMAIN}/health" && echo
curl -fsS -o /dev/null -w "Web : %{http_code}\n" "https://${DOMAIN}"

echo "==> Déploiement terminé."
