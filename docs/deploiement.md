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

**Pour tout redéploiement après le tout premier** (nouveau code poussé sur
`main`), utiliser `./deploy.sh` depuis la racine du dépôt sur le VPS plutôt
que de rejouer les commandes à la main : il enchaîne `git pull`, rebuild,
`migrate` (schéma `identity`), puis rejoue `provision-site` pour CHAQUE site
déjà provisionné (idempotent — applique les migrations EF du schéma tenant
`agents`, etc.), et enfin `grant-app-role`. Ce script existe précisément
parce qu'un oubli de cette séquence après déploiement a cassé la connexion
en production le 04/08/2026 (colonne manquante — migration EF non rejouée).

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
  notifiée) + alerte automatique. `GET /health` (non authentifié) existe déjà
  et fait une vraie sonde base (pas un simple "l'API répond") — voir
  `Program.cs`, ajouté après l'incident du 03/08/2026 (schéma manquant faute
  de migration rejouée, découvert manuellement au lieu d'être détecté en
  secondes). Les trois services Docker (`postgres`, `api`, `web`) ont chacun
  un `healthcheck` propre dans `docker-compose.yml`, mais celui-ci ne fait
  redémarrer que le conteneur local — il ne prévient PERSONNE.
- **Recommandation concrète (non mise en place — nécessite un compte que je
  ne peux pas créer à la place de Mamadou)** : un moniteur externe gratuit
  (ex. UptimeRobot, plan gratuit — 50 moniteurs, intervalle 5 min) pointé sur
  `https://api.sigasacces.com/health`, avec alerte email/SMS si `status`
  ≠ `"ok"` ou si la réponse dépasse un délai. 10 minutes de configuration,
  aucun coût, couvre l'essentiel de REQ-FIAB-05 sans infrastructure
  supplémentaire à maintenir. Alternative auto-hébergée si Sigasécurité
  préfère ne rien confier à un tiers : Uptime Kuma (conteneur Docker de plus
  dans `docker-compose.yml`, un seul binaire, aucune dépendance externe).
- Surveillance ressources (CPU/mémoire/disque) et espace PostgreSQL — non
  mise en place à ce jour, même remarque (nécessite un choix d'outil et des
  identifiants que Mamadou doit fournir).
- Les **événements de sécurité** (hors-fenêtre, QR consommé, conflit de resync)
  sont déjà remontés applicativement (journal + SignalR) : prévoir leur relève
  côté supervision.
- **Sauvegardes (§7.4)** : `BackupScheduler` (ajouté le 10/08/2026) déclenche
  une sauvegarde chiffrée (AES-256-GCM, `DatabaseBackup:EncryptionPassphrase`)
  automatiquement si `DatabaseBackup:AutoBackup:Enabled=true` — désactivé par
  défaut, à activer explicitement en `.env` (la production refuse de démarrer
  avec l'automatique activé sans passphrase configurée, voir
  `ProductionConfigurationValidator`). Réplication hors-site optionnelle vers
  un compartiment S3-compatible (`DatabaseBackup:Offsite:*` — Contabo Object
  Storage ou équivalent, identifiants à fournir par Mamadou, désactivée par
  défaut) : condition d'isolement anti-rançongiciel, un volume Docker sur le
  même serveur ne protège de rien si le VPS lui-même est compromis.

## 8bis. Test de charge (REQ-FIAB-06)

Outil dédié : `tools/NovAcces.LoadTest` (console .NET, ajouté le 10/08/2026).
Provisionne N postes RÉELS (même parcours d'enrôlement qu'un vrai poste —
ticket QR + preuve de possession de clé), une visite par poste, puis chaque
poste scanne en boucle (alternance entrée/sortie) au rythme demandé pendant
la durée demandée, et rapporte débit + latence (p50/p95/p99) + répartition
des statuts HTTP.

```bash
dotnet run --project tools/NovAcces.LoadTest -- \
  --base-url https://api.sigasacces.com \
  --site sicopa \
  --admin-email <compte admin réel> --admin-password <mot de passe réel> \
  --terminals 5 --requests-per-minute-per-terminal 25 \
  --duration-seconds 1800 \
  --output tools/load-test-results-$(date +%F).md
```

- `--terminals` × `--requests-per-minute-per-terminal` doit rester **sous 30**
  par poste (politique de débit "sensitive" de `/api/scan`, voir `Program.cs`)
  sous peine de mesurer le plafonnement du rate limiter plutôt que le débit
  soutenable réel — dimensionner selon le nombre de postes physiques
  réellement prévus pour le site.
- `--duration-seconds 1800` pour les 30 minutes exigées par le CDC §6 avant
  bascule en production (le calibrage du 10/08/2026, voir §9 ci-dessous,
  n'a couvert que 3 minutes en local — représentatif de la latence
  applicative, PAS du réseau/matériel réels du VPS).
- Contre `https://localhost:54980` avec `--no-insecure-tls` omis (défaut) :
  accepte le certificat auto-signé de Kestrel en dev, à ne PAS utiliser tel
  quel contre une URL de production (le flag existe justement pour empêcher
  ce défaut silencieux — passer `--no-insecure-tls` explicitement, ou
  simplement s'assurer que le certificat Let's Encrypt de Caddy est valide,
  ce qui est déjà le cas en production).

## 9. Avant bascule pilote — check-list

- [ ] TLS actif (≥ 1.2), redirection HTTPS forcée, en-têtes de sécurité.
- [ ] PostgreSQL non exposé publiquement, rôle applicatif non superutilisateur.
- [ ] Secrets en variables d'environnement, aucun secret versionné.
- [ ] Clés ES256 générées ; clé privée sur serveur uniquement.
- [ ] Site pilote provisionné (schéma + triggers append-only vérifiés).
- [ ] Sauvegarde quotidienne chiffrée + **un test de restauration réussi**.
- [x] **Test de charge** — calibrage réalisé le 10/08/2026 en local (5 postes
      simulés × 25 scans/min, 3 min soutenues, 125 scans/min agrégé — cible
      ≥ 100/min du CDC dépassée), 375/375 requêtes réussies, p95 91 ms, p99
      165 ms. Outil réutilisable : `tools/NovAcces.LoadTest` (voir
      `tools/load-test-results-2026-08-10.md`). **Reste à faire avant le
      pilote** : rejouer la même commande contre le VPS réel (réseau/matériel
      de production, pas un poste de développement) avec le nombre de postes
      physiques réellement prévus pour SICOPA, sur une fenêtre de 30 min.
- [ ] Supervision + alerte en place.
- [ ] `recette-securite.md` remis et recommandations §5 traitées.
- [ ] App agent compilée en VS et testée sur un terminal réel (mode dégradé inclus).
