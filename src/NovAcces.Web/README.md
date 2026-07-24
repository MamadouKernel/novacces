# NovAcces.Web (Blazor Server, .NET 8) — Jalon 2

Portail web de NovAcces. Application Blazor Web App (interactivité Server, sans
prérendu), **cliente de `NovAcces.Api`** : elle ne réimplémente aucune règle
métier ni de sécurité — l'API reste l'unique point d'application de
l'authentification, du RBAC et du cloisonnement multi-tenant.

## Implémenté (incrément 1)

- **Connexion** (`/login`) : formulaire e-mail/mot de passe → `POST /api/auth/login`.
  Gère le **2FA** : si un second facteur est requis, saisie du code TOTP →
  `POST /api/auth/login/2fa`. Le JWT obtenu est conservé dans l'état du circuit
  (`AuthState`, scoped).
- **Portail hôte** (`/hote`, rôle Hôte) : création d'une demande de visite
  (`POST /api/visits`, le site venant du claim du jeton) et **affichage du QR
  signé** généré (rendu via QRCoder). Déconnexion.

Architecture : `Services/AuthState.cs` (état d'auth par circuit),
`Services/NovAccesApiClient.cs` (client typé, joint le Bearer),
`Services/QrImage.cs` (QR → data URI). `HttpClient` configuré vers `Api:BaseUrl`
(appsettings), avec acceptation du certificat auto-signé **en développement
uniquement**.

## Démarrage (dev)

L'API doit tourner (voir README racine, `dotnet run` dans `src/NovAcces.Api`).
Puis :
```bash
cd src/NovAcces.Web
dotnet run --launch-profile http   # http://localhost:5282
```
Compte Hôte de test : créé via `POST /api/auth/register` (Admin), ou l'Admin de
dev amorcé peut en créer un.

## Reste à construire (incréments suivants)

- **Portail hôte** : autocomplétion des visiteurs connus, liste et **révocation**
  de ses propres demandes (REQ-F-09).
- **Dashboard sûreté** (`/surete`) : journal des scans en temps réel (SignalR,
  hub `/hubs/scan` déjà exposé), présents sur site, synthèse quotidienne,
  exports CSV.
- **Administration** (`/admin`) : provisionnement de sites, gestion des comptes.
- Persistance de session du JWT (aujourd'hui perdue au rechargement complet) et
  redirection propre sur expiration/401.
