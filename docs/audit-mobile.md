# Audit de l'application agent (MAUI) — 25/07/2026

Revue du code mobile existant contre `docs/scenarios-fonctionnels.md` (§6 mode
dégradé, §11 moindre privilège) et le CLAUDE.md. Effectuée par lecture : le
projet MAUI **ne se compile pas en ligne de commande** dans cet environnement
(workloads MAUI sur bande 10.0.100, SDK CLI 10.0.200 — cf.
`src/NovAcces.Mobile/README.md`). Seul Visual Studio compile le projet.

## Constat général

Le mobile est **bien plus avancé** que ce qu'affirmait le README racine
(« non commencé » = faux) : ScanPage/ViewModel, ExpectedTodayPage, AgentApiClient,
OfflineScanStore, vérification ES256 hors-ligne (clé publique) avec tests de
compatibilité croisée, file SQLite persistante, moindre privilège sur les
attendus, retour non-visuel (vibration + voix), bascule directionnelle.

Le README **mobile**, lui, dit « complet » — ce qui **surévaluait le mode
dégradé**, où se concentraient les vrais écarts. Corrigés ci-dessous.

## Constats et suites données

| # | Gravité | Constat | Suite |
|---|---|---|---|
| 1 | 🔴 Élevé | Anti-rejeu local & cycle directionnel non appliqués hors-ligne (état local « déjà sur site » non exploité par le ViewModel) | **Corrigé** — `OfflineScanEvaluator` applique le cycle directionnel + l'anti-rejeu local (miroir de `Domain/Visit.Scan`) ; `OfflineOnSiteState` reconstruit l'état sur site (instantané serveur transporté dans la liste signée + scans locaux). Testé (7 tests unitaires). Câblage `ScanViewModel`/`AgentSession` à valider en VS. |
| 2 | 🔴 Élevé | Journalisation centrale incomplète : `/resync` ne journalisait que les conflits ; refus hors-ligne non remontés | **Corrigé** — `/resync` journalise désormais CHAQUE scan (accordé/refusé/conflit, marqué mode dégradé) ; le client met en file tous les scans porteurs d'un token. Testé (intégration + unitaire). |
| 3 | 🟠 Moyen/Élevé | Expiration cryptographique du QR ignorée hors-ligne (mode 30 jours sans contrôle d'expiration) | **Corrigé** — `OfflineScanEvaluator` rejette `now > token.ExpiresAt` (`OfflineOutcome.Expired`). Testé (unitaire). |
| 4 | 🟠 Moyen | Pas de resync/refresh auto à la reconnexion (`ConnectivityChanged` non écouté) | **Corrigé (MAUI, à valider en VS)** — `ScanPage` s'abonne à `ConnectivityChanged` : au retour en ligne, rafraîchit la liste hors-ligne et resynchronise automatiquement les scans en attente (conflits signalés à l'agent). |
| 5 | 🟡 Faible | Bandeau connectivité statique (mis à jour seulement `OnAppearing`) | **Corrigé (MAUI, à valider en VS)** — bandeau mis à jour à chaque changement de connectivité. |
| 6 | 🟡 Info | `AgentConfig` en dur avec placeholders (URL, clé API, clé publique) | **Corrigé (MAUI, à valider en VS)** — config chargée depuis `SecureStorage` (`AgentConfig.LoadAsync`/`SaveAsync`, `IsEnrolled`) ; plus aucun secret en dur ; vérificateur ES256 construit paresseusement (un terminal non enrôlé ne plante plus au démarrage). Reste à créer l'écran d'enrôlement qui appelle `SaveAsync`. |

## Ce qui était déjà correct (conforme §6)

QR absent de la liste → « vérification impossible », non-sécurité (§6.2) ·
TTL expiré → validation impossible (§6.3) · révocation prise en compte au
retour en ligne (§6.4) · exclusion → refus générique (moindre privilège) ·
fenêtre −20/+15 répliquée (Unique) · file SQLite survit au redémarrage ·
attendus limités à nom + statut + fenêtre (§11).

## Reste à faire

Les 6 constats de l'audit sont traités. Restent des tâches **non vérifiables
hors Visual Studio / terminal réel** :

1. **Compiler le projet dans Visual Studio** et corriger toute erreur (les
   corrections MAUI — constats 4/5/6 — ont été écrites sans compilation CLI
   possible dans cet environnement).
2. **Écran d'enrôlement** appelant `AgentConfig.SaveAsync` (saisie/QR de la clé
   API, clé publique, URL) — pour rendre le constat 6 pleinement opérationnel.
3. **Tests sur terminal réel** : scan caméra (autofocus, luminosité), rendu
   audio (synthèse vocale), et surtout le **mode dégradé bout-en-bout** (cycle
   directionnel + anti-rejeu local + resync automatique à la reconnexion).

Les écarts de **sûreté** du mode dégradé (constats 1, 2, 3) sont corrigés et
**testés** côté `Shared`/API. Les constats 4, 5, 6 sont du câblage MAUI, écrit
mais **à compiler/valider en VS**.
