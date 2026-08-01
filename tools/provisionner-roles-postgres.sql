-- =====================================================================
-- NovAcces — création des rôles PostgreSQL (à exécuter UNE FOIS, en
-- superutilisateur, avant le premier démarrage de l'API sur un serveur).
--
-- POURQUOI DEUX RÔLES
-- L'inaltérabilité des journaux (scan_logs, admin_audit — §8.5 du CDC) est
-- garantie par des triggers PostgreSQL qui s'appliquent à TOUS les rôles,
-- superutilisateur compris. C'est la protection réelle, et elle fonctionne
-- même avec un rôle unique.
--
-- La seconde barrière — retirer DELETE et TRUNCATE au rôle qui exécute l'API —
-- n'a en revanche d'effet QUE si ce rôle n'est pas le propriétaire des tables :
-- un propriétaire conserve des droits implicites sur ses propres objets, un
-- REVOKE ne l'atteint pas. D'où la séparation :
--
--   novacces_owner  — propriétaire des schémas. Sert UNIQUEMENT au DDL
--                     (migrations, provisionnement d'un site, habilitations).
--                     Jamais utilisé pour servir des requêtes.
--   novacces_app    — rôle du runtime. Lit et écrit les données métier, mais
--                     ne peut ni supprimer ni tronquer un journal, même si un
--                     trigger venait à disparaître lors d'une maintenance.
--
-- Cette séparation vaut aussi contre une injection SQL qui passerait toutes les
-- autres barrières : le rôle compromis serait celui qui ne peut pas effacer les
-- traces de l'intrusion.
--
-- USAGE
--   psql -U postgres -f tools/provisionner-roles-postgres.sql
-- puis, avec le compte owner :
--   dotnet ef database update --project src/NovAcces.Infrastructure
--   dotnet run --project src/NovAcces.Api -- provision-site <siteId>
--   dotnet run --project src/NovAcces.Api -- grant-app-role
-- =====================================================================

-- ---------------------------------------------------------------------
-- 1. Les deux rôles. REMPLACER les mots de passe avant exécution.
--    Ne jamais versionner les valeurs réelles : elles vont dans les
--    variables d'environnement du service (ConnectionStrings__Postgres et
--    ConnectionStrings__PostgresOwner).
-- ---------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'novacces_owner') THEN
        CREATE ROLE novacces_owner LOGIN PASSWORD 'CHANGER_CE_MOT_DE_PASSE_OWNER';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'novacces_app') THEN
        CREATE ROLE novacces_app LOGIN PASSWORD 'CHANGER_CE_MOT_DE_PASSE_APP';
    END IF;
END
$$;

-- ---------------------------------------------------------------------
-- 2. La base, propriété du rôle owner.
--    CREATE DATABASE ne peut pas figurer dans un bloc DO : à décommenter
--    si la base n'existe pas encore.
-- ---------------------------------------------------------------------
-- CREATE DATABASE novacces OWNER novacces_owner;

\connect novacces

-- ---------------------------------------------------------------------
-- 3. Cloisonnement du schéma « public ».
--    Par défaut, tout rôle peut y créer des objets. Or le search_path des
--    requêtes métier est « site_<id>, public » : une table homonyme
--    déposée dans public serait consultée dès qu'un schéma de site est
--    incomplet. On retire donc ce droit à tout le monde.
-- ---------------------------------------------------------------------
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO novacces_app;

-- ---------------------------------------------------------------------
-- 4. Le rôle applicatif peut se connecter, rien de plus à ce stade.
--    Ses privilèges sur les données sont posés schéma par schéma par
--    l'API elle-même : provision-site pour un nouveau site, grant-app-role
--    pour (re)synchroniser l'ensemble. Ce sont ces commandes qui retirent
--    DELETE/TRUNCATE sur les journaux.
-- ---------------------------------------------------------------------
GRANT CONNECT ON DATABASE novacces TO novacces_app;
GRANT CONNECT ON DATABASE novacces TO novacces_owner;

-- ---------------------------------------------------------------------
-- 5. Le rôle applicatif ne doit jamais créer d'objets : il ne fait pas de
--    DDL, et ne doit pas pouvoir contourner les triggers en recréant une
--    table de journal sans eux.
-- ---------------------------------------------------------------------
ALTER ROLE novacces_app NOCREATEDB NOCREATEROLE NOSUPERUSER;

-- ---------------------------------------------------------------------
-- 6. Vérification (à lire après exécution) : les deux rôles existent et
--    le rôle applicatif n'est ni superutilisateur ni créateur.
-- ---------------------------------------------------------------------
SELECT rolname, rolsuper, rolcreatedb, rolcreaterole, rolcanlogin
FROM pg_roles
WHERE rolname IN ('novacces_owner', 'novacces_app');
