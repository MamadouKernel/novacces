# PR — Jalon 2 : API + Web complets, socle Mobile, durcissement sécurité

**Branche** : `fix/multitenant-isolation-securisation` → `main`
**Ampleur** : 21 commits · 118 fichiers · +8 579 / −227 · **74 tests au vert · 0 warning**

> ⚠️ **Relecture humaine obligatoire avant merge** : plusieurs commits touchent
> les zones sensibles listées dans `CLAUDE.md §7` (cryptographie, cloisonnement
> multi-tenant, anti-rejeu, journal inaltérable). Relire attentivement ces
> zones avant d'accepter.

## Ce que fait cette PR

Fait passer NovAcces du scaffold Jalon 1 à un **Jalon 2 API + Web complet et
testé**, conforme au CDC et à la maquette validée le 22/07/2026, et pose le
**socle vérifiable du Mobile**.

### 1. Corrections de sécurité critiques (à relire en priorité)
- **Cloisonnement multi-tenant robuste** : `search_path` repositionné à chaque
  ouverture de connexion (`TenantSchemaConnectionInterceptor`) — l'ancien « SET »
  ne survivait pas au pooling. Tenant dérivé du **jeton**, pas d'un en-tête.
- **Anti-rejeu (REQ-SEC-03)** : verrou `FOR UPDATE` désormais tenu dans une
  transaction (`IUnitOfWork`) — sinon relâché avant la sauvegarde.
- **Fix clé JWT signe/valide** : la validation lisait la clé trop tôt (fragilité
  révélée par les tests d'intégration).
- **Durcissement** : secrets hors dépôt (user-secrets), `.gitignore` clés/pem,
  journal `scan_logs` append-only (triggers).

### 2. Authentification, RBAC, 2FA
JWT (web) + clé API (agents), policies Hôte/Agent/Sûreté/Admin, **2FA TOTP** +
codes de récupération, moindre privilège, persistance de session.

### 3. Portail web (`NovAcces.Web`, Blazor Server)
Portail hôte (création + QR + liste + révocation + autocomplétion/ré-invitation),
dashboard sûreté (temps réel SignalR + présents + synthèse intelligente + export
CSV + recherche + liste d'exclusion + alertes de dépassement), administration
(comptes + provisionnement + vue multi-sites).

### 4. Fonctionnalités métier complétées
Anti-doublon, jours ouvrés (fériés), audit de révocation, **escalade des
dépassements** (service de fond réel), liste d'exclusion fonctionnelle.

### 5. Socle Mobile (vérifiable, testé)
- Endpoints agent : attendus du jour, liste hors-ligne signée, resynchronisation.
- `OfflineQrVerifier` + `OfflineScanEvaluator` (Shared) : vérification ES256 et
  décision de scan **hors ligne**, compatibilité serveur ↔ agent **prouvée**.
- Code de l'app MAUI **écrit mais non compilé ici** (à finir sur terminal).

### 6. Documentation
Dossier de recette de sécurité, matrice de traçabilité CDC à jour, plan Mobile.

## Tests
- 43 unitaires (reproduisent la maquette scénario par scénario) + 31 intégration
  (API réelle en mémoire). Nécessitent un PostgreSQL local (sinon skippés).

## Points d'attention
- **Ne pas fusionner sans relire les zones §7.** 
- Config de production à renseigner (WhatsApp Meta, TLS, VPS) — hors code.
- App agent MAUI à compiler/tester sur terminal réel.

## Comment relire / valider
```bash
dotnet build          # 0 warning attendu
dotnet test           # 74 tests (PostgreSQL local requis pour l'intégration)
```
