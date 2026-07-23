# Note d'analyse du CDC — Prestataire (v1.1, annexée au contrat)

> Ce document résume la note d'analyse remise par Mamadou KONATE avec son
> offre. Elle contient les propositions du prestataire qui COMPLÈTENT le
> CDC original — c'est ici que sont définies les doctrines "REQ-SEC-06" et
> "REQ-F-11" citées dans la proposition commerciale, qui n'existent pas
> sous ce numéro dans le CDC original du client (voir CLAUDE.md section 2).

## Points forts du CDC salués par le prestataire
Validation de fenêtre côté serveur (REQ-SEC-02), anti-rejeu atomique
(REQ-SEC-03), condition suspensive d'audit indépendant (7.6 — depuis
révisée, voir accord-commercial.md), mode dégradé exigé (REQ-FIAB-02).

## 9 points de vigilance soulevés, avec la doctrine retenue

### 1. Articulation validation serveur / mode dégradé — LA PLUS IMPORTANTE
**Constat** : REQ-SEC-02 (validation exclusivement serveur) et REQ-FIAB-02
(mode dégradé local) sont littéralement contradictoires.

**Doctrine retenue (référencée "REQ-SEC-06" dans les documents commerciaux,
numérotation propre au prestataire)** :
- Mode nominal (connecté) : validation serveur exclusive, aucune exception.
- Mode dégradé (coupure) : l'app agent bascule sur une **liste des QR
  valides du jour, signée cryptographiquement par le serveur**.
  - La liste a une durée de vie limitée (**TTL cible ≤ 4 heures**) : passé
    ce délai, l'app refuse toute validation locale.
  - Toute validation en mode dégradé est **marquée comme telle** dans le
    journal.
  - À la reconnexion, les scans hors ligne sont **resynchronisés et
    confrontés au registre central** : tout conflit anti-rejeu détecté
    a posteriori (même QR validé sur 2 postes pendant la coupure) est
    remonté comme événement de sécurité.
  - Une révocation émise pendant la coupure prend effet localement dès la
    synchronisation suivante.

### 2. Volumétrie absente du CDC
Hypothèses retenues pour le dimensionnement : 1 site pilote puis 10-15
sites à 24 mois ; 50 visiteurs/jour/site en moyenne, 200-300 en pic ;
cible de test de charge : ≥ 100 scans/minute soutenus 30 minutes ; 2 à 5
postes de contrôle simultanés par site.

### 3. RPO durci
Le CDC demandait RPO ≤ 24h ; le prestataire s'engage sur **RPO ≤ 4h**
(perdre une journée de QR generés serait inacceptable pour la sûreté).

### 4. SLA sans pénalités
Le prestataire propose un barème de pénalités contractuelles (voir
accord-commercial.md pour l'état final — actuellement pas de SLA formalisé
en phase pilote, "meilleurs efforts" avec objectif 99,5 %).

### 5. Conformité "exigences ivoiriennes" non nommée
Précisée : **loi n° 2013-450 du 19 juin 2013** relative à la protection des
données à caractère personnel + formalités **ARTCI**. Le prestataire
accompagne Sigasécurité dans la constitution du dossier technique.

### 6. Coûts de notification isolés
Initialement SMS, **remplacé par WhatsApp Business Platform** (API
officielle Meta Cloud API) en cours de négociation — voir
accord-commercial.md.

### 7. Réversibilité outillée
Plan de réversibilité remis dès la recette, export intégral en formats
ouverts (dump SQL, CSV/JSON, journaux inclus), assistance de réversibilité
optionnelle (3 mois, 400 000 FCFA).

### 8. Ergonomie terrain de l'app agent
Verdict de scan < 2 secondes, interface fort contraste plein écran
(vert/rouge/bleu), signaux sonore et vibratoire, compatible terminaux
d'entrée de gamme du parc QR Patrol existant.

### 9. Cas fonctionnels ajoutés (référencés "REQ-F-11" dans les documents
commerciaux, numérotation propre au prestataire, non présents dans le CDC
original)
- **Visiteurs récurrents** : réutilisation des identités déjà enregistrées.
- **Liste d'exclusion par site** : toute demande ou tout scan concernant
  une personne exclue génère une alerte à la sûreté, **sans divulguer le
  motif à l'agent** au poste de contrôle (principe du moindre privilège).

## Questions formelles posées en phase Q/R (pour référence historique)
Doctrine mode dégradé, hypothèses de volumétrie, modèles de terminaux du
parc QR Patrol, statut des formalités ARTCI existantes, préférence
agrégateur (devenue sans objet — WhatsApp retenu), cadre budgétaire, RPO,
canaux de notification, connectivité de secours des sites, gouvernance de
la liste d'exclusion.

## Engagements de principe du prestataire
Réponse point par point aux exigences en tableau de conformité, acceptation
du principe de validation de sécurité avant mise en production (modalités
finalement revues, voir accord-commercial.md), offre financière décomposée,
cession de PI du code spécifique avec transparence sur les composants
génériques, SLA (modalités revues, voir accord-commercial.md).
