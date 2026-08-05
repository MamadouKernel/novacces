# Audit de sécurité — API et Web — 05/08/2026

> **Mise à jour du même jour** : les findings Élevé, tous les Moyen sauf M2,
> et tous les Faible ont été corrigés le 05/08/2026 (voir tableau de suivi
> en fin de document). M1 a été traité par un avertissement à la connexion
> plutôt qu'une réactivation forcée du 2FA, sur décision explicite de
> Mamadou (le 2FA optionnel reste une décision assumée, en attente de
> confirmation écrite de Sigasécurité — voir note-decisions-client.md §5).
> M2 nécessite une action sur le VPS de production, hors de portée d'un
> agent local.

Audit indépendant de l'ensemble de l'application (NovAcces.Api,
NovAcces.Web), réalisé à la demande de Mamadou Konate. Périmètre : contrôle
d'accès (RBAC, cloisonnement multi-tenant), cryptographie, gestion des
sessions/secrets, OWASP Top 10 côté API et côté portail Blazor,
configuration de déploiement. Hors périmètre : app mobile (NovAcces.Mobile),
audit d'intrusion actif (cf. décision contractuelle, `accord-commercial.md`).

**Méthode** : relecture manuelle des zones sensibles listées au CLAUDE.md
§7 (Visit.cs, Es256QrSigningService, cloisonnement tenant, journaux
append-only), complétée par trois revues ciblées (endpoints API/RBAC, Web
Blazor, secrets/déploiement) et vérification que `dotnet build` et
`dotnet test` passent intégralement (**202/202 tests verts** : 84
unitaires + 15 Web + 103 intégration contre PostgreSQL réel).

## Synthèse

Le code réserve un niveau de rigueur et de documentation de la logique de
sûreté nettement au-dessus de la moyenne pour ce stade de projet : anti-rejeu
correctement centralisé dans le domaine, cloisonnement multi-tenant à double
barrière (middleware + intercepteur de connexion + guillemets défensifs sur
le nom de schéma), journaux réellement inaltérables au niveau PostgreSQL
(triggers, indépendants du rôle applicatif), 2FA TOTP avec anti-timing sur
le login, préservation de secrets par variable d'environnement, en-têtes de
sécurité HTTP + CSP sur les deux applications. **Aucune faille critique
n'a été identifiée.** Les constats ci-dessous sont classés par sévérité ;
plusieurs sont déjà connus et documentés dans le code lui-même comme
temporaires — ils sont repris ici pour ne pas être perdus avant la mise en
production pilote.

| Sévérité | Nombre |
|---|---|
| Élevé | 1 |
| Moyen | 3 |
| Faible | 6 |
| Info / bonnes pratiques à noter | 3 |

---

## Constats — Élevé

### É1. `POST /api/auth/refresh` (branche agent) ne revalide pas l'autorisation du terminal sur le site

**Fichier** : `src/NovAcces.Api/Endpoints/AuthEndpoints.cs:278-293`

`/login` applique explicitement `TerminalMayServeSite(principal, siteId)`
avant d'émettre un jeton agent (ligne 54, avec commentaire dédié sur le
risque de cloisonnement puisque `/api/auth` est exempté de
`TenantResolutionMiddleware`). La branche agent de `/refresh` ne fait, elle,
que vérifier l'égalité du `TerminalId` présenté avec celui encodé dans le
sujet du refresh token — elle ne rappelle jamais `TerminalMayServeSite`
pour confirmer que le terminal est **toujours** autorisé à servir
`subject.SiteId` au moment du renouvellement.

**Scénario concret** : un terminal enrôlé pour le site A, puis réaffecté au
site B (ou révoqué du site A) dans `TerminalDirectory`. S'il détient encore
un refresh token émis avant la réaffectation et non explicitement révoqué,
il peut continuer à renouveler un jeton agent valide pour le site A tant
que ce refresh token n'a pas expiré — en contradiction avec le principe de
cloisonnement énoncé par le projet lui-même (CLAUDE.md §7.3).

**Recommandation** : appeler `TerminalMayServeSite` (ou équivalent basé sur
l'état courant de `TerminalDirectory`) dans la branche agent de `/refresh`,
exactement comme dans `/login`. Envisager aussi de révoquer explicitement
les refresh tokens actifs d'un terminal lors de sa réaffectation/révocation
côté `TerminalDirectory`.

---

## Constats — Moyen

### M1. Rôle Admin global sans cloisonnement par client, combiné à un 2FA optionnel

**Fichiers** : `AuthEndpoints.cs:199-209` (register), `AdminEndpoints.cs`
(comptes/agents/sites/export), `AuthSetup.cs:98` (policy `Admin` sans
`HasResolvedTenant`).

Confirmé intentionnel par `NovAccesAuthorizationMatrix` (aucune notion de
périmètre par site pour Admin/SuperAdmin) : c'est cohérent avec le modèle
métier (les Admins appartiennent à Sigasécurité, pas à un client final).
Mais la conséquence est qu'**un compte Admin compromis a accès en écriture
à l'ensemble des sites de tous les clients de Sigasécurité** (création de
comptes, gestion des agents/terminaux, export de données), et le 2FA pour
les rôles privilégiés reste piloté par une option (`Auth:RequireTwoFactorForPrivileged`)
qui peut être désactivée par configuration.

**Recommandation** : s'assurer que `RequireTwoFactorForPrivileged=true` est
figé (non désactivable) en production, et envisager une journalisation/alerte
renforcée sur toute action Admin touchant un site autre que ceux touchés
habituellement par ce compte (détection d'anomalie a posteriori, faute de
cloisonnement a priori).

### M2. Séparation des rôles PostgreSQL (owner/app) non encore appliquée en production actuelle

**Fichiers** : `tools/provisionner-roles-postgres.sql`,
`TenantProvisioningService.cs:107-125`, `.env.example:18-22`.

Le mécanisme à deux rôles (`novacces_owner` pour le DDL, `novacces_app` pour
le runtime, avec REVOKE DELETE/TRUNCATE sur les journaux) est bien conçu et
documenté, mais `.env.example` indique que le déploiement réel actuel
tourne encore avec un seul rôle owner+runtime. La seconde barrière contre
une mutation des journaux (`scan_logs`, `admin_audit`) au-delà des triggers
n'est donc pas encore active — la garantie d'inaltérabilité repose
aujourd'hui **uniquement** sur les triggers `forbid_journal_mutation`.

**Recommandation** : exécuter `tools/provisionner-roles-postgres.sql` puis
`dotnet run --project src/NovAcces.Api -- grant-app-role` sur
l'environnement de production avant la mise en service pilote, comme prévu
par le CLAUDE.md §7.4.

### M3. Validation SMTP désactivée temporairement dans `ProductionConfigurationValidator`

**Fichier** : `src/NovAcces.Api/Configuration/ProductionConfigurationValidator.cs:23-30`

Le contrôle qui empêche un démarrage de production sans configuration SMTP
valide est commenté (« TEMPORAIRE, 02/08/2026, Mamadou — le temps de créer
le compte Brevo »). Or l'email est désormais **l'unique canal de
notification** du produit (WhatsApp abandonné, décision du 01/08/2026) :
un déploiement démarré sans SMTP configuré ne préviendrait plus l'hôte
d'aucune arrivée, départ, suspicion de copie ou dépassement — silencieusement,
sans erreur au démarrage.

**Recommandation** : restaurer les 4 lignes commentées avant tout test avec
de vrais visiteurs, comme le commentaire du code le prévoit déjà lui-même.
Signalé ici uniquement pour que ce ne soit pas oublié — c'est un TODO
explicite laissé par le prestataire, pas une découverte nouvelle.

---

## Constats — Faible

### F1. Aucun rate-limiting dédié sur `/api/agent/*` (offline-list, sync) et `/api/site/config`
Protégés par authentification (`AgentTerminal`) mais pas par une politique
de débit nommée comme `/api/scan` ou `/api/auth`. Risque limité (nécessite
déjà une clé API terminal valide) mais absence de garde-fou anti-DoS
applicatif dédié.

### F2. Gardes d'autorisation Web reposant sur convention, pas sur attribut systématique
`src/NovAcces.Web/Components/Layout/AdminLayout.razor:18,73-74` centralise
bien la garde des 7 pages Admin, et chaque page métier (`HotePortal.razor`,
`SuretePortal.razor`) porte sa propre vérification `Auth.IsInRole(...)`.
Mais rien n'empêche structurellement qu'une future page oublie
`@layout AdminLayout` ou sa propre garde — l'API resterait protégée par les
policies serveur, mais ce serait une régression de défense en profondeur
silencieuse côté UI. Recommandation : envisager un attribut/convention
vérifiable automatiquement (test qui énumère les pages et vérifie la
présence d'une garde) plutôt qu'une simple convention de code.

### F3. Duplication documentée de la règle de rôle `CanViewDashboard`
`src/NovAcces.Web/Components/Pages/SuretePortal.razor:469-475` recalcule
côté Blazor la même règle que la policy serveur `SecurityJournal`. Déjà
commenté dans le code comme risque de divergence assumé — à surveiller si
la policy serveur évolue sans que ce composant soit mis à jour en miroir.

### F4. `.env` sans permissions restreintes documentées sur le VPS
`deploy.sh` charge le `.env` correctement (sans `source`/`eval`, extraction
ciblée — bon point, voir historique des commits `62b623c`/`446f23f`), mais
aucun `chmod 600 .env` n'est scripté ni documenté pour restreindre l'accès
au fichier sur le serveur de production.

### F5. `.gitignore` ne couvre pas explicitement un futur `appsettings.Production.json`
Non exploité actuellement (la production passe par variables d'environnement
Docker Compose), donc risque latent seulement — mais si ce fichier était
créé un jour par erreur, il ne serait pas automatiquement exclu du dépôt.

### F6. Mots de passe de démonstration en clair dans `tools/simuler-scans.ps1`
(`ChangeMoi!2026Dev`, `Hote!2026Demo`, lignes 31/36/40). Acceptable pour un
script de démo/dev local — à confirmer qu'il n'est jamais exécuté contre
l'environnement de production avec ces identifiants.

---

## Points positifs à noter (bonnes pratiques observées)

- **Cryptographie** : ES256 natif (`System.Security.Cryptography`, aucune
  dépendance tierce à auditer), rotation de clé gérée par `kid`, séparation
  stricte instance-par-thread pour la thread-safety d'ECDsa, échec au
  démarrage plutôt qu'en scan si une clé est illisible.
- **Cloisonnement multi-tenant** : double barrière (middleware de
  résolution + intercepteur `SET search_path` à l'ouverture de connexion,
  contournant le comportement de pool de Npgsql), whitelist stricte des
  identifiants de site, `/api/auth` revalide bien le site pour `/login`
  (voir toutefois É1 pour `/refresh`).
- **Journaux d'audit** : réellement append-only au niveau PostgreSQL
  (triggers indépendants du rôle, y compris superutilisateur), seule
  l'anonymisation RGPD/ARTCI du nom de visiteur est permise, vérifiée par
  diff `jsonb` robuste à l'évolution du schéma.
- **Authentification** : anti-énumération de comptes par leurre à temps
  constant sur le login et le mot de passe oublié, verrouillage après 5
  échecs, politique de mot de passe forte (12 caractères, complexité),
  2FA TOTP avec interdiction de ré-exposer le secret une fois activé,
  ré-authentification par mot de passe avant toute action sensible
  (changement d'email, désactivation 2FA).
- **Enrôlement de terminal** : preuve de possession ECDSA P-256 liant la
  signature au ticket ET à l'installation, empêchant la capture/rejeu d'un
  QR d'enrôlement photographié.
- **Web (Blazor Server)** : XSS neutralisé (`WebUtility.HtmlEncode` avant
  toute `MarkupString`), CSRF largement neutralisé par le modèle de circuit
  SignalR (aucun formulaire HTTP classique exposé), jeton JWT chiffré côté
  serveur avant persistance en `sessionStorage` (`ProtectedSessionStorage`,
  pas de JWT en clair côté navigateur), CSP stricte avec `script-src 'self'`,
  hub temps réel qui revalide le site côté serveur sans jamais faire
  confiance à la query string du client.
- **Réseau/HTTP** : en-têtes de sécurité (nosniff, X-Frame-Options DENY,
  no-referrer) sur les deux applications, HSTS en production, rate limiting
  natif .NET 8 partitionné par IP+terminal avec liste blanche stricte des
  proxys de confiance pour `X-Forwarded-For` (sans quoi le rate limiting et
  la traçabilité s'effondreraient derrière un reverse proxy).
- **Secrets** : aucun secret réel committé (recherche exhaustive), `.env`
  chargé sans `source`/`eval` (évite l'injection shell), validateur qui
  refuse un démarrage de production avec des valeurs `CHANGE_ME`.
- **Tests** : 202/202 verts, y compris 103 tests d'intégration contre un
  vrai PostgreSQL couvrant le cloisonnement et le verrou de concurrence.

---

## Suivi des corrections (05/08/2026, même jour)

| Finding | Statut | Détail |
|---|---|---|
| É1 — `/api/auth/refresh` agent | ✅ Corrigé | `TerminalMayServeSite` rappelé dans la branche agent (`AuthEndpoints.cs`) |
| M1 — 2FA optionnel | ⚠️ Assumé, pas un bug | Laissé optionnel (décision du 02/08/2026) ; ajout d'un avertissement à la connexion (`LoginResponseDto.TwoFactorRecommended`, toast Web) au lieu d'une réactivation forcée |
| M2 — Rôles PostgreSQL owner/app pas actifs en prod | ⏳ Action VPS requise | Rien à corriger côté code (`deploy.sh` applique déjà `grant-app-role` automatiquement, sans effet tant que `Database:ApplicationRole` n'est pas configuré) — reste à exécuter `tools/provisionner-roles-postgres.sql` puis configurer `Database:ApplicationRole`/`ConnectionStrings:PostgresOwner` sur le `.env` du VPS réel |
| M3 — Validation SMTP désactivée | ✅ Corrigé | 4 lignes restaurées dans `ProductionConfigurationValidator.cs` ; identifiants SMTP réels (Gmail) préparés dans un `.env` local, non committé — à copier vers le VPS |
| F1 — Rate limiting `/api/agent/*`, `/api/site/config` | ✅ Corrigé | Politique `sensitive` appliquée aux deux groupes (`Program.cs`, `AgentContractEndpoints.cs`) |
| F2 — Gardes UI par convention | 📝 Recommandation non implémentée | Reste un chantier de test (énumération automatique des pages) plutôt qu'un bug ponctuel — pas traité dans cette passe |
| F3 — Duplication `CanViewDashboard` | ✅ Corrigé | Centralisé dans `NovAccesAuthorizationMatrix.CanViewSecurityJournal` (Shared), inclut désormais explicitement SuperAdmin |
| F4 — `.env` sans permissions restreintes | ✅ Corrigé | `chmod 600 .env` ajouté dans `deploy.sh` |
| F5 — `.gitignore` sans règle `appsettings.Production.json` | ✅ Corrigé | Ligne ajoutée |
| F6 — Mots de passe démo en clair | ✅ Réévalué, sans risque réel | `tools/simuler-scans.ps1` cible `https://localhost:54980` en dur, non paramétrable — ne peut pas viser accidentellement la production |

Vérification après correctifs : build propre, **202/202 tests toujours verts**.

## Notes de méthode / hors périmètre

- Pas d'audit d'intrusion actif réalisé (hors périmètre contractuel actuel,
  cf. `docs/accord-commercial.md`) — cette revue est une relecture de code
  et de configuration, pas un test d'exploitation en conditions réelles.
- L'app mobile (NovAcces.Mobile) n'a pas été auditée dans cette passe.
- Aucun workflow CI/CD (GitHub Actions) n'existe actuellement dans le
  dépôt : les vérifications ci-dessus (`dotnet build`/`test`) ne sont donc
  pas automatiquement rejouées à chaque push.
