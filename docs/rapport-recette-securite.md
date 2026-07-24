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

Date de rédaction : 24/07/2026 · Périmètre : API + Web (Jalon 2). L'application
agent (MAUI) fera l'objet d'une recette dédiée sur terminal réel.

---

## 1. Méthodologie

- **Recette interne documentée** : ce rapport, par exigence, avec renvoi au code
  et aux tests.
- **Tests automatisés** : **74 tests** au vert (43 unitaires + 31 d'intégration),
  0 avertissement de compilation. Les tests unitaires reproduisent scénario par
  scénario la maquette validée par le client le 22/07/2026
  (`docs/scenarios-fonctionnels.md`) ; les tests d'intégration exercent l'API
  réelle en mémoire (auth, RBAC, cloisonnement, temps réel).
- **Analyse OWASP** : cf. §5, mise en regard du Top 10.

---

## 2. Exigences de sécurité (REQ-SEC) — état et preuves

| Exigence | Mesure | Code | Test |
|---|---|---|---|
| REQ-SEC-01 — pas de donnée personnelle en clair dans le QR | Payload = `VisitId` + `VisitToken` (Guid opaques) + expiration ; aucune donnée nominative | `Es256QrSigningService` | signature/vérif. |
| REQ-SEC-02 — fenêtre validée exclusivement côté serveur | Heure serveur (`IDateTimeProvider`), jamais l'heure client | `Visit.Scan` | `VisitScanTests.Scan_TooEarly/TooLate…` |
| REQ-SEC-03 — anti-rejeu atomique, scans simultanés | `SELECT … FOR UPDATE` **dans une transaction** (`IUnitOfWork`) + contrainte unique sur `VisitToken` | `ScanQrHandler`, `UnitOfWork` | `ConcurrencyAntiReplayTests` (4 scans concurrents → 1 seule entrée) |
| REQ-SEC-04 — signature vérifiable, expiration intégrée | ECDSA P-256 (ES256), `System.Security.Cryptography` natif, expiration cryptographique rejetée | `Es256QrSigningService`, `ScanQrHandler` | `Es256QrSigningServiceTests`, `ScanQrHandlerTests.HandleAsync_ExpiredCryptographicToken…` |
| REQ-SEC-05 — tentatives journalisées comme événements de sécurité | `ScanLogEntry.IsSecurityEvent`, journal **append-only** (trigger DB) | `ScanLogEntry`, `TenantProvisioningService` (trigger) | `TenantProvisioningTests.ScanLogsJournal_IsAppendOnly…` |
| REQ-SEC-06 (proposition) — mode dégradé sécurisé | Vérification ES256 **hors ligne** (clé publique seule), liste du jour signée + TTL | `OfflineQrVerifier`, `OfflineScanEvaluator` (Shared) | `OfflineQrVerifierTests`, `OfflineScanEvaluatorTests` (12 tests) |

**Cryptographie** : décision actée d'ECDSA P-256 natif (zéro dépendance
cryptographique tierce à auditer). La clé privée ne quitte jamais le serveur ;
seule la clé publique est destinée à être embarquée dans l'app agent.

---

## 3. Authentification, RBAC et session (section 8.2 / 8.5 du CDC)

- **Authentification** : JWT (portail web) + clé API par terminal (agents).
  Politique de mot de passe durcie (≥ 12, mixte), verrouillage après 5 échecs.
  **2FA TOTP** (application d'authentification) avec codes de récupération.
  Persistance de session chiffrée (ProtectedSessionStorage).
- **RBAC** : policies ASP.NET Core (Hôte / Agent / Sûreté / Admin). **Moindre
  privilège** appliqué : un Hôte ne révoque que ses propres demandes ; le motif
  d'exclusion n'est visible que de la Sûreté/Admin.
- **Comparaison des clés API** à temps constant (`FixedTimeEquals`).
- Tests : `AuthEndpointsTests` (401 anonyme, 403 mauvais rôle, 2FA, anti-évasion
  de tenant), `VisitsTests` (moindre privilège révocation), `ExclusionTests`.

---

## 4. Cloisonnement multi-tenant (REQ-F-10) — le risque majeur

- Un **schéma PostgreSQL par site** ; le `search_path` est repositionné à
  **chaque ouverture de connexion** (`TenantSchemaConnectionInterceptor`),
  robuste au pooling.
- Le tenant est **dérivé du jeton authentifié** (claim `SiteId`), pas d'un
  en-tête client falsifiable ; une tentative de viser un autre site → **403**.
- Validation stricte des identifiants de site (whitelist ASCII, longueur bornée
  pour éviter la troncature silencieuse de PostgreSQL).
- **Preuve par test** : `TenantIsolationTests` — deux sites, aucune donnée ne
  franchit la frontière, y compris sous connexions poolées ; `AuthEndpointsTests`
  (anti-évasion de tenant).

---

## 5. Analyse OWASP Top 10 (2021)

| Risque | Traitement |
|---|---|
| A01 Contrôle d'accès défaillant | RBAC par policy, moindre privilège, tenant par claim, révocation avec contrôle de propriété. |
| A02 Défaillances cryptographiques | ES256 natif ; secrets (clés, mots de passe, connection string) **hors dépôt** (user-secrets / variables d'environnement) ; QR sans donnée personnelle. |
| A03 Injection | EF Core paramétré ; le seul SQL brut (`search_path`, nom de schéma) est sur identifiant **validé + mis entre guillemets** ; recherche journal paramétrée. |
| A04 Conception non sécurisée | Logique de sûreté centralisée dans le Domain (jamais dupliquée client) ; journal append-only ; sortie jamais bloquée. |
| A05 Mauvaise configuration | Rate limiting sur endpoints sensibles ; redirection HTTPS ; provisionnement DDL réservé à l'Admin/CLI. |
| A07 Identification/Authentification | 2FA TOTP, verrouillage, messages d'échec génériques (anti-énumération), comparaison de clés à temps constant. |
| A08 Intégrité logiciel/données | Journal INSERT-only imposé au niveau base (triggers) ; signature vérifiable des QR et listes. |
| A09 Journalisation | Chaque tentative journalisée ; événements de sécurité distingués ; supervision des dépassements. |

Non directement applicables au périmètre code : A06 (dépendances — parc réduit,
zéro dépendance cryptographique tierce), A10 (SSRF — pas d'appel serveur piloté
par l'utilisateur, hormis WhatsApp vers l'API Meta officielle).

---

## 6. Couverture de tests (synthèse)

- **Domain (maquette)** : cycle Unique, poste directionnel + copie volée, mode
  30 jours, exclusion, QR falsifié, escalade de dépassement — reproduits par
  `VisitScanTests`, `Es256QrSigningServiceTests`, `BusinessDayServiceTests`.
- **Mode dégradé** : `OfflineQrVerifierTests`, `OfflineScanEvaluatorTests`
  (compatibilité croisée serveur ↔ agent prouvée).
- **Intégration HTTP** : auth/RBAC/2FA, cloisonnement, anti-rejeu concurrent,
  dashboard temps réel (SignalR), exclusion, admin, endpoints agent.

---

## 7. Limites connues et recommandations

1. **Audit d'intrusion externe** recommandé avant tout déploiement chez un
   client tiers de Sigasécurité (au-delà du site pilote SICOPA).
2. **Application agent (MAUI)** : le cœur cryptographique hors-ligne est fourni
   et testé ; l'app elle-même doit être construite et **recettée sur terminal
   réel** (scan caméra, autofocus, luminosité).
3. **Notifications WhatsApp** : nécessite les identifiants Meta Cloud API de
   production (configuration, pas code).
4. **Enrôlement 2FA obligatoire** pour Sûreté/Admin : mécanisme 2FA en place ;
   l'imposer à la connexion est un durcissement à activer.
5. **Fériés hors-ligne** : la vérification de jour ouvré en mode dégradé ne
   connaît pas les fériés (confrontés au retour en ligne lors de la resync).

---

## 8. Conclusion

Le socle critique de sûreté (signature, anti-rejeu, fenêtre serveur, cycle
directionnel, cloisonnement multi-tenant, authentification/RBAC/2FA, journal
inaltérable) est **implémenté, conforme à la maquette validée et couvert par
74 tests automatisés au vert**. Sous réserve des recommandations du §7 —
notamment l'audit externe avant déploiement chez un tiers et la recette de l'app
agent sur terminal — le périmètre API + Web est jugé prêt pour la mise en
production sur le site pilote.
