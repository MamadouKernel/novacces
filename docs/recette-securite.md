# Rapport de recette de sécurité interne — NovAcces

**Version** : 1.0 · **Date** : 25/07/2026 · **Auteur** : Mamadou KONATE (prestataire)
**Destinataire** : Sigasécurité — Direction des Opérations (M. Kodjo)

> **Cadre contractuel.** Le CDC original (§7.6) posait un audit d'intrusion
> externe comme condition suspensive de mise en production. Sigasécurité a
> décidé, par écrit (voir `accord-commercial.md`), de **ne pas y recourir pour
> la phase pilote**, et de le remplacer par **une recette de sécurité interne
> documentée + des tests automatisés + une analyse OWASP**. Le présent rapport
> est ce livrable. L'audit externe **reste recommandé** avant tout déploiement
> chez un client tiers de Sigasécurité.

## 1. Périmètre et méthode

- **Périmètre** : API centrale (`NovAcces.Api`), domaine et logique de sûreté
  (`NovAcces.Domain`, `NovAcces.Application`), infrastructure (`NovAcces.Infrastructure`),
  portail web (`NovAcces.Web`), cœur de sûreté hors-ligne partagé (`NovAcces.Shared`).
  L'application agent MAUI (`NovAcces.Mobile`) est **hors périmètre de cette
  itération** (compilation/tests sur terminal à réaliser — voir `audit-mobile.md`).
- **Méthode** : revue de code ciblée sur les zones sensibles (CLAUDE.md §7),
  analyse OWASP Top 10 (2021), et **suite de tests automatisés exécutée contre un
  PostgreSQL réel**.
- **Couverture de tests au moment du rapport** : **113 tests, tous au vert** —
  52 unitaires + 46 d'intégration + 15 web.

## 2. Contrôles de sécurité vérifiés (exigences déterminantes du CDC §7)

| Réf. CDC | Contrôle | Implémentation | Preuve |
|---|---|---|---|
| REQ-SEC-01 | QR sans donnée personnelle en clair | Payload = identifiants opaques (Guid) + expiration uniquement | `Es256QrSigningService` ; tests `Security/*` |
| REQ-SEC-02 | Fenêtre de validité vérifiée **exclusivement côté serveur** | Horloge serveur (`IDateTimeProvider`), jamais l'heure client | `Domain/Visit.EvaluateWindow` ; tests `Visits/*` |
| REQ-SEC-03 | Anti-rejeu atomique, scans simultanés | Verrou pessimiste `SELECT … FOR UPDATE` + contrainte d'unicité | `VisitRepository.GetForUpdateAsync` ; `ConcurrencyAntiReplayTests` |
| REQ-SEC-04 | Signature vérifiable, expiration intégrée | ECDSA P-256 (ES256) natif ; expiration vérifiée en ligne **et hors ligne** | `Es256QrSigningService`, `OfflineScanEvaluator` |
| REQ-SEC-05 | Tentatives hors règle = événement de sécurité | `IsSecurityEvent` propagé, journalisé, diffusé (SignalR) | `Domain/Visit.Scan` ; tests intégration |
| §7.2 | 2FA obligatoire comptes à privilèges | TOTP + codes de récupération | `AuthEndpoints`, `AuthEndpointsTests` |
| §7.2 | Rate limiting endpoints sensibles | Fixed-window limiter sur `/api/scan`, `/api/visits`, `/api/auth` | `Program.cs` |
| §7.3 | Cloisonnement multi-tenant étanche | Schéma PostgreSQL par site + intercepteur de `search_path` robuste au pooling | `TenantSchemaConnectionInterceptor` ; `TenantIsolationTests` |
| §7.3 | Rétention limitée + purge automatique | Purge des demandes > 365 j ; anonymisation du nom dans le journal > 1095 j | `DataRetentionService` ; `RetentionTests` |
| §7.5 | Journal d'audit inaltérable | Triggers append-only (UPDATE/DELETE/TRUNCATE bloqués) sur `scan_logs` et `admin_audit` | `TenantProvisioningService` ; `AuditTests`, `ScanLogImmutabilityTests` |
| §7.5 | RBAC 4 profils, moindre privilège | Policies ASP.NET Core (Hôte/Agent/Sûreté/Admin) ; refus générique à l'agent | `AuthSetup` ; `AdminTests`, `ExclusionTests` |
| §8.5 | Traçabilité des actions d'administration | Journal `admin_audit` par site (révocation, exclusion, purge), minimisé | `AdminAuditLog` ; `AuditTests` |

## 3. Analyse OWASP Top 10 (2021)

| Catégorie | Évaluation | Mesures en place |
|---|---|---|
| **A01 — Contrôle d'accès défaillant** | ✅ Maîtrisé | RBAC par policy ; cloisonnement tenant dérivé du jeton (anti-évasion) ; moindre privilège (motif d'exclusion réservé à la sûreté, refus générique à l'agent). Testé. |
| **A02 — Défaillances cryptographiques** | ✅ Maîtrisé | ES256 natif (aucune dépendance crypto tierce à auditer) ; aucun secret en dur (user-secrets/variables d'env côté serveur, SecureStorage côté terminal) ; QR sans PII ; TLS ≥ 1.2 en transit. |
| **A03 — Injection** | ✅ Maîtrisé | EF Core paramétré ; l'unique requête SQL brute (`FOR UPDATE`) est **interpolée paramétrée** ; noms de schéma en **liste blanche ASCII stricte** (`CurrentTenant.IsValidSiteId`, borne 40 car.). |
| **A04 — Conception non sécurisée** | ✅ Maîtrisé | Modèle de sûreté explicite : cycle directionnel entrée/sortie, anti-rejeu sur le cycle complet, « la sortie n'est jamais bloquée », événements de sécurité, escalade des dépassements. |
| **A05 — Mauvaise configuration** | 🟡 À finaliser (Jalon 3) | Redirection HTTPS, rate limiting, séparation dev/recette/prod prévue. Durcissement VPS (WAF, segmentation) à réaliser au déploiement — voir `deploiement.md`. |
| **A06 — Composants vulnérables** | ✅ Maîtrisé | Dépendances minimales ; **CVE-2025-6965 (SQLitePCLRaw) corrigée** cette itération (NU1903 résolu, bump 2.1.10 → 2.1.12). Veille à poursuivre. |
| **A07 — Défaillances d'identification** | ✅ Maîtrisé | 2FA TOTP pour comptes à privilèges ; JWT à expiration ; clé API par terminal ; persistance de session maîtrisée. |
| **A08 — Défaut d'intégrité logiciel/données** | ✅ Maîtrisé | Journaux append-only (triggers base) ; QR et liste hors-ligne **signés** et vérifiés (ES256) ; anonymisation contrôlée n'altérant aucun fait de sécurité. |
| **A09 — Journalisation/supervision insuffisante** | 🟡 Partiel | Journal de **chaque** tentative de scan (en ligne et hors-ligne), journal d'audit admin, événements de sécurité diffusés en temps réel (SignalR). Supervision infra (monitoring/alerting) à mettre en place au déploiement (Jalon 3). |
| **A10 — SSRF** | ✅ Non applicable/maîtrisé | Aucune URL sortante contrôlée par l'utilisateur ; destinations (WhatsApp Cloud API, SMTP) fixées par configuration serveur. |

## 4. Zones sensibles — état de la revue (CLAUDE.md §7)

1. `Domain/Visit.cs` — logique de sûreté ✅ couverte par tests unitaires.
2. `Es256QrSigningService` — cryptographie ✅ ; clé privée jamais journalisée.
3. Cloisonnement multi-tenant ✅ prouvé sous connexions poolées (`TenantIsolationTests`).
4. `ScanLogEntryConfiguration` + provisionnement — journal INSERT-only ✅ imposé
   au niveau base (triggers), y compris pour un superutilisateur.

## 5. Risques résiduels et recommandations

| Risque résiduel | Gravité | Recommandation |
|---|---|---|
| Absence d'audit d'intrusion externe (décision client) | Moyenne | Le commander **avant tout déploiement chez un client tiers** de Sigasécurité. |
| Tests de charge non réalisés (REQ-FIAB-06) | Moyenne | Exécuter un test de charge représentatif d'un pic **avant mise en production** (Jalon 3). |
| Sauvegardes/PRA/supervision non déployés | Moyenne | Mettre en œuvre au déploiement VPS — voir `deploiement.md` (sauvegardes chiffrées isolées, RTO ≤ 4h / RPO ≤ 24h). |
| App agent MAUI non compilée/testée sur terminal | Moyenne | Compiler en VS + tester le mode dégradé bout-en-bout sur un terminal du parc (voir `audit-mobile.md`). |
| Durcissement infra (WAF, segmentation) à faire | Moyenne | À réaliser sur le VPS avant la mise en production pilote. |

## 6. Conclusion

Au niveau **applicatif et cryptographique**, les exigences déterminantes du CDC
(§7.1 à §7.5) sont **implémentées et vérifiées par tests automatisés**. Les
risques résiduels relèvent de **l'exploitation et de la mise en production**
(Jalon 3) et de la **finalisation de l'app agent**. Sous réserve de traiter les
recommandations du §5 avant la bascule pilote, aucune vulnérabilité critique ou
élevée n'a été identifiée dans le périmètre revu.
