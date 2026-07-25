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
| 1 | 🔴 Élevé | Anti-rejeu local & cycle directionnel non appliqués hors-ligne (état local « déjà sur site » non exploité par le ViewModel) | **Ouvert** — nécessite un état local (SQLite) reconstruit et une logique de cycle côté agent. À implémenter (cœur en `Shared`, câblage en VS). |
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

1. **Constat 1** — appliquer le cycle directionnel entrée/sortie et l'anti-rejeu
   « déjà sur site » hors-ligne, à partir de l'état local persistant. C'est le
   dernier vrai écart de sûreté du mode dégradé.
2. **Constats 4 et 5** — resync/refresh automatiques + bandeau dynamique.
3. **Constat 6** — externaliser la configuration du terminal.
4. Compiler + exécuter le projet dans Visual Studio, tester le scan caméra et le
   rendu audio sur un terminal réel du parc.
