# Guide de déploiement et d'exploitation — NovAcces (Jalon 3)

**Version** : 1.0 · **Date** : 25/07/2026

Procédure de mise en production du pilote (site SICOPA) sur VPS. Couvre
l'hébergement, la base, les secrets, la génération des clés, le provisionnement
des sites, les sauvegardes, le PRA et la supervision. À adapter au fournisseur
retenu ; les valeurs marquées « à confirmer » restent à figer avec Sigasécurité.

## 0. Déploiement conteneurisé (Docker) — ajouté le 02/08/2026

Un `docker-compose.yml` (racine du dépôt) packages les 4 services : `postgres`,
`api`, `web`, et `caddy` (reverse proxy + TLS Let's Encrypt automatique).
Dockerfiles : `src/NovAcces.Api/Dockerfile`, `src/NovAcces.Web/Dockerfile`.

```bash
cp .env.example .env        # puis renseigner les valeurs réelles (voir le fichier)
docker compose up -d --build
docker compose exec api dotnet NovAcces.Api.dll migrate   # 1re fois, puis après chaque migration EF
```

Le reste de ce document (durcissement, secrets, sauvegardes…) s'applique
identiquement — Docker ne change que le mécanisme de packaging/exécution, pas
les exigences de sécurité. L'option **systemd + `dotnet publish`** du §5
reste valide si vous préférez ne pas passer par Docker.

## 1. Hébergement cible

- **VPS Contabo (Union européenne)**, ~8-15 €/mois (décision actée, voir
  `accord-commercial.md`). Localisation UE explicitée au client.
- **Nom de domaine** : `sigasacces.com` (acheté le 02/08/2026). Deux
  sous-domaines : `sigasacces.com` (portail Web) et `api.sigasacces.com` (API,
  terminaux agents) — voir `.env.example`.
- OS conseillé : Linux (Debian/Ubuntu LTS). **System.Drawing volontairement
  écarté** (le QR est rendu via `PngByteQRCode`, sans libgdiplus).

## 2. Durcissement du serveur (§7.4 du CDC)

- Pare-feu : n'exposer que 443 (HTTPS) et le SSH (port non standard, clé
  uniquement, `PasswordAuthentication no`).
- Comptes administrateurs restreints et **journalisés** (accès `sudo` tracé).
- Mises à jour de sécurité automatiques (`unattended-upgrades`).
- **WAF / reverse proxy** (nginx ou Caddy) devant l'API et le Web ; TLS ≥ 1.2,
  certificat Let's Encrypt (renouvellement automatique).
- Segmentation : PostgreSQL **n'écoute que sur `localhost`** (jamais exposé au
  réseau public) ; sauvegardes isolées de la production (protection rançongiciel).

## 3. Base de données PostgreSQL

- Installer PostgreSQL (version LTS). Créer un rôle applicatif **dédié**, non
  superutilisateur, propriétaire de la base `novacces`.
- Le cloisonnement est **par schéma** (un schéma `site_<id>` par client) : aucune
  base physique par site à créer. Le provisionnement crée le schéma, le modèle et
  les **triggers append-only** des journaux (voir §5).
- Chiffrement au repos : chiffrement du volume/disque (LUKS) au niveau système.

## 4. Secrets et clés (jamais versionnés)

Fournis par **variables d'environnement** (ou `dotnet user-secrets` en dev) —
jamais dans `appsettings.json`. Sections attendues :

| Clé | Rôle |
|---|---|
| `ConnectionStrings:Postgres` | Chaîne de connexion (rôle applicatif dédié) |
| `Api:PublicBaseUrl` | URL HTTPS absolue de l'API (terminaux, enrôlement) |
| `QrSigning:PrivateKeyPem` / `PublicKeyPem` | Clés ES256 (voir §4.1) |
| `Jwt:SigningKey` (≥ 32 car.) | Jetons web |
| `Smtp:*` | Notifications — **email uniquement** depuis le 01/08/2026 (WhatsApp abandonné, voir `accord-commercial.md` ; `EmailNotificationService`) |
| `SeedAdmin:Email` / `Password` / `DisplayName` | Amorçage du compte Admin initial (`dotnet NovAcces.Api.dll migrate`) |
| `Retention:VisitRetentionDays=365`, `Retention:JournalRetentionDays=1095` | Rétention (validées client) |
| `BusinessDays:Holidays` | Jours fériés ivoiriens par site |

### 4.1 Génération des clés ES256

Générer une paire ECDSA P-256 (PEM). La **clé privée reste sur le serveur** ; seule
la **clé publique** est distribuée aux terminaux agents (vérification hors-ligne) :

```bash
openssl ecparam -name prime256v1 -genkey -noout -out novacces-ec-private.pem
openssl ec -in novacces-ec-private.pem -pubout -out novacces-ec-public.pem
```

Renseigner `QrSigning:PrivateKeyPem` (serveur) et distribuer `novacces-ec-public.pem`
aux terminaux via l'enrôlement (SecureStorage — voir `audit-mobile.md`).

## 5. Publication et exécution

1. Publier l'API : `dotnet publish src/NovAcces.Api -c Release`.
2. Exécuter derrière le reverse proxy comme **service systemd** (redémarrage
   auto, journaux `journald`). Environnement `Production`.
3. Publier `NovAcces.Web` de la même manière.
4. **Provisionner le site pilote** (crée schéma + modèle + triggers append-only) :
   - via l'API : `POST /api/admin/sites` (authentifié, rôle Admin), ou
   - en CLI d'exploitation : `dotnet run -- provision-site sicopa` (à confirmer
     selon le point d'entrée retenu).
5. Appliquer les migrations et amorcer le compte Admin initial :
   `dotnet NovAcces.Api.dll migrate` (lit `SeedAdmin:Email` / `SeedAdmin:Password` /
   `SeedAdmin:DisplayName` — mot de passe à changer dès la première connexion).
   Idempotent : à rejouer après chaque déploiement qui ajoute une migration EF.

## 6. Environnements séparés (REQ-FIAB-07)

- **dev** (poste), **recette**, **production** : trois environnements distincts,
  chaînes de connexion et secrets propres à chacun.
- Base de test dédiée pour l'intégration (`novacces_test`) — jamais la prod.
- Procédure de **rollback documentée** : conserver l'artefact de publication
  précédent et la migration EF correspondante ; retour arrière = redéploiement de
  l'artefact N-1 (les migrations sont idempotentes et additives).

## 7. Sauvegardes et PRA (REQ-FIAB-03/04)

- **Sauvegardes quotidiennes automatiques, chiffrées** (`pg_dump` chiffré GPG),
  stockées **hors du serveur de production** (bucket/emplacement isolé).
- **Test de restauration trimestriel** documenté.
- Objectifs : **RPO ≤ 24h** (sauvegarde quotidienne) et **RTO ≤ 4h**
  (procédure de restauration + redéploiement chronométrée et documentée).
- Attention : le journal `scan_logs` est append-only ; une restauration doit
  recréer les triggers (le script de provisionnement est idempotent).

## 8. Supervision (REQ-FIAB-05)

- Monitoring de disponibilité (cible **≥ 99,5 %** mensuel hors maintenance
  notifiée) + alerte automatique (ex. sonde HTTP sur un endpoint de santé).
- Surveillance ressources (CPU/mémoire/disque) et espace PostgreSQL.
- Les **événements de sécurité** (hors-fenêtre, QR consommé, conflit de resync)
  sont déjà remontés applicativement (journal + SignalR) : prévoir leur relève
  côté supervision.

## 9. Avant bascule pilote — check-list

- [ ] TLS actif (≥ 1.2), redirection HTTPS forcée, en-têtes de sécurité.
- [ ] PostgreSQL non exposé publiquement, rôle applicatif non superutilisateur.
- [ ] Secrets en variables d'environnement, aucun secret versionné.
- [ ] Clés ES256 générées ; clé privée sur serveur uniquement.
- [ ] Site pilote provisionné (schéma + triggers append-only vérifiés).
- [ ] Sauvegarde quotidienne chiffrée + **un test de restauration réussi**.
- [ ] **Test de charge** représentatif d'un pic exécuté (REQ-FIAB-06).
- [ ] Supervision + alerte en place.
- [ ] `recette-securite.md` remis et recommandations §5 traitées.
- [ ] App agent compilée en VS et testée sur un terminal réel (mode dégradé inclus).
