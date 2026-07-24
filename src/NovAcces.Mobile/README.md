# NovAcces.Mobile (.NET MAUI — Android) — plan de construction

L'application agent de contrôle d'accès : scan caméra, verdict plein écran,
mode dégradé, poste directionnel entrée/sortie, liste des attendus du jour.

> **À construire sur une machine avec le workload MAUI installé et un terminal
> Android de test.** Le scaffolding + build MAUI et le test caméra ne sont pas
> réalisables dans l'environnement CI/headless où le reste a été développé.

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

```bash
dotnet workload install maui        # si absent
cd src
dotnet new maui -n NovAcces.Mobile -o NovAcces.Mobile --force
cd NovAcces.Mobile
dotnet add reference ../NovAcces.Shared/NovAcces.Shared.csproj
dotnet add package ZXing.Net.MAUI              # scan caméra
dotnet add package sqlite-net-pcl              # stockage local mode dégradé
dotnet sln ../../NovAcces.sln add NovAcces.Mobile.csproj
```

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
