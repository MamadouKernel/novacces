# Rapport de recette de sécurité — NovAcces (Jalon 2)

> Document interne remis à Mamadou KONATE (prestataire) en vue de la recette de
> sécurité prévue au contrat. Conformément à l'accord commercial, l'audit
> d'intrusion externe a été écarté par le client pour cette phase et **remplacé
> par une recette de sécurité interne documentée + tests automatisés + analyse
> OWASP**. Ce rapport en constitue la trace.
>
> **Rappel** : NovAcces conditionne un accès PHYSIQUE sur site sensible. L'audit
> d'intrusion externe reste recommandé avant tout déploiement chez un client
> tiers de Sigasécurité.

Date de rédaction initiale : 24/07/2026 · Revue complémentaire : 25/07/2026 ·
**Revue étendue (API + Web) : 01/08/2026 — cf. §9.**
Périmètre : API + Web (Jalon 2). L'application agent (MAUI) fera l'objet d'une
recette dédiée sur terminal réel.

> **Ce que ce rapport couvre — et ce qu'il ne couvre pas.** Il porte sur la
> sécurité et la fiabilité du code livré pour le Jalon 2 (API + Web). Il ne
> couvre PAS le test de charge (REQ-FIAB-06), ni l'infrastructure de production
> (sauvegardes, PRA, supervision, WAF, séparation dev/recette/prod) — travaux du
> Jalon 3, non réalisés à la date de cette revue. Voir §7.

---

## 1. Méthodologie

- **Recette interne documentée** : ce rapport, par exigence, avec renvoi au code
  et aux tests.
- **Tests automatisés** : **161 tests** au vert (62 unitaires + 84 d'intégration
  + 15 de composants Blazor via bUnit), 0 avertissement de compilation. Les
  tests unitaires reproduisent scénario par scénario la maquette validée par le
  client le 22/07/2026 (`docs/scenarios-fonctionnels.md`) ; les tests
  d'intégration exercent l'API réelle en mémoire (auth, RBAC, cloisonnement,
  temps réel) contre une base PostgreSQL dédiée aux tests.
- **Analyse OWASP** : cf. §5, mise en regard du Top 10.
- **Recette fonctionnelle Web** : parcours rejoués dans un navigateur réel
  (connexion, enrôlement 2FA avec génération de codes TOTP valides, création de
  visite, réémission de QR, sortie manuelle, console d'administration), pas
  seulement en test automatisé — cf. §9.4.

---

## 2. Exigences de sécurité (REQ-SEC) — état et preuves

| Exigence | Mesure | Code | Test |
|---|---|---|---|
| REQ-SEC-01 — pas de donnée personnelle en clair dans le QR | Payload = `VisitId` + `VisitToken` (Guid opaques) + expiration ; aucune donnée nominative | `Es256QrSigningService` | signature/vérif. |
| REQ-SEC-02 — fenêtre validée exclusivement côté serveur | Heure serveur (`IDateTimeProvider`), jamais l'heure client | `Visit.Scan` | `VisitScanTests.Scan_TooEarly/TooLate…` |
| REQ-SEC-03 — anti-rejeu atomique, scans simultanés | `SELECT … FOR UPDATE` **dans une transaction** (`IUnitOfWork`) + contrainte unique sur `VisitToken` | `ScanQrHandler`, `UnitOfWork` | `ConcurrencyAntiReplayTests` (4 scans concurrents → 1 seule entrée) |
| REQ-SEC-04 — signature vérifiable, expiration intégrée | ECDSA P-256 (ES256), `System.Security.Cryptography` natif, expiration cryptographique rejetée, **identifiant de clé (kid) embarqué pour permettre la rotation** | `Es256QrSigningService`, `ScanQrHandler` | `Es256QrSigningServiceTests`, `ScanQrHandlerTests.HandleAsync_ExpiredCryptographicToken…` |
| REQ-SEC-05 — tentatives journalisées comme événements de sécurité | `ScanLogEntry.IsSecurityEvent`, journal **append-only** (trigger DB **+ privilèges DB en seconde barrière**, cf. §9.1) | `ScanLogEntry`, `TenantProvisioningService` (trigger) | `TenantProvisioningTests.ScanLogsJournal_IsAppendOnly…`, `ApplicationRole_CannotDeleteJournals_EvenIfTriggersWereDropped` |
| REQ-SEC-06 (proposition) — mode dégradé sécurisé | Vérification ES256 **hors ligne** (clé publique seule, y compris clés retirées pendant une rotation), liste du jour signée + TTL, **exclusion appliquée à l'émission de la liste** | `OfflineQrVerifier`, `OfflineScanEvaluator` (Shared) | `OfflineQrVerifierTests`, `OfflineScanEvaluatorTests` |

**Cryptographie** : décision actée d'ECDSA P-256 natif (zéro dépendance
cryptographique tierce à auditer). La clé privée ne quitte jamais le serveur ;
seule la clé publique est destinée à être embarquée dans l'app agent. Le
service de signature est un singleton sollicité par des requêtes concurrentes :
les instances `ECDsa` sont désormais isolées par thread (`ThreadLocal<ECDsa>`),
cf. §9.1.

---

## 3. Authentification, RBAC et session (section 8.2 / 8.5 du CDC)

- **Authentification** : JWT (portail web) + clé API par terminal (agents).
  Politique de mot de passe durcie (≥ 12, mixte), verrouillage après 5 échecs.
  **2FA TOTP obligatoire pour les comptes à privilèges** (Admin, SuperAdmin,
  Sûreté), avec codes de récupération. Persistance de session chiffrée
  (ProtectedSessionStorage). Le parcours d'enrôlement initial (première
  connexion d'un compte privilégié) est désormais implémenté de bout en bout
  côté Web — cf. §9.3, bloquant corrigé.
- **RBAC** : policies ASP.NET Core (Hôte / Agent / Sûreté / Admin). **Moindre
  privilège** appliqué : un Hôte ne révoque que ses propres demandes ; le motif
  d'exclusion n'est visible que de la Sûreté/Admin ; le journal du site
  (dashboard, export) est réservé à la Sûreté/Admin — un Hôte n'y a plus accès
  (cf. §9.1).
- **Comparaison des clés API** à temps constant (`FixedTimeEquals`).
- **Réutilisation de refresh token détectée** : un jeton déjà consommé qu'on
  représente signifie qu'il a fuité ; toute la lignée de sessions du sujet est
  révoquée, pas seulement le jeton rejoué (cf. §9.1).
- Tests : `AuthEndpointsTests` (401 anonyme, 403 mauvais rôle, 2FA, anti-évasion
  de tenant), `VisitsTests` (moindre privilège révocation), `ExclusionTests`,
  `RefreshTokenReuse_RevokesTheWholeChain`.

---

## 4. Cloisonnement multi-tenant (REQ-F-10) — le risque majeur

- Un **schéma PostgreSQL par site** ; le `search_path` est repositionné à
  **chaque ouverture de connexion** (`TenantSchemaConnectionInterceptor`),
  robuste au pooling.
- Le tenant est **dérivé du jeton authentifié** (claim `SiteId`), pas d'un
  en-tête client falsifiable ; une tentative de viser un autre site → **403**.
- **`/api/auth` est exempté du middleware de cloisonnement** (le compte n'est
  pas encore authentifié au moment du login) : la connexion agent (matricule +
  PIN) revalide donc elle-même le site demandé contre les claims du terminal
  — trou corrigé le 01/08/2026, cf. §9.1.
- **Diffusion temps réel (SignalR)** : le hub applique la **même règle** — un
  utilisateur rattaché à un site ne peut s'abonner qu'au flux de SON site (le
  paramètre `site` est revalidé ET confronté au claim). *Corrigé le 25/07/2026,
  cf. §8.*
- Validation stricte des identifiants de site (whitelist ASCII, longueur bornée
  pour éviter la troncature silencieuse de PostgreSQL) ; un site au format
  valide mais non provisionné renvoie désormais 404 (au lieu d'une erreur
  PostgreSQL brute en 500), cf. §9.1.
- **Séparation en deux rôles PostgreSQL** (propriétaire pour le DDL, applicatif
  pour le runtime) : le rôle applicatif se voit retirer `DELETE`/`TRUNCATE` sur
  `scan_logs` et toute mutation sur `admin_audit` — seconde barrière derrière
  les triggers, effective uniquement si les deux rôles sont distincts (un
  `REVOKE` reste sans effet sur le propriétaire d'une table). Bootstrap :
  `tools/provisionner-roles-postgres.sql` + `dotnet run -- grant-app-role`.
  Vérifié par un test d'intégration avec un rôle non-propriétaire réel.
- **Preuve par test** : `TenantIsolationTests` — deux sites, aucune donnée ne
  franchit la frontière, y compris sous connexions poolées ;
  `AuthEndpointsTests` (anti-évasion de tenant) ;
  `AgentLogin_WithSiteOutsideTerminalAllowList_IsRejected` ;
  `ApplicationRole_CannotDeleteJournals_EvenIfTriggersWereDropped`.

---

## 5. Analyse OWASP Top 10 (2021)

| Risque | Traitement |
|---|---|
| A01 Contrôle d'accès défaillant | RBAC par policy, moindre privilège, tenant par claim, révocation avec contrôle de propriété, journal du site restreint à la Sûreté/Admin. |
| A02 Défaillances cryptographiques | ES256 natif, thread-safe, rotation de clé supportée (kid) ; secrets (clés, mots de passe, connection string) **hors dépôt** (user-secrets / variables d'environnement) ; QR sans donnée personnelle. |
| A03 Injection | EF Core paramétré ; le seul SQL brut (`search_path`, nom de schéma) est sur identifiant **validé + mis entre guillemets** ; recherche journal paramétrée ; **export CSV du journal neutralisé contre l'injection de formule** (préfixe apostrophe, reco. OWASP) — test dédié. |
| A04 Conception non sécurisée | Logique de sûreté centralisée dans le Domain (jamais dupliquée client, y compris pour la resynchronisation hors ligne — cf. §9.1) ; journal append-only ; sortie jamais bloquée. |
| A05 Mauvaise configuration | Rate limiting sur endpoints sensibles **et sur l'authentification (par IP)** ; **en-têtes de sécurité HTTP** sur API et Web + HSTS en production ; antiforgery (CSRF) côté Web ; redirection HTTPS ; **en-têtes de proxy inverse (`ForwardedHeaders`) avec liste blanche**, sans quoi le rate limiting et le journal global sont aveugles derrière un reverse proxy (cf. §9.1) ; provisionnement DDL réservé à l'Admin/CLI. |
| A07 Identification/Authentification | 2FA TOTP obligatoire pour comptes à privilèges (parcours d'enrôlement complet, cf. §9.3), verrouillage, messages d'échec génériques (anti-énumération), comparaison de clés à temps constant, détection de réutilisation de refresh token. |
| A08 Intégrité logiciel/données | Journal INSERT-only imposé au niveau base (triggers **+ privilèges**) ; signature vérifiable des QR et listes ; **la resynchronisation hors ligne rejoue systématiquement la vérification serveur** au lieu de faire confiance au verdict déclaré par le terminal (cf. §9.1) ; **preuve de possession de clé exigée à l'enrôlement d'un device** (un ticket intercepté ne suffit plus à activer un terminal à la place du bon). |
| A09 Journalisation | Chaque tentative journalisée ; événements de sécurité distingués ; supervision des dépassements ; rétention désormais appliquée aussi au journal technique global et aux sessions de rafraîchissement (cf. §9.1), qui croissaient sans purge. |

Non directement applicables au périmètre code : A06 (dépendances — parc réduit,
zéro dépendance cryptographique tierce), A10 (SSRF — pas d'appel serveur piloté
par l'utilisateur, hormis WhatsApp vers l'API Meta officielle).

---

## 6. Couverture de tests (synthèse)

- **Domain (maquette)** : cycle Unique, poste directionnel + copie volée, mode
  30 jours, exclusion (y compris exclusion appliquée après l'émission du QR),
  QR falsifié, escalade de dépassement, expiration de QR (`ComputeQrExpiry`) —
  reproduits par `VisitScanTests`, `Es256QrSigningServiceTests`,
  `BusinessDayServiceTests`.
- **Mode dégradé** : `OfflineQrVerifierTests`, `OfflineScanEvaluatorTests`
  (compatibilité croisée serveur ↔ agent prouvée, y compris multi-clés lors
  d'une rotation).
- **Intégration HTTP** : auth/RBAC/2FA, cloisonnement (y compris à la
  connexion agent), anti-rejeu concurrent, dashboard temps réel (SignalR),
  exclusion, admin, endpoints agent, resynchronisation hors ligne rejouée
  côté serveur, réémission de QR, sortie manuelle, réutilisation de refresh
  token, séparation des rôles PostgreSQL.
- **Composants Web (bUnit)** : 15 tests sur les contrôles partagés
  (`PasswordBox`, `ToastHost`, `SiteSlug`).

---

## 7. Limites connues et recommandations

1. **Audit d'intrusion externe** recommandé avant tout déploiement chez un
   client tiers de Sigasécurité (au-delà du site pilote SICOPA).
2. **Application agent (MAUI)** : le cœur cryptographique hors-ligne est fourni
   et testé ; l'app elle-même doit être construite et **recettée sur terminal
   réel** (scan caméra, autofocus, luminosité).
3. **Notifications WhatsApp** : nécessite les identifiants Meta Cloud API de
   production (configuration, pas code) ; les démarches d'approbation de
   template (24-72h, avec risque de refus) sont à initier au plus tôt.
4. **Test de charge (REQ-FIAB-06)** : non exécuté à la date de cette revue.
   Exigé par le CDC avant mise en production. Hors périmètre de ce rapport
   (Jalon 3).
5. **Infrastructure de production** (sauvegardes chiffrées + test de
   restauration, PRA, supervision, WAF, séparation dev/recette/prod) : non
   réalisée à la date de cette revue. Hors périmètre de ce rapport (Jalon 3),
   documentée dans `docs/deploiement.md`.
6. **Fériés hors-ligne** : la vérification de jour ouvré en mode dégradé ne
   connaît pas les fériés (confrontés au retour en ligne lors de la resync).
7. ~~**Réversibilité des données**~~ — **corrigé (03/08/2026)** : export complet
   d'un tenant (visites, journal, audit — CSV zippé) à la demande, console
   Admin → Sites, voir `note-decisions-client.md` §6.
8. ~~**Énumération de comptes par canal temporel**~~ — **corrigé (25/07/2026)**.
9. ~~**Enrôlement 2FA obligatoire pour Sûreté/Admin : parcours incomplet**~~ —
   **corrigé (01/08/2026)**, cf. §9.3.

---

## 8. Revue de sécurité complémentaire (25/07/2026)

Revue ligne par ligne des quatre zones sensibles de `CLAUDE.md §7`, avec
relecture du code et vérification par tests.

| Zone | Constat |
|---|---|
| Cryptographie (`Es256QrSigningService`) | **Conforme.** Clé privée jamais journalisée ni exposée ; algorithme figé dans le code (pas de confusion d'algorithme type JWT `alg:none`) ; la vérification porte sur les octets exactement signés (pas de faille de canonicalisation) ; payload sans donnée personnelle. |
| Cloisonnement (`TenantResolutionMiddleware`, `TenantSchemaConnectionInterceptor`, `CurrentTenant`) | **Conforme.** Tenant issu du claim ; en-tête divergent rejeté (403) ; `search_path` par connexion ; identifiant validé (whitelist ASCII, longueur < NAMEDATALEN) avant tout usage. |
| Anti-rejeu (`ScanQrHandler`, `UnitOfWork`, `VisitRepository`) | **Conforme.** `SELECT … FOR UPDATE` **paramétré** tenu dans une transaction jusqu'au commit ; expiration cryptographique vérifiée ; diffusion temps réel **après** commit. |
| Journal (`TenantProvisioningService`) | **Conforme.** Triggers bloquant `UPDATE`/`DELETE`/`TRUNCATE` sur `scan_logs`, quel que soit le rôle. |

### Faille identifiée et corrigée — fuite temps réel inter-tenant (SignalR)

- **Constat** : le hub `ScanEventsHub` validait le **format** du site demandé
  (paramètre `site`) mais pas sa **correspondance avec le compte connecté**. Un
  utilisateur authentifié d'un site (ex. Sûreté du client A) pouvait s'abonner
  au groupe d'un **autre** site et recevoir son flux de scans en direct (noms de
  visiteurs, verdicts). Impact : fuite de données entre clients de Sigasécurité
  (`CLAUDE.md §7.3`). Exploitabilité : nécessite un compte dashboard valide.
- **Correctif** : le hub confronte désormais le paramètre `site` au claim `SiteId`
  du principal — un utilisateur rattaché à un site ne rejoint que le sien ; seul
  l'Admin global (sans claim de site) peut cibler un site précis. Règle identique
  à celle du middleware HTTP.
- **Test de non-régression** : `DashboardTests.Hub_RejectsCrossTenantSubscription`
  — un utilisateur rattaché à un autre site qui vise `sicopa` ne reçoit aucun
  événement (sans le correctif, ce test échouerait).

### Injection de formule CSV — corrigée (OWASP A03)

- **Constat** : l'export CSV du journal échappait correctement le séparateur et
  les guillemets, mais pas les **caractères de formule** en tête de cellule
  (`= + - @`). Un nom de visiteur tel que `=HYPERLINK(...)` aurait pu être
  exécuté par Excel/LibreOffice/Sheets à l'ouverture, sur le poste de l'agent.
- **Correctif** : préfixe apostrophe sur toute valeur commençant par un
  caractère de formule (reco. OWASP), sans altérer la lisibilité du nom.
  Couvert par `DashboardTests.CsvExport_NeutralizesFormulaInjection`.

### Durcissement 2FA — pas de ré-exposition du secret TOTP

- **Constat** : `/2fa/setup` renvoyait la clé TOTP même lorsque le 2FA était
  déjà activé. Une session détournée (jeton volé) aurait pu lire le secret et
  cloner l'authentificateur de la victime.
- **Correctif** : `/2fa/setup` est refusé si le 2FA est déjà actif (il faut le
  désactiver d'abord, ce qui exige le mot de passe). Couvert par
  `AuthEndpointsTests.TwoFactor_Setup_WhenAlreadyEnabled_IsRejected`.

### Durcissement HTTP — rate limiting auth + en-têtes de sécurité

- **Rate limiting de l'authentification** : `/api/auth` (login, 2FA) était le
  seul endpoint sensible non limité. Ajout d'une politique **par IP** (10/min,
  configurable) — freine le brute-force / password-spraying au-delà du
  verrouillage par compte.
- **En-têtes de sécurité HTTP** (OWASP A05), absents jusqu'ici, posés sur
  toutes les réponses de l'API et du Web : `X-Content-Type-Options: nosniff`
  (anti-sniffing MIME), `X-Frame-Options: DENY` (anti-clickjacking),
  `Referrer-Policy: no-referrer` ; HSTS en production. Test dédié
  `AuthEndpointsTests.SecurityHeaders_ArePresentOnResponses`.

### Revue des journaux applicatifs (§7.2)

- **Aucun secret journalisé** : revue de tous les points de log — ni clé privée
  ES256, ni jeton JWT, ni mot de passe, ni clé API, ni jeton Meta, ni
  téléphone/email/QR ne sont écrits dans les traces.
- **Minimisation des données** : les noms de visiteurs (PII) qui apparaissaient
  dans quelques logs sont remplacés par l'**identifiant opaque de la visite**.

### Durcissement — isolation des tests d'intégration

Les tests d'intégration utilisent une base **dédiée** `novacces_test`, créée
automatiquement et surchargeable en CI (`NOVACCES_TEST_POSTGRES`). La base de
dev reste intacte.

---

## 9. Revue de sécurité et fiabilité étendue (01/08/2026)

Revue complète de l'API (endpoint par endpoint) puis du Web (les trois
portails, écran par écran), déclenchée par un doute légitime du prestataire
(« est-ce que l'API est bouclée à 100 % ? ») plutôt que par un incident. Douze
défauts de sécurité et de robustesse ont été identifiés et corrigés côté API,
puis un audit systématique du Web a mis au jour un bloquant de production et un
défaut de fiabilité récurrent sur onze méthodes. Le détail complet dépasse le
format de ce rapport ; ce qui suit est le résumé des constats à valeur de
sécurité, avec leur correctif et leur preuve par test.

### 9.1 API — constats et correctifs

| # | Constat | Correctif | Preuve |
|---|---|---|---|
| 1 | La liste d'exclusion (REQ-F-11) n'était vérifiée qu'à la CRÉATION de la demande. Une personne inscrite sur la liste **après** l'émission de son QR n'était jamais réévaluée au scan. | `Visit.Scan` prend désormais un paramètre `isOnExclusionList` obligatoire, relu en base à chaque scan (en ligne et hors ligne, à l'émission de la liste signée). | `Scan_VisitorAddedToExclusionListAfterQrIssued_IsDeniedAtEntry`, `HandleAsync_VisitorPutOnExclusionListAfterQrIssued_IsDeniedAndLogged` |
| 2 | `/api/agent/resync` faisait confiance au verdict **déclaré par le terminal** (`WasGranted`) pour journaliser un accès « accordé » dans un journal ineffaçable. Une clé de terminal volée pouvait ainsi forger des entrées « accordé » arbitraires. | La resynchronisation rejoue systématiquement la vérification de signature ES256 puis la règle métier complète via `ScanQrHandler` ; le verdict du terminal ne sert plus qu'à détecter un écart (= conflit remonté à la sûreté). | `Resync_FabricatedGrant_WithoutValidSignature_IsNeverJournaledAsGranted` |
| 3 | Derrière un reverse proxy (déploiement cible), `RemoteIpAddress` vaut l'adresse du proxy pour toutes les requêtes : le rate limiting par IP s'effondre en un seul seau pour tout le parc, et le journal global perd toute valeur d'enquête. | `UseForwardedHeaders` avec liste blanche de proxys de confiance (`ForwardedHeaders:KnownProxies`), en tête de pipeline. | Revue de configuration ; comportement par défaut sûr (loopback uniquement) si non configuré. |
| 4 | `/api/auth` est exempté du middleware de cloisonnement (compte pas encore authentifié). La connexion agent (matricule + PIN) résolvait le tenant depuis l'en-tête `X-Site-Id` **sans le confronter aux sites autorisés du terminal** : un terminal du site A pouvait éprouver les PIN des agents du site B. | La connexion agent revalide le site demandé contre les claims (`SiteId`/`AllowedSite`) du terminal authentifié, même règle que le middleware. | `AgentLogin_WithSiteOutsideTerminalAllowList_IsRejected`, `AgentLogin_WithOwnSite_Succeeds` |
| 5 | Aucune limite de débit ni de taille de lot sur les deux endpoints de resynchronisation ; chaque élément écrit une ligne ineffaçable. | Politique de rate limiting + plafond de 200 scans par lot sur les deux routes. | Revue de code ; couvert indirectement par les tests de resync existants. |
| 6 | `Es256QrSigningService` est un singleton sollicité par des requêtes concurrentes ; les membres d'instance `ECDsa` ne sont pas garantis thread-safe. La rotation de clé était en outre impossible (le `kid` n'était pas embarqué dans l'enveloppe signée). | Instances `ECDsa` isolées par thread (`ThreadLocal`) ; `kid` embarqué et clés retirées acceptées en vérification (`RetiredVerificationKeys`), côté serveur et mobile. | `Es256QrSigningServiceTests`, `OfflineQrVerifierTests` (compatibilité multi-clés) |
| 7 | `/api/audit/application` (et son export CSV) n'appliquait aucune limite par défaut : un GET sans paramètre chargeait toute la table en mémoire. L'export matérialisait le contenu trois fois. | Limite par défaut (200) et plafond (5000, CSV 100000) ; export en flux (`Results.Stream`). | Revue de code. |
| 8 | Le journal technique global (`ApplicationAudit`, une ligne par requête API) et les sessions de rafraîchissement révoquées/expirées n'étaient jamais purgés. | Rétention étendue (180 j / 30 j après expiration) dans `DataRetentionService`. | Revue de code. |
| 9 | L'enrôlement d'un terminal (ticket QR à usage unique) n'exigeait aucune preuve que l'appareil qui l'active détient la clé privée qu'il déclare : un ticket intercepté (capture d'écran) suffisait à enrôler un appareil tiers à la place du bon. | Le device signe `ticket|deviceInstanceId` avec sa clé privée ; l'API vérifie avec la clé publique présentée avant de délivrer la clé API. | `DeviceActivation_WithoutProofOfPossession_IsRejected` |
| 10 | Le dashboard sûreté (journal, export CSV) était accessible au rôle **Hôte**, qui voyait ainsi les allées et venues de tous les visiteurs du site, pas seulement les siens. | Nouvelle policy `SecurityJournal` (Sûreté/Admin uniquement) sur `/api/dashboard/*`. | Revue de code + `Journal_RequiresDashboardRole`. |
| 11 | Un site au format d'identifiant valide mais non provisionné retombait sur le schéma `public`, produisant une erreur PostgreSQL brute (500) au lieu d'un refus propre. | `ISiteCatalog.ExistsAsync` (mis en cache 30 s, invalidé après provisionnement) ; 404 explicite. | Revue de code. |
| 12 | Un refresh token déjà consommé, représenté à l'API, était refusé — mais sans conséquence sur le jeton **suivant** de la même lignée. Si le jeton avait fui, celui qui détenait le jeton suivant (légitime ou attaquant) gardait un accès valide : refuser le rejeu seul ne sert à rien. | Toute réutilisation (ou usage concurrent) révoque l'intégralité de la lignée de sessions du sujet. | `RefreshTokenReuse_RevokesTheWholeChain` |

En complément, la séparation en deux rôles PostgreSQL (§4) a été mise en place
et vérifiée avec un rôle non-propriétaire réel contre une instance PostgreSQL,
et non seulement documentée.

### 9.2 Environnement local — écart non lié au code

Un site provisionné localement avant l'ajout d'une migration (`CheckpointId`
sur `scan_logs`) a produit des erreurs 500 lors des tests Web (`/api/dashboard/summary`,
`/api/dashboard/journal`). Corrigé en rejouant `dotnet run -- provision-site`
(idempotent). Ce n'est pas un défaut de code : c'est le rappel opérationnel que
toute migration doit être suivie d'un re-provisionnement des sites existants —
déjà documenté au §7 de `CLAUDE.md`.

### 9.3 Web — bloquant de production

- **Constat** : le parcours d'enrôlement 2FA obligatoire pour un compte à
  privilèges (Admin, Sûreté) jamais connecté n'était pas implémenté côté Web.
  L'API renvoyait correctement `requiresTwoFactorEnrollment`, mais le client ne
  traitait que deux des trois réponses possibles de `/api/auth/login`. Tout
  compte concerné — **y compris l'Admin d'amorçage en production**
  (`SeedAdmin`) — tombait sur « Réponse d'authentification inattendue » sans
  pouvoir jamais se connecter. Sans ce correctif, le portail aurait été
  inaccessible dès la mise en production.
- **Correctif** : parcours complet ajouté (QR d'enrôlement + saisie manuelle de
  la clé, validation d'un premier code TOTP, affichage unique des codes de
  récupération) avant toute délivrance de jeton.
- **Vérification** : rejoué de bout en bout dans un navigateur réel, avec un
  code TOTP calculé à partir du secret émis par l'API, jusqu'à connexion
  effective sur la console Admin.

### 9.4 Web — défaut de fiabilité systémique (disponibilité)

- **Constat** : au cours de la recette, un simple 500 API a fait tomber tout le
  circuit Blazor du dashboard sûreté (déconnexion complète de l'utilisateur).
  En creusant, le même défaut — une action déclenchée par un bouton
  (`try { … } finally { … }` **sans `catch`**) — existait dans **11 méthodes**
  réparties sur les trois portails (créer/révoquer/provisionner un compte, un
  site, un agent, un terminal ; ajouter/retirer une exclusion ; consulter un
  QR ; enregistrer une sortie manuelle). N'importe quel aléa réseau sur l'une
  de ces actions déconnectait l'utilisateur entier — précisément au moment où
  la sûreté ou l'administration en a le plus besoin.
- **Correctif** : les 11 méthodes protègent désormais l'appel API et affichent
  un message d'erreur au lieu de laisser l'exception atteindre Blazor.
- **Constat associé, plus grave** : deux de ces méthodes (révocation de QR côté
  Hôte, retrait d'exclusion côté Sûreté) affichaient un message de **succès
  inconditionnel**, sans vérifier le résultat réel de l'appel API. Sur une
  action de sûreté, annoncer à tort qu'un QR est révoqué ou qu'une exclusion
  est levée est plus grave qu'un plantage : cela donne une fausse assurance à
  l'utilisateur qui agit en conséquence.
- **Correctif** : le message affiché reflète désormais le résultat réel de
  l'appel.
- **Vérification** : revue exhaustive de tous les appels API des trois
  portails (pas un échantillon) ; correctifs vérifiés dans le navigateur avec
  latence réseau réelle (Blazor Server).

### 9.5 Web — fonctionnalités manquantes au regard des scénarios validés

Trois écarts fonctionnels identifiés en comparant systématiquement l'API et le
Web à `docs/scenarios-fonctionnels.md`, sans impact sécurité direct mais
pertinents pour la recette :

- L'hôte n'était **jamais notifié** de l'arrivée, du départ, d'une suspicion de
  copie ou d'un dépassement concernant son visiteur (§1, §2, §7 du document de
  scénarios), alors que ces notifications avaient été démontrées au client le
  22/07. Ajouté (canal email, best-effort, après commit).
- Aucun moyen de clore le cycle d'un visiteur reparti sans scanner (§7) : il
  restait « présent » et en dépassement indéfiniment. `Visit.ForceCheckOut`
  ajouté, exposé au dashboard sûreté, audité.
- Le visiteur ayant perdu son QR (message WhatsApp/email non reçu) n'avait
  aucun recours : `GET /api/visits/{id}/qr` ajouté pour réémettre le même
  jeton avec la même expiration, réservé au propriétaire de la demande et à la
  Sûreté/Admin.

---

## 10. Conclusion

Le socle critique de sûreté (signature, anti-rejeu, fenêtre serveur, cycle
directionnel, cloisonnement multi-tenant, authentification/RBAC/2FA, journal
inaltérable) est **implémenté, conforme à la maquette validée et couvert par
161 tests automatisés au vert**.

Deux revues complémentaires ont suivi la recette initiale du 24/07 :
- celle du 25/07 (§8) a identifié et corrigé une fuite temps réel inter-tenant
  (hub SignalR), avec test de non-régression ;
- celle du 01/08 (§9), plus large, a porté sur l'API dans son ensemble puis sur
  le Web écran par écran. Elle a corrigé douze constats de sécurité côté API
  (dont un contournement du cloisonnement à la connexion agent et une
  resynchronisation hors ligne qui faisait confiance au terminal), un bloquant
  qui aurait rendu le portail Web inutilisable en production (parcours
  d'enrôlement 2FA absent), et un défaut de disponibilité systémique touchant
  onze actions du Web.

Le périmètre **API + Web du Jalon 2** est jugé conforme à la maquette validée
et à un niveau de sécurité et de fiabilité satisfaisant pour la suite de la
recette. Il **n'est pas évalué prêt pour la mise en production** au sens du
CDC : le test de charge (REQ-FIAB-06) n'a pas été exécuté et l'infrastructure
de production (sauvegardes, PRA, supervision, WAF, séparation des
environnements) n'est pas en place — ces deux points relèvent du Jalon 3 et
sortent du périmètre de ce rapport. L'audit externe reste par ailleurs
recommandé avant tout déploiement chez un client tiers de Sigasécurité, et la
recette de l'application agent sur terminal réel reste à mener.
