# NovAcces.Mobile (.NET MAUI — Android) — à construire au Jalon 2

L'application agent de contrôle d'accès : scan caméra, verdict plein écran,
mode dégradé, poste directionnel entrée/sortie, liste des attendus du jour.

## Points d'attention spécifiques (au-delà de la logique déjà testée dans Domain)

1. **Lecture caméra réelle** : utiliser `ZXing.Net.MAUI` (maintenu, supporte
   Android/iOS) pour le scan — tester tôt sur un vrai terminal du parc
   Sigasécurité, pas seulement l'émulateur (autofocus, luminosité).
2. **Stockage local du mode dégradé** : SQLite via `sqlite-net-pcl`, table
   `offline_visits` recevant la liste signée (voir `IQrSigningService.
   VerifyDailyOfflineList` côté serveur, déjà implémenté et testé).
3. **La vérification de signature ES256 doit être dupliquée en local**,
   avec la clé PUBLIQUE uniquement embarquée dans l'app (jamais la clé
   privée). Réutiliser une classe de vérification équivalente à
   `Es256QrSigningService.VerifyDailyOfflineList`, mais compilée dans
   l'app elle-même — c'est ce qui permet la vérification hors ligne.
4. **Détection de connectivité** : `IConnectivity` (Microsoft.Maui.Essentials)
   pour basculer automatiquement en mode dégradé.

## Commande de démarrage (à exécuter au moment venu, nécessite les
## workloads MAUI installés : `dotnet workload install maui`)

```bash
cd src
dotnet new maui -n NovAcces.Mobile -o NovAcces.Mobile --force
cd NovAcces.Mobile
dotnet add reference ../NovAcces.Shared/NovAcces.Shared.csproj
dotnet add package ZXing.Net.MAUI
dotnet add package sqlite-net-pcl
dotnet sln ../../NovAcces.sln add NovAcces.Mobile.csproj
```
