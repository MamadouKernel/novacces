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
| 4 | 🟠 Moyen | Pas de resync/refresh auto à la reconnexion (`ConnectivityChanged` non écouté) | **Ouvert** — à câbler côté MAUI (vérif en VS). |
| 5 | 🟡 Faible | Bandeau connectivité statique (mis à jour seulement `OnAppearing`) | **Ouvert** — UX, câblage MAUI. |
| 6 | 🟡 Info | `AgentConfig` en dur avec placeholders (URL, clé API, clé publique) | **Ouvert (tâche)** — à externaliser en stockage sécurisé à l'enrôlement du terminal. Bloquant pour un déploiement réel, pas pour la recette. |

## Ce qui était déjà correct (conforme §6)

QR absent de la liste → « vérification impossible », non-sécurité (§6.2) ·
TTL expiré → validation impossible (§6.3) · révocation prise en compte au
retour en ligne (§6.4) · exclusion → refus générique (moindre privilège) ·
fenêtre −20/+15 répliquée (Unique) · file SQLite survit au redémarrage ·
attendus limités à nom + statut + fenêtre (§11).

## Reste à faire (priorité)

1. **Constats 4 et 5** — resync/refresh automatiques (`ConnectivityChanged`) +
   bandeau connectivité dynamique. Purement MAUI.
2. **Constat 6** — externaliser la configuration du terminal (`AgentConfig`).
3. Compiler + exécuter le projet dans Visual Studio, tester le scan caméra, le
   rendu audio et le mode dégradé (cycle directionnel + resync) sur un terminal
   réel du parc.

Les écarts de sûreté du mode dégradé (constats 1, 2, 3) sont désormais tous
corrigés et testés côté `Shared`/API. Ne restent que du câblage MAUI (à valider
en VS) et de l'exploitation.
