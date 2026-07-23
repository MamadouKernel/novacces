# Cahier des charges original du client — NOVACCÈS

**Référence** : CDC-DEV-NOVACCES-2026-001 · **Version** : 1.0 · **Date** : 17/07/2026
**Émetteur** : Sigasécurité — Direction des Opérations

> Ce document est la version ORIGINALE reçue du client. C'est elle qui fait
> foi juridiquement (annexée au contrat), pas une éventuelle réécriture.
> La note d'analyse (`note-analyse.md`) contient les propositions
> d'amélioration du prestataire, annexées séparément.

## 1. Objet
Sélection d'un prestataire pour concevoir, développer, sécuriser, tester et
déployer NOVACCÈS : solution de gestion digitalisée des visiteurs par QR
Code d'invitation, en architecture multi-clients (multi-tenant), pour les
sites de Sigasécurité (industriels, agro-industriels, portuaires, logistiques).

## 2. Contexte
Sigasécurité : entreprise de sécurité privée agréée (Agrément n° 344 MI),
Abidjan + San Pedro + sites intérieurs. Prestations : gardiennage, QR Patrol
(rondes digitalisées), vidéosurveillance, contrôle d'accès, géolocalisation
flotte, télésurveillance, incendie/secourisme. Le prestataire s'inscrit dans
l'écosystème existant, notamment le parc de smartphones déjà déployé pour
QR Patrol.

## 3. Périmètre attendu
Conception détaillée, développement (portail web hôte, API centrale, app
mobile agent), infrastructure d'hébergement, recette fonctionnelle et
sécurité, déploiement pilote puis multi-clients, documentation, transfert
de compétences, maintenance 12 mois minimum recommandé.

## 4. Exigences fonctionnelles majeures

| Réf. | Exigence |
|---|---|
| REQ-F-01 | Portail web hôte : création demande de visite (identité, entreprise, motif, dates, email/téléphone) |
| REQ-F-02 | Génération automatique QR Code unique, chiffré, à la validation |
| REQ-F-03 | Transmission automatique du QR par email et/ou SMS |
| REQ-F-04 | App mobile agent : lecture QR, affichage instantané statut (autorisé/refusé + motif) |
| REQ-F-05 | Deux modes : accès unique (fenêtre stricte -20/+15 min) et accès 30 jours (jours ouvrés uniquement) |
| REQ-F-06 | Notification temps réel de l'hôte à l'arrivée du visiteur |
| REQ-F-07 | Journalisation de chaque tentative de scan (acceptée/refusée), horodatage, agent, motif |
| REQ-F-08 | Tableau de bord : visiteurs présents, historique, export CSV/Excel |
| REQ-F-09 | Révocation manuelle du QR par hôte ou sûreté, à tout moment |
| REQ-F-10 | Architecture multi-tenant, déploiement indépendant multi-sites |

## 5. Architecture technique
Couches : app mobile agent / portail web hôte / API centrale / base de
données / notification — communication exclusivement HTTPS/TLS. App agent
Android, mode dégradé hors connexion (cache local QR valides du jour,
synchronisation différée). Base cloisonnée par client. API REST documentée.
Hébergement au choix du prestataire (cloud souverain ou régional privilégié).

## 6. Fiabilité et disponibilité

| Réf. | Exigence | Niveau |
|---|---|---|
| REQ-FIAB-01 | Disponibilité service central | ≥ 99,5 % mensuel, hors maintenance planifiée notifiée |
| REQ-FIAB-02 | App agent en coupure réseau | Mode dégradé obligatoire : vérification locale QR valides du jour, sync différée |
| REQ-FIAB-03 | Sauvegardes | Quotidiennes automatiques, chiffrées, test de restauration trimestriel |
| REQ-FIAB-04 | PRA | RTO ≤ 4h, RPO ≤ 24h |
| REQ-FIAB-05 | Supervision | Monitoring proactif, alerte automatique |
| REQ-FIAB-06 | Tests de charge | Volume représentatif d'un site à fort flux, avant mise en production |
| REQ-FIAB-07 | Traçabilité versions | Environnements séparés dev/recette/prod, rollback documenté |

## 7. Sécurité — section déterminante

### 7.1 Sécurité du QR et anti-fraude
- REQ-SEC-01 : QR sans donnée personnelle en clair — identifiant de visite chiffré et signé uniquement
- REQ-SEC-02 : vérification de fenêtre de validité **exclusivement côté serveur**, jamais côté app mobile
- REQ-SEC-03 : anti-rejeu — QR à passage unique marqué consommé de façon atomique dès le premier scan validé, y compris scans simultanés depuis plusieurs postes
- REQ-SEC-04 : résistance à la copie/capture d'écran/falsification (signature cryptographique vérifiable, expiration intégrée)
- REQ-SEC-05 : toute tentative hors fenêtre ou QR déjà consommé = journalisée ET remontée comme **événement de sécurité**, pas un simple refus fonctionnel

### 7.2 Sécurité applicative
OWASP Top 10, authentification forte + 2FA obligatoire pour comptes à
privilèges (admin Sigasécurité, responsables sûreté client), gestion
sécurisée des sessions, validation systématique côté serveur, TLS 1.2
minimum, rate limiting sur endpoints sensibles.

### 7.3 Sécurité des données et conformité
Chiffrement au repos et en transit, cloisonnement multi-tenant étanche,
durée de conservation limitée et paramétrable par client avec purge
automatique (conformité protection des données personnelles ivoirienne),
traçabilité complète et non répudiable des accès aux données, localisation
d'hébergement précisée avec garanties.

### 7.4 Infrastructure
Hébergement durci (WAF, segmentation réseau, accès administrateurs
restreints et journalisés), correctifs de sécurité selon procédure et
délai documentés, sauvegardes chiffrées isolées de la production
(protection rançongiciels), séparation stricte dev/recette/prod.

### 7.5 Habilitations et traçabilité
RBAC aligné sur 4 profils : Hôte, Responsable sûreté client, Agent de
contrôle d'accès, Administrateur Sigasécurité. Principe du moindre
privilège. Journal d'audit inaltérable (logs protégés en écriture).

### 7.6 Tests et validation de sécurité obligatoires

> **Condition suspensive de mise en production (CDC original)** :
> aucune mise en production sur un site client sans audit de sécurité
> (test d'intrusion) préalable par une entité indépendante du
> développeur, et correction de toute vulnérabilité critique ou élevée.
>
> **⚠️ ÉTAT ACTUEL (voir accord-commercial.md)** : Sigasécurité a
> finalement choisi de NE PAS recourir à cet audit externe pour la phase
> pilote. Un dispositif de remplacement (tests internes documentés +
> analyse OWASP) a été convenu, avec l'audit externe maintenu en
> recommandation forte avant tout déploiement chez un client tiers.

Pentest applicatif et infrastructure avant pilote, puis annuel. Tests
unitaires/intégration sur fonctions critiques. Test de charge représentatif
d'un pic. Recette de sécurité fonctionnelle spécifique (scan hors fenêtre,
réutilisation QR consommé, altération QR, usurpation compte hôte).

### 7.7 Incidents et MCS
Procédure documentée, notification sous 24h. Délai de correction selon
criticité (critique 48h, élevée 7j, moyenne 30j). Veille et mises à jour
régulières.

## 8. Qualité, méthodologie, livrables
Méthodologie agile ou cycle en V avec jalons de validation. Documentation
technique et utilisateur complète. Code source livré avec PI transférée.
Rapport d'audit sécurité et test de charge avant mise en production.
Transfert de compétences.

## 9. Composition du dossier de réponse
Présentation prestataire, offre technique (réponse point par point),
note méthodologique sécurité, planning, offre financière, engagements
contractuels (SLA, garanties, réversibilité).

## 10. Critères d'évaluation

| Critère | Pondération |
|---|---|
| Sécurité et fiabilité | 35 % |
| Conformité fonctionnelle | 25 % |
| Architecture technique et évolutivité | 15 % |
| Méthodologie, qualité, références | 10 % |
| Coût global | 10 % |
| Délai proposé | 5 % |

## 12. Confidentialité, PI, réversibilité
NDA préalable. Cession intégrale du code et PI à réception définitive.
Clause de réversibilité (reprise par un tiers si nécessaire).
