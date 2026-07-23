# NovAcces.Web (Blazor Server) — à construire au Jalon 2

Ce projet portera les trois interfaces web démontrées le 22/07/2026 :

- **Portail hôte** (`/hote`) — création de demande, autocomplétion visiteurs connus, révocation
- **Dashboard sûreté** (`/surete`) — journal, présents, synthèse quotidienne, exports CSV
- **Administration multi-sites** (`/admin`) — provisionnement de sites, gestion des comptes

## Pourquoi ce n'est pas scaffoldé maintenant

Le cœur sensible du système (signature ES256, anti-rejeu, multi-tenant) devait être posé
et testé en priorité — c'est fait (voir `src/NovAcces.Domain`, `src/NovAcces.Infrastructure`,
`tests/NovAcces.UnitTests`). Le Web consomme ces briques via `NovAcces.Api` : il n'y a pas
de risque architectural à le construire ensuite, contrairement au multi-tenant ou à la
cryptographie qui doivent être corrects dès le premier jour.

## Commande de démarrage (à exécuter au moment venu)

```bash
cd src
dotnet new blazorserver -n NovAcces.Web -o NovAcces.Web --force
cd NovAcces.Web
dotnet add reference ../NovAcces.Shared/NovAcces.Shared.csproj
dotnet sln ../../NovAcces.sln add NovAcces.Web.csproj
```

Puis RBAC par policy (`[Authorize(Policy = "Hote")]`, etc.) branché sur ASP.NET Core Identity
une fois celui-ci ajouté à `NovAcces.Api` (voir TODO dans `Program.cs`).
