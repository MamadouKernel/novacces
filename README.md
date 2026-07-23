# NovAcces — Scaffold initial (Jalon 1)

Système de gestion des visiteurs par QR Code sécurisé pour Sigasécurité.
Ce scaffold pose le **cœur critique** du système — celui qui ne doit jamais
être improvisé : signature cryptographique, anti-rejeu, cloisonnement
multi-tenant. Tout le reste (Blazor, MAUI, WhatsApp) se construit dessus.

## Ce qui est réellement implémenté et testé dans ce scaffold

| Brique | État |
|---|---|
| Logique métier (`Visit.Scan`) : fenêtre, anti-rejeu, cycle entrée/sortie, exclusion, dépassements, expiration 30 jours | ✅ Implémentée + 19 tests unitaires |
| Signature ES256 des QR et des listes hors ligne, avec vérification d'expiration | ✅ Implémentée + 6 tests unitaires |
| Orchestration du scan (`ScanQrHandler`), révocation (`RevokeVisitHandler`) | ✅ Implémentée + 2 tests unitaires |
| Multi-tenant : résolution + schéma PostgreSQL par site | ✅ Implémentée (à valider par `dotnet build` + tests d'intégration réels) |
| API minimale (`/api/scan`, `/api/visits`, `/api/visits/{id}/revoke`) | ✅ Squelette fonctionnel |
| Notifications WhatsApp/email | ❌ Aucune interface — Jalon 2 |
| Notification temps réel de l'hôte (SignalR) | ❌ Aucun hook — Jalon 2 |
| Portail hôte / dashboard sûreté / admin (Blazor) | ⏳ Jalon 2 — voir `src/NovAcces.Web/README.md` |
| Application agent (MAUI) | ⏳ Jalon 2 — voir `src/NovAcces.Mobile/README.md` |
| ASP.NET Core Identity + 2FA + RBAC | ⏳ Jalon 2 (TODO marqué dans `Program.cs`) |

**Voir la section "Audit de conformité du 23/07/2026" plus bas pour le détail
des corrections apportées et la matrice de traçabilité complète face au CDC.**

## Important — je n'ai pas pu compiler ce code

Cet environnement n'a pas accès au SDK .NET. Le code a été écrit avec la plus
grande rigueur, mais **la première chose à faire est de lancer un build réel**
chez toi ou dans Claude Code, et de corriger les éventuelles erreurs de
compilation (souvent mineures : versions de packages, imports).

## Démarrage, étape par étape

### 1. Prérequis
- SDK .NET 8 installé (`dotnet --version` doit répondre)
- PostgreSQL 15+ en local (ou via Docker, voir plus bas)

### 2. Premier build
```bash
cd NovAcces
dotnet restore
dotnet build
```
Corrige les éventuelles erreurs de compilation avant de continuer — c'est
normal d'en avoir quelques-unes à la première tentative (versions de
packages notamment, `Microsoft.EntityFrameworkCore` évolue vite).

### 3. Lancer les tests unitaires — NE PASSE PAS À LA SUITE SI ÇA ÉCHOUE
```bash
dotnet test
```
Les 23 tests doivent tous passer. Ils reproduisent fidèlement les scénarios
validés sur la maquette de démonstration (anti-rejeu, copie volée, fenêtre
de validité, cycle entrée/sortie, dépassements avec escalade). S'ils ne
passent pas, ne continue pas le développement avant de comprendre pourquoi.

### 4. Générer les clés de signature ES256 (jamais commitées)
```bash
openssl ecparam -genkey -name prime256v1 -noout -out qr-signing-key.pem
openssl ec -in qr-signing-key.pem -pubout -out qr-signing-public.pem
```
Copie le contenu de ces deux fichiers dans `src/NovAcces.Api/appsettings.
Development.json` (à créer, ignoré par git) sous `QrSigning:PrivateKeyPem`
et `QrSigning:PublicKeyPem` — ou mieux, utilise `dotnet user-secrets` :
```bash
cd src/NovAcces.Api
dotnet user-secrets set "QrSigning:PrivateKeyPem" "$(cat ../../qr-signing-key.pem)"
dotnet user-secrets set "QrSigning:PublicKeyPem" "$(cat ../../qr-signing-public.pem)"
```

### 5. Base de données locale (Docker, le plus simple)
```bash
docker run --name novacces-pg -e POSTGRES_PASSWORD=devpassword \
  -e POSTGRES_DB=novacces -p 5432:5432 -d postgres:16
```
Mets à jour `ConnectionStrings:Postgres` dans `appsettings.Development.json`
avec ce mot de passe.

### 6. Créer le schéma d'un premier site (le pilote)
Le multi-tenant repose sur un schéma PostgreSQL par site. Pour le pilote :
```sql
CREATE SCHEMA site_sicopa;
```
Puis générer et appliquer la première migration EF Core **contre ce schéma**
(la génération de migrations schema-aware est un point à valider ensemble
au démarrage du jalon 2 — EF Core ne gère pas nativement le multi-schéma
dynamique, une stratégie de migration manuelle ou un outil dédié sera
nécessaire).

### 7. Lancer l'API
```bash
cd src/NovAcces.Api
dotnet run
```
Puis teste avec un en-tête `X-Site-Id: sicopa` (Swagger disponible en
développement sur `/swagger`).

## Pour continuer avec Claude Code

Ouvre ce dossier avec Claude Code et donne-lui ce contexte en premier
message :

> Ce projet est NovAcces, un système de contrôle d'accès par QR Code pour
> Sigasécurité (Côte d'Ivoire). Architecture en Clean Architecture :
> Domain (logique métier pure, voir Visit.cs), Application (cas d'usage),
> Infrastructure (EF Core + PostgreSQL multi-tenant par schéma + signature
> ES256), Api (minimal API). Lis le README.md à la racine et les README.md
> dans src/NovAcces.Web et src/NovAcces.Mobile pour le contexte complet.
> Lance d'abord `dotnet build` et `dotnet test`, corrige les erreurs de
> compilation, puis on continue selon la feuille de route du jalon 2.

## Points de vigilance à ne jamais déléguer sans relecture

Ces zones sont sensibles ; si Claude Code (ou toi) les modifie, relis
attentivement avant de committer :

1. **`Visit.cs` (Domain)** — c'est la logique de sûreté. Toute modification
   doit être accompagnée d'un test qui la couvre.
2. **`Es256QrSigningService.cs`** — la cryptographie. Ne jamais réduire les
   vérifications, ne jamais logger la clé privée.
3. **`NovAccesDbContext.EnsureTenantSchemaAppliedAsync`** — c'est la garantie
   du cloisonnement multi-tenant. Une erreur ici est une fuite de données
   entre clients de Sigasécurité — la pire chose qui puisse arriver à ce
   projet.
4. **`ScanLogEntryConfiguration`** — le journal doit rester en INSERT-only en
   base (voir le commentaire dans le fichier, à appliquer via un script SQL
   de provisionnement, hors EF Core).

## Audit de conformité du 23/07/2026 — ce qui a été corrigé

Un audit ligne par ligne contre le CDC original et la proposition v4 a été
réalisé après la première version de ce scaffold. Deux bugs réels ont été
identifiés et corrigés directement dans ce commit ; ne pas les réintroduire :

1. **L'expiration cryptographique du QR n'était jamais vérifiée**
   (`ScanQrHandler`) — violation de REQ-SEC-04. Corrigé : un jeton dont
   `ExpiresAt` est dépassé est désormais rejeté comme signature invalide,
   avec test de régression (`ScanQrHandlerTests.HandleAsync_
   ExpiredCryptographicToken_IsRejectedAsSecurityEvent`).
2. **Aucun endpoint pour révoquer un QR** (REQ-F-09) — ajouté :
   `POST /api/visits/{visitId}/revoke`.
3. **Le mode « 30 jours » n'expirait jamais** après la période de 30 jours
   calendaires — corrigé dans `Visit.Scan` (REQ-F-05), avec test
   (`Scan_ThirtyDaysMode_AfterThirtyDayPeriod_IsDenied`).

Deux écarts restent **volontairement non couverts par ce scaffold**, à
traiter explicitement en Jalon 2 (ne pas les oublier) :

4. **Aucune interface de notification** (WhatsApp/email) n'existe encore —
   `CreateVisitHandler` génère le QR mais ne l'envoie nulle part. Prévoir
   une interface `INotificationService` dès le début du Jalon 2.
5. **Aucun hook de notification temps réel de l'hôte** (REQ-F-06) au moment
   du scan — à ajouter en même temps que SignalR (déjà noté en TODO dans
   `Program.cs`).

### Clarification sur la numérotation des exigences

La note d'analyse et la proposition financière citent « REQ-SEC-06 »
(doctrine du mode dégradé) et « REQ-F-11 » (liste d'exclusion) comme des
exigences à part entière. **Ce sont des propositions du prestataire**,
démontrées et acceptées via la note d'analyse v1.1 annexée au contrat —
elles n'existent pas sous ce numéro dans le cahier des charges original de
Sigasécurité. Ce n'est pas un problème contractuel (la note d'analyse fait
foi), mais évite toute confusion si le client recherche ces références
dans sa propre copie du CDC.

## Matrice de traçabilité CDC — état d'implémentation

Le CDC (section 9) demande une réponse point par point ; voici l'état réel
de ce scaffold face aux exigences qui concernent le code (les exigences
d'exploitation — sauvegardes, PRA, supervision — relèvent du Jalon 3 /
déploiement, pas du code, et ne sont pas listées ici).

| Exigence | Description | État |
|---|---|---|
| REQ-F-02 | Génération automatique du QR chiffré et signé | ✅ Implémenté (`CreateVisitHandler`) |
| REQ-F-03 | Transmission par email/WhatsApp | ❌ Aucune interface — Jalon 2 |
| REQ-F-05 | Fenêtre unique -20/+15 min ; 30 jours ouvrés avec expiration | ✅ Implémenté et corrigé (`Visit.Scan`) |
| REQ-F-06 | Notification temps réel de l'hôte | ❌ Aucun hook — Jalon 2 (SignalR) |
| REQ-F-07 | Journalisation de chaque tentative | ✅ Implémenté (`ScanLogEntry`) |
| REQ-F-09 | Révocation manuelle à tout moment | ✅ Implémenté et corrigé (endpoint ajouté) |
| REQ-F-10 | Architecture multi-tenant | ✅ Implémenté (schéma PostgreSQL par site) |
| REQ-F-11 (proposition) | Liste d'exclusion | 🟡 Modélisé dans le Domain ; service réel = Jalon 2 |
| REQ-SEC-01 | QR sans donnée personnelle en clair | ✅ Implémenté (payload = Guid + expiration uniquement) |
| REQ-SEC-02 | Validation de fenêtre exclusivement serveur | ✅ Implémenté (`IDateTimeProvider`, jamais l'heure client) |
| REQ-SEC-03 | Anti-rejeu atomique, scans simultanés | ✅ Implémenté (verrou `FOR UPDATE` + contrainte unique) |
| REQ-SEC-04 | Signature vérifiable, expiration intégrée | ✅ Implémenté et corrigé (ES256 + vérification d'expiration) |
| REQ-SEC-05 | Tentatives journalisées comme événements de sécurité | ✅ Implémenté (`IsSecurityEvent`) |
| REQ-SEC-06 (proposition) | Mode dégradé sécurisé | 🟡 Signature de liste hors ligne implémentée ; app MAUI = Jalon 2 |
| 8.2 | Rate limiting sur endpoints sensibles | ✅ Implémenté (fixed window limiter) |
| 8.2 | 2FA, gestion de session | ❌ Non commencé — Jalon 2 (Identity) |
| 8.5 | RBAC par profil | ❌ Non commencé — Jalon 2 (Identity + policies) |

**Légende** : ✅ implémenté et testé · 🟡 partiellement modélisé · ❌ non commencé (Jalon 2/3 prévu)

## Correspondance avec les jalons de la proposition v4

- **Jalon 1 (signature, 600 000 FCFA)** : ce scaffold + build/tests qui passent
  + premier déploiement de l'API en local ou sur le VPS de recette.
- **Jalon 2 (recette, 800 000 FCFA)** : Web + Mobile + Identity/2FA +
  WhatsApp + SignalR + dossier de conformité ARTCI initié.
- **Jalon 3 (production, 600 000 FCFA)** : déploiement VPS Contabo, recette
  de sécurité documentée (section 5 de la proposition), mise en production
  sur le site pilote SICOPA.
