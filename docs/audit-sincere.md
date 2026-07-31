# Audit sincère — NovAcces

> Auto-évaluation franche de l'état réel de l'application, sans filtre
> commercial. Distingue ce qui est **prouvé**, ce qui est **écrit mais non
> vérifié**, et ce qui est **absent ou risqué**. Date : 25/07/2026.

## Verdict global

**Base de code sérieuse et bien architecturée, mais PAS un produit prêt à
livrer.** Socle Jalon 2 solide qui exige trois choses qu'aucune ligne de code ne
remplace : une **relecture humaine**, une **vérification réelle de l'UI et du
Mobile**, et un **test des notifications en conditions réelles**.

## Audit par domaine

| Domaine | État réel | Vérifié ? | Risque | Action requise |
|---|---|---|---|---|
| Logique de sûreté (Domain) | Complète, conforme maquette | ✅ (tests + rejeu API) | Faible | Relecture humaine §7 |
| Crypto ES256 | Solide, native | ✅ | Faible | — |
| Cloisonnement multi-tenant | Robuste (claim, search_path) | ✅ | Faible | Relecture humaine §7 |
| Journal append-only | Triggers DB actifs | ✅ (constaté en base) | Faible | — |
| Anti-rejeu | Verrou FOR UPDATE transactionnel | ✅ (test concurrence) | Faible | — |
| Auth / RBAC / 2FA | Complet, durci | ✅ (tests) | Faible-moyen | Relecture humaine |
| Interface Web (Blazor) | Écrite ; logique de qq composants testée (bUnit) | 🔴 **Parcours complet non cliqué** | **Élevé** | **Smoke test au F5** |
| Mobile (MAUI) | Écrit, jamais compilé | 🔴 **Non compilé** | **Élevé** | **Compiler en VS + terminal** |
| Notifications WhatsApp/Email | Codées, messages soignés | 🟠 **Jamais envoyées** | **Moyen-élevé** | **1 test réel (Meta + SMTP)** |
| Tests automatisés | 95 au vert (dont 15 composants Blazor) | ✅ | Moyen | Couverture UI encore partielle |
| Déploiement / VPS / TLS / charge | Inexistant | 🔴 | **Élevé** | Tout reste à faire |
| Données réelles / multi-tenant sous charge | Synthétiques, 1 base dev | 🔴 | Moyen | Éprouver en pilote |

## Findings de sécurité (revue interne)

| Finding | Sévérité | État |
|---|---|---|
| Fuite temps réel inter-tenant (hub SignalR) | 🔴 Élevée | ✅ Corrigé + test |
| Injection de formule CSV (export journal) | 🟠 Moyenne | ✅ Corrigé + test |
| `/api/auth` non rate-limitée | 🟠 Moyenne | ✅ Corrigé |
| En-têtes de sécurité HTTP absents | 🟠 Moyenne | ✅ Corrigé + test |
| Ré-exposition secret TOTP (`/2fa/setup`) | 🟡 Basse | ✅ Corrigé + test |
| Noms de visiteurs (PII) dans les logs | 🟡 Basse | ✅ Minimisé |
| Énumération de comptes par canal temporel | 🟡 Basse | ✅ Corrigé (leurre à temps constant) |

**Fait marquant, à ne pas édulcorer** : une fuite inter-tenant réelle (hub
SignalR) avait été livrée dans des commits antérieurs et les tests ne l'ont pas
attrapée jusqu'à une revue ciblée. Une revue par une seule IA a des angles
morts — la relecture humaine et l'audit externe restent nécessaires.

## Périmètre contractuel

| Élément | Statut |
|---|---|
| API + Web (Jalon 2) | Écrit ; UI non prouvée |
| Socle Mobile (Jalon 2) | Écrit ; non compilé |
| App agent terminal réel (Jalon 3) | À faire |
| Recette de sécurité interne | ✅ `docs/rapport-recette-securite.md` |
| Audit d'intrusion externe | ⚠️ Écarté au contrat — recommandé avant client tiers |
| Invitation groupée | 🟠 Hors CDC → avenant |
| Notifications enrichies (modes/templates) | 🟠 Hors CDC → avenant |

## À faire — fermement — avant de dire « prêt »

1. **Smoke test Web** (toi) : connexion des 3 rôles, création de visite, toast,
   modale groupe, dashboard temps réel.
2. **Compiler le Mobile en VS**, corriger, tester le scan sur un vrai terminal.
3. **Tester une vraie notification** (WhatsApp de test + SMTP réel), au moins une fois.
4. **Relecture humaine des zones §7** avant merge — non négociable (contrat).
5. **Pas de déploiement chez un client tiers** sans l'audit externe recommandé.

## Ce qui a été corrigé suite à cet audit

- **Énumération par canal temporel** : leurre de hachage à temps constant pour
  les emails inconnus (`AuthEndpoints`).
- **Angle mort UI** partiellement réduit : tests de composants Blazor (bUnit)
  ajoutés sur les composants isolés testables (`PasswordBox`, `ToastHost`) +
  test unitaire de `SiteSlug`. Ne remplace pas le smoke test manuel, mais
  couvre la logique de ces composants.

## Verdict final, réglo

Le **cœur (sûreté, crypto, multi-tenant, journal) est solide, testé et durci**.
Mais **l'UI Web n'est pas prouvée, le Mobile n'est pas compilé, les
notifications ne sont pas testées en réel**, et une IA seule ne remplace pas une
relecture humaine. Bon socle Jalon 2 — pas un produit fini.
