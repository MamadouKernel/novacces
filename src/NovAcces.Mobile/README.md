# NovAcces.Mobile (.NET MAUI — Android/Windows)

L'application agent de contrôle d'accès : scan caméra, verdict plein écran,
mode dégradé, poste directionnel entrée/sortie, liste des attendus du jour.

## ✅ État : projet scaffoldé et intégré (à compiler dans Visual Studio)

Le projet est désormais **complet et prêt à compiler** :
`NovAcces.Mobile.csproj` (net10 android + windows), `App.xaml(.cs)` (ouvre
directement `ScanPage` via l'injection), `Platforms/`, `Resources/`, permission
caméra Android, câblage DI (`MauiProgram.cs`), et référence à `NovAcces.Shared`.

### Compiler / lancer (dans Visual Studio)
1. **Ouvrir dans Visual Studio 2022** (workloads MAUI installés) :
   clic droit sur la solution → *Ajouter → Projet existant* →
   `src/NovAcces.Mobile/NovAcces.Mobile.csproj`.
   > Le projet n'est **volontairement pas** dans `NovAcces.sln` : le SDK en
   > ligne de commande (bande 10.0.200) ne voit pas les workloads MAUI
   > installés par VS (bande 10.0.100), ce qui casserait `dotnet build` du
   > reste de la solution. **Visual Studio, lui, compile le projet sans
   > problème.** (Si tu veux l'ajouter au `.sln` définitivement, fais-le
   > depuis VS et compile via VS, pas via `dotnet build` en CLI.)
2. Renseigner `AgentConfig` dans `MauiProgram.cs` (URL API, clé API du
   terminal, clé PUBLIQUE ES256) — à externaliser en stockage sécurisé à
   l'enrôlement.
3. Sélectionner une cible **Android** (terminal ou émulateur) et lancer.

### Reste à finir (TODO)
- Persister `AgentSession.PendingOfflineScans` en **SQLite** (`sqlite-net-pcl`) :
  actuellement en mémoire ; doit survivre à un redémarrage pendant une coupure.
- Écran « **attendus du jour** » (consomme `GetExpectedTodayAsync`) + resync à
  la reconnexion (afficher les conflits).
- Signal **sonore** au verdict (la vibration est déjà câblée).

---

## Historique / référence (conception)

## Ce qui est DÉJÀ prêt et testé côté serveur/partagé (à consommer)

- **Vérification ES256 hors ligne** : `NovAcces.Shared/Offline/OfflineQrVerifier`
  — vérifie un QR et la liste du jour avec la **clé publique seule**, hors ligne.
  Compatibilité croisée avec les signatures serveur **prouvée par tests**
  (`tests/NovAcces.UnitTests/Security/OfflineQrVerifierTests`). L'app embarque la
  clé publique (jamais la privée) et utilise cette classe.
- **Endpoints agent** (clé API de terminal, en-tête `X-Api-Key`) :
  - `GET /api/agent/expected-today` — attendus du jour (nom + statut + fenêtre).
  - `GET /api/agent/offline-list` — liste du jour signée (TTL 4h).
  - `POST /api/agent/resync` — confrontation des scans hors-ligne (conflits).
  - `POST /api/scan` — scan nominal en ligne (verdict + journalisation).
- DTOs partagés dans `NovAcces.Shared/Dtos/AgentDtos.cs`.

## Étapes de construction (sur ta machine)

> **Important** : les fichiers `.cs`/`.xaml` déjà présents dans ce dossier
> (Services/, ViewModels/, Pages/, MauiProgram.cs) ont été **écrits mais NON
> compilés** dans l'environnement de développement (pas de workload MAUI ni de
> terminal). Ils sont à intégrer dans le projet que TU scaffoldes ci-dessous, puis
> à compiler/tester chez toi. Le cœur logique et cryptographique qu'ils appellent
> (NovAcces.Shared/Offline) est, lui, **testé** (12 tests).

```bash
dotnet workload install maui        # si absent
cd src
# Scaffolde le shell dans un dossier temporaire (App, csproj, Platforms générés
# correctement pour ton workload), puis récupère ce shell dans NovAcces.Mobile.
dotnet new maui -n NovAcces.Mobile -o NovAcces.Mobile.shell
```

### Intégration des fichiers fournis
1. Copier depuis le shell généré : `App.xaml(.cs)`, `NovAcces.Mobile.csproj`,
   `Platforms/`, `Resources/` vers ce dossier (garder mes `Services/`,
   `ViewModels/`, `Pages/`, `MauiProgram.cs`).
2. Références et packages :
   ```bash
   dotnet add reference ../NovAcces.Shared/NovAcces.Shared.csproj
   dotnet add package ZXing.Net.MAUI
   dotnet add package sqlite-net-pcl          # persistance mode dégradé (voir §TODO)
   dotnet sln ../../NovAcces.sln add NovAcces.Mobile.csproj
   ```
3. Dans `App.xaml.cs` (généré), afficher la page de scan via l'injection :
   ```csharp
   public App(IServiceProvider services)
   {
       InitializeComponent();
       MainPage = new NavigationPage(services.GetRequiredService<Pages.ScanPage>());
   }
   ```
   (En .NET MAUI 9, utiliser `CreateWindow` selon le template — adapter.)
4. **Permission caméra Android** : dans `Platforms/Android/AndroidManifest.xml`,
   ajouter `<uses-permission android:name="android.permission.CAMERA" />` et
   demander la permission runtime au lancement de `ScanPage`.
5. Renseigner `AgentConfig` (URL API, clé API du terminal, clé PUBLIQUE ES256)
   dans `MauiProgram.cs` — à externaliser en stockage sécurisé à l'enrôlement.

### Fichiers déjà fournis (à compiler chez toi)
- `Services/AgentConfig.cs`, `Services/AgentApiClient.cs`, `Services/AgentSession.cs`
- `ViewModels/ScanViewModel.cs` (online → API ; offline → OfflineScanEvaluator)
- `Pages/ScanPage.xaml(.cs)` (caméra ZXing + verdict plein écran + bascule Entrée/Sortie + vibration)
- `MauiProgram.cs` (câblage DI, à fusionner)

### TODO à finir sur terminal
- **Persister `AgentSession.PendingOfflineScans` en SQLite** (sqlite-net-pcl) :
  actuellement en mémoire ; doit survivre à un redémarrage pendant une coupure.
- Charger `RefreshOfflineListAsync` au démarrage + périodiquement (en ligne).
- Écran « attendus du jour » (consomme `GetExpectedTodayAsync`) et resync à la
  reconnexion (afficher les conflits).
- Signal **sonore** au verdict (en plus de la vibration déjà câblée).

## Points d'attention (au-delà de la logique déjà testée dans Domain/Shared)

1. **Scan caméra** : `ZXing.Net.MAUI` (Android/iOS). Tester tôt sur un vrai
   terminal du parc Sigasécurité (autofocus, luminosité), pas seulement l'émulateur.
2. **Vérification de signature en local** : utiliser `OfflineQrVerifier` (déjà
   fourni, clé PUBLIQUE embarquée uniquement). C'est ce qui permet la
   vérification hors ligne — argument de vente à préserver.
3. **Mode dégradé** : au passage hors ligne, charger `GET /api/agent/offline-list`
   (liste signée + TTL), la stocker (SQLite `sqlite-net-pcl`). Un QR absent de la
   liste → « VÉRIFICATION IMPOSSIBLE — hors ligne ». TTL expiré → plus aucune
   validation locale. Chaque scan hors-ligne marqué `RecordedInDegradedMode`.
4. **Resynchronisation** : au retour en ligne, remonter les scans hors-ligne via
   `POST /api/agent/resync` ; afficher les conflits éventuels (QR révoqué pendant
   la coupure = événement de sécurité).
5. **Ergonomie (§11)** : verdict PLEIN ÉCRAN vert/bleu/rouge < 2 s, son +
   vibration, bascule poste Entrée ⇄ Sortie toujours visible, liste « attendus
   aujourd'hui » (nom + statut + fenêtre UNIQUEMENT — jamais motif/entreprise).
6. **Connectivité** : `IConnectivity` (Microsoft.Maui.Essentials) pour basculer
   automatiquement en mode dégradé.

## Rappel sécurité

- Ne JAMAIS embarquer la clé privée de signature dans l'app (clé publique seule).
- La logique de sûreté (fenêtre, anti-rejeu, cycle directionnel) reste **serveur**
  en mode nominal ; en mode dégradé, seule la vérification de signature + la liste
  signée du jour sont locales — la resynchronisation reconfronte tout au registre.
