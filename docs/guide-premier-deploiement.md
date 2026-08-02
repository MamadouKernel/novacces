# Guide — premier déploiement sur le VPS (Contabo)

**VPS** : `169.58.112.177` (Contabo, Hub Europe) · **Domaine** : `sigasacces.com`
(DNS déjà pointé vers l'IP, vérifié le 02/08/2026).

Ce guide complète `docs/deploiement.md` : c'est la séquence de commandes à
copier-coller, dans l'ordre, pour la toute première mise en service. Chaque
section indique où vous en êtes avant de continuer.

## 0. Connexion initiale

Depuis PowerShell (Windows a un client SSH intégré) :

```bash
ssh root@169.58.112.177
```

Tapez le mot de passe root choisi à la commande Contabo (celui que je n'ai
jamais vu). Une fois connecté, vérifiez l'OS installé :

```bash
cat /etc/os-release
```

Si ce n'est pas déjà Debian 12 ou Ubuntu 22.04/24.04 LTS, dites-le-moi —
la suite suppose l'un des deux (Contabo permet de réinstaller l'OS depuis
le panneau `my.contabo.com` si besoin).

## 1. Mise à jour + sécurisation minimale

```bash
apt update && apt upgrade -y

# Pare-feu : seuls 22 (SSH), 80 (validation Let's Encrypt) et 443 (HTTPS)
apt install -y ufw
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
ufw status
```

## 2. Clé SSH (remplace le mot de passe root)

**Depuis votre PC** (PowerShell, pas sur le VPS), si vous n'avez pas déjà
une clé :

```powershell
ssh-keygen -t ed25519 -C "mamadou@sigasacces"
ssh-copy-id root@169.58.112.177
```

Si `ssh-copy-id` n'existe pas sous Windows, copiez manuellement le contenu
de `~/.ssh/id_ed25519.pub` dans `~/.ssh/authorized_keys` sur le VPS.

**Une fois la connexion par clé confirmée** (déconnectez-vous et
reconnectez-vous sans mot de passe pour vérifier), désactivez l'authentification
par mot de passe sur le VPS :

```bash
sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
systemctl restart sshd
```

⚠️ Ne faites cette dernière étape qu'**après avoir vérifié** que la
connexion par clé fonctionne — sinon vous vous bloquez l'accès au serveur.

## 3. Installer Docker

```bash
curl -fsSL https://get.docker.com | sh
systemctl enable --now docker
docker --version
docker compose version
```

## 4. Récupérer le code

```bash
apt install -y git
git clone https://github.com/MamadouKernel/novacces.git /opt/sigasacces
cd /opt/sigasacces
```

## 5. Générer les secrets (une seule fois)

```bash
# Clés ES256 (signature des QR)
openssl ecparam -name prime256v1 -genkey -noout -out sigasacces-ec-private.pem
openssl ec -in sigasacces-ec-private.pem -pubout -out sigasacces-ec-public.pem

# Clé JWT et mot de passe Postgres
echo "JWT: $(openssl rand -base64 48)"
echo "Postgres: $(openssl rand -base64 24)"
```

Copiez le `.env` d'exemple puis ouvrez-le pour le compléter :

```bash
cp .env.example .env
nano .env
```

Dans `.env`, collez :
- `POSTGRES_PASSWORD` : la valeur générée ci-dessus
- `JWT_SIGNING_KEY` : la valeur générée ci-dessus
- `QR_SIGNING_PRIVATE_KEY_PEM` / `QR_SIGNING_PUBLIC_KEY_PEM` : le contenu
  des deux fichiers `.pem` générés (`cat sigasacces-ec-private.pem`)
- `SEED_ADMIN_PASSWORD` : un mot de passe fort temporaire (à changer à la
  première connexion)
- `SMTP_*` : **obligatoires pour démarrer** — l'API refuse de démarrer en
  production si l'un de ces champs est vide ou vaut encore `CHANGE_ME`
  (`ProductionConfigurationValidator`). Le "best-effort" ne s'applique
  qu'à l'envoi d'un email une fois l'API démarrée (une panne SMTP
  ponctuelle ne bloque jamais un scan), pas au démarrage lui-même.
  Créez un compte Brevo gratuit (https://www.brevo.com, jusqu'à 300
  emails/jour) → **SMTP & API** dans le menu → onglet **SMTP** → notez
  la "Login" (ressemble à un email `@smtp-brevo.com` ou similaire, c'est
  `SMTP_USERNAME`) et cliquez **Générer une nouvelle clé SMTP**
  (c'est `SMTP_PASSWORD`, différent du mot de passe de votre compte
  Brevo). Collez les deux dans `.env`.

`DOMAIN`, `API_DOMAIN` et `API_PUBLIC_BASE_URL` sont déjà corrects dans
`.env.example` (sigasacces.com).

Une fois les clés collées dans `.env`, supprimez les fichiers `.pem` du
disque (ils ne doivent plus traîner en clair une fois dans `.env`) :

```bash
shred -u sigasacces-ec-private.pem sigasacces-ec-public.pem
```

## 6. Démarrer la stack

```bash
docker compose up -d --build
docker compose ps
```

Attendez que les 4 services soient `healthy` (`docker compose ps`), puis
appliquez les migrations et amorcez le compte Admin initial :

```bash
docker compose exec api dotnet NovAcces.Api.dll migrate
```

## 7. Provisionner le site pilote (SICOPA)

```bash
docker compose exec api dotnet NovAcces.Api.dll provision-site sicopa
```

## 8. Vérifier

```bash
curl -I https://sigasacces.com
curl -I https://api.sigasacces.com/health
```

Les deux doivent répondre `200` avec un certificat valide (Caddy l'aura
obtenu automatiquement auprès de Let's Encrypt — peut prendre jusqu'à une
minute au tout premier démarrage). Puis, depuis un navigateur :
`https://sigasacces.com` → connexion avec `SEED_ADMIN_EMAIL` /
`SEED_ADMIN_PASSWORD` → changer le mot de passe et activer le 2FA
immédiatement.

## 9. À ne pas oublier ensuite

- [ ] Sauvegarde quotidienne chiffrée de Postgres (`docs/deploiement.md` §7) —
      pas encore mise en place par ce guide, à faire avant la bascule réelle.
- [ ] Supprimer le compte `qa.verif2` / autres comptes de test s'ils existent
      encore dans votre base de dev (n'affecte pas la prod, base séparée).
- [ ] Checklist complète avant bascule pilote : `docs/deploiement.md` §9.
