# NovAcces

Plateforme de gestion des visiteurs et de contrôle d’accès par QR Code pour Sigasécurité.

## Statut du projet

| Composant | Statut | Périmètre actuel |
|---|---|---|
| API | ✅ Opérationnelle | API .NET 8, Clean Architecture, PostgreSQL multi-tenant, authentification JWT/2FA, RBAC, QR signés ES256, enrôlement terminal par QR temporaire, scans entrée/sortie, mode hors ligne, notifications WhatsApp/email, audit et supervision. |
| Web | 🟡 En finalisation | Portail Blazor pour Hôte, Sûreté, Admin et SuperAdmin : authentification, visites, QR, dashboard, exclusions, utilisateurs, terminaux et traçabilité. |
| Client mobile | 🔌 Séparé | Le client React Native consomme le contrat API. Le projet MAUI historique n’est pas la cible actuelle. |

## API — état de validation

- Build .NET sans erreur ni avertissement.
- 54 tests unitaires réussis.
- 72 tests d’intégration API réussis.
- Isolation stricte par site et contrôle des terminaux multi-sites.
- Policies centralisées pour Hôte, Agent, Sûreté, Admin et SuperAdmin.
- SuperAdmin soumis aux contraintes de site, terminal, 2FA et audit.
- Désactivation logique des comptes : aucune suppression physique.
- La désactivation d’un compte est réservée à l’Admin ou au SuperAdmin.
- Verrouillage temporaire après plusieurs erreurs de PIN agent.
- Traçabilité des requêtes API, des actions sensibles et des connexions temps réel.

## Web — état et travaux restants

### Déjà disponible

- Connexion JWT et 2FA.
- Gestion des demandes de visite et génération des QR.
- Révocation des QR selon les droits.
- Dashboard Sûreté avec présents, journal et export.
- Gestion des exclusions.
- Administration des utilisateurs, sites, agents et terminaux.
- Consultation de la traçabilité par le SuperAdmin.

### À finaliser

- Vérification complète des parcours dans le navigateur avec la maquette validée.
- Tests end-to-end des rôles et des scénarios de site.
- Harmonisation finale des messages d’erreur et des états de chargement.
- Vérification responsive, accessibilité et validation graphique.
- Configuration des environnements de recette et de production.

## Règles de compte

Les comptes ne sont jamais supprimés physiquement.

~~~http
POST /api/admin/users/{id}/deactivate
~~~

Cette opération exige un motif, révoque les sessions, conserve l’historique et est inscrite dans l’audit.

- Un Admin peut désactiver les comptes ordinaires.
- Seul un SuperAdmin peut désactiver un Admin ou un SuperAdmin.
- Le dernier SuperAdmin actif est protégé.
- DELETE /api/auth/me est refusé.

## Démarrage de l’API

Prérequis :

- .NET 8 SDK
- PostgreSQL
- Clés de signature QR ES256
- Variables d’environnement ou dotnet user-secrets pour les secrets

Commandes principales :

~~~bash
dotnet restore
dotnet build
dotnet test
~~~

L’API expose Swagger directement à la racine :

~~~text
/
~~~

Provisionnement d’un site :

~~~bash
dotnet run --project src/NovAcces.Api -- provision-site <site-id>
~~~

### Rôles PostgreSQL (déploiement serveur)

L’inaltérabilité des journaux repose sur des triggers PostgreSQL, qui
s’appliquent à tous les rôles — superutilisateur compris. Cela fonctionne avec
un rôle unique.

Pour ajouter une **seconde barrière** (retirer `DELETE`/`TRUNCATE` sur les
journaux au rôle qui sert les requêtes), il faut deux rôles distincts : un
`REVOKE` reste sans effet sur le propriétaire d’une table. Un rôle unique laisse
donc cette protection inopérante.

~~~bash
psql -U postgres -f tools/provisionner-roles-postgres.sql
~~~

Puis renseigner les deux chaînes de connexion et le nom du rôle applicatif :

~~~text
ConnectionStrings__Postgres       → novacces_app    (runtime)
ConnectionStrings__PostgresOwner  → novacces_owner  (DDL uniquement)
Database__ApplicationRole         → novacces_app
~~~

Enfin, appliquer les habilitations sur le schéma partagé et tous les sites déjà
provisionnés — à rejouer après toute migration qui ajoute des tables :

~~~bash
dotnet run --project src/NovAcces.Api -- grant-app-role
~~~

Ne jamais committer les clés privées, mots de passe, jetons ou chaînes de connexion réelles.

## Architecture

~~~text
src/
├── NovAcces.Domain          # règles métier et entités
├── NovAcces.Application     # cas d’usage et abstractions
├── NovAcces.Infrastructure  # PostgreSQL, Identity, sécurité, notifications
├── NovAcces.Api             # endpoints, hubs et middleware
├── NovAcces.Shared          # contrats et DTO partagés
└── NovAcces.Web             # portail Blazor
~~~

Le client React Native est maintenu séparément et consomme les contrats exposés par l’API.
