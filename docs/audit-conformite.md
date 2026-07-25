# Audit de conformité — 23/07/2026

Audit ligne par ligne du scaffold Jalon 1 contre le CDC original et la
proposition v4. Voir aussi la section "Audit de conformité" du README.md
racine (matrice complète). Ce document donne le détail des corrections
déjà appliquées et de ce qui reste à faire.

## Corrections appliquées dans ce commit (ne pas régresser)

1. **Expiration cryptographique du QR jamais vérifiée** (REQ-SEC-04) —
   `ScanQrHandler.HandleAsync` compare désormais `verification.ExpiresAt`
   à l'horloge courante avant toute autre logique. Test :
   `ScanQrHandlerTests.HandleAsync_ExpiredCryptographicToken_
   IsRejectedAsSecurityEvent`.
2. **Endpoint de révocation manquant** (REQ-F-09) — ajouté
   `POST /api/visits/{visitId}/revoke`, `RevokeVisitHandler` créé.
3. **Mode 30 jours sans expiration réelle** (REQ-F-05) — `Visit.Scan`
   vérifie désormais `now > CreatedAt.AddDays(30)`. Test :
   `Scan_ThirtyDaysMode_AfterThirtyDayPeriod_IsDenied`.

## Gaps identifiés, NON corrigés, à traiter en priorité en Jalon 2

1. **Aucune interface `INotificationService`** — `CreateVisitHandler`
   génère le QR signé mais ne l'envoie nulle part. À créer dans
   `Application/Abstractions`, avec une implémentation WhatsApp Cloud API
   dans `Infrastructure` (voir accord-commercial.md pour les détails
   techniques : templates "Utility", QR envoyé en image).
2. **Aucun hook de notification temps réel de l'hôte** (REQ-F-06) —
   `ScanQrHandler` ne déclenche aucun événement. Prévoir soit un
   événement domaine consommé par un handler SignalR, soit un appel
   direct à un `IRealtimeNotifier` injecté.
3. **`IExclusionListService` est un stub qui retourne toujours `false`** —
   nécessite une vraie table `exclusion_entries` par tenant + interface
   d'administration côté dashboard sûreté.
4. **Aucune gestion des jours fériés ivoiriens** dans le calcul
   "jour ouvré" (actuellement : juste lundi-vendredi, codé en dur dans
   `ScanEndpoints.cs`). À paramétrer par site.
5. **Le garde-fou anti-doublon** (une seule demande active par visiteur,
   voir scenarios-fonctionnels.md section 8) n'est pas implémenté côté
   `CreateVisitHandler` — juste noté en TODO dans `VisitEndpoints.cs`.
6. **Pas de journal d'audit des actions d'administration** (qui a révoqué
   quoi, qui a modifié quel paramètre) — distinct du journal des scans,
   requis par la section 8.5 du CDC.

## Points de vigilance transversaux

- **Numérotation "REQ-SEC-06" et "REQ-F-11"** utilisée dans les documents
  commerciaux et dans les commentaires du code : ce sont des propositions
  du prestataire (voir note-analyse.md), pas des références du CDC
  original de Sigasécurité. Ne pas s'étonner de ne pas les retrouver si un
  jour on compare au CDC original tel quel.
- **Pas de tests d'intégration réels** (contre une vraie base PostgreSQL) —
  seulement des tests unitaires purs (Domain) et des tests avec doublures
  manuelles (Application). À ajouter en Jalon 2, notamment pour valider
  que le verrou `FOR UPDATE` fonctionne réellement sous concurrence (un
  test avec deux tâches parallèles scannant le même QR serait la meilleure
  preuve de REQ-SEC-03).
- **Aucune vérification que ce code compile réellement** — a été écrit
  sans accès à un SDK .NET. La toute première action en Jalon 2 doit être
  `dotnet build` + `dotnet test`, et la correction de toute erreur avant
  de continuer.

## Mise à jour 25/07/2026 — conformité données & traçabilité (Jalon 2)

Travail réalisé et **prouvé par tests** (88 tests verts : 43 unitaires +
45 d'intégration contre un vrai PostgreSQL). Les gaps de la section
précédente sont désormais **tous fermés** : 1 et 2 (notifications WhatsApp +
SignalR temps réel), 3 (liste d'exclusion réelle par tenant), 4 (jours fériés
paramétrables `BusinessDays`), 5 (anti-doublon `HasActiveVisitForVisitorAsync`),
6 (journal d'audit d'administration — ci-dessous).

### Rétention et purge des données personnelles (§7.3, ARTCI)
- `IDataRetentionService` + service de fond `RetentionMonitor` (passe
  quotidienne, balayage multi-sites) + déclenchement manuel
  `POST /api/admin/retention/run`, état `GET /api/admin/retention`.
- Les **demandes de visite** (PII : nom, téléphone, email) sont **supprimées**
  au-delà de `Retention:VisitRetentionDays`, **jamais** un visiteur encore sur
  site (la sécurité prime).
- Paramétrable par déploiement (`appsettings`, section `Retention`).

### Journal d'audit des actions d'administration (§8.5) — gap 6 fermé
- Table `admin_audit` **par site**, **inaltérable** (trigger append-only) :
  révocation de QR, ajout/retrait d'exclusion, purge, anonymisation.
- **Minimisé** : aucun nom de visiteur (références opaques), donc conservable
  long terme sans anonymisation. Consultation `GET /api/audit` (Sûreté/Admin).

### Conciliation « journal inaltérable » (§7.5) vs « rétention limitée » (§7.3)
Décision de sûreté prise (voir `DataRetentionService` et
`TenantProvisioningService.AppendOnlyJournalDdl`) : **anonymisation, jamais
suppression** des journaux. Au-delà de `Retention:JournalRetentionDays`, le
nom du visiteur dans `scan_logs` est remplacé par `[anonymisé]`. Le trigger
append-only n'autorise **que** cette transition (nom → sentinel, en avant,
vérifiée par diff `jsonb` robuste aux évolutions de schéma) ; DELETE, TRUNCATE
et toute autre modification restent rejetés au niveau base. Aucun fait de
sécurité (verdict, horodatage, agent, événement) ne peut donc être altéré.

### ⚠️ POINT OUVERT — durées légales à confirmer avec le client / l'ARTCI
Les valeurs par défaut sont des **propositions du prestataire, pas des
exigences chiffrées du client** :

| Paramètre | Défaut posé | À confirmer |
|---|---|---|
| `Retention:VisitRetentionDays` | 365 j | Durée de conservation des demandes de visite |
| `Retention:JournalRetentionDays` | 1095 j (3 ans) | Durée de conservation du nom dans le journal des scans |

**Action requise avant mise en production** : faire valider ces durées par
Sigasécurité au regard de la réglementation ivoirienne sur les données
personnelles (ARTCI) et des obligations propres à chaque site client. Ce sont
de simples paramètres `appsettings` (aucune reprise de code), mais la décision
doit être **écrite** et versée au dossier de conformité. Tant que ce n'est pas
tranché, les défauts ci-dessus s'appliquent.
