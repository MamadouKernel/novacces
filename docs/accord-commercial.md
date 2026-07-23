# Accord commercial final — Proposition v4 (base du contrat signé)

> Historique des versions commerciales, pour comprendre le contexte si des
> traces d'anciens montants apparaissent ailleurs (échanges, anciens
> documents) : v1 (300 000 FCFA + abonnement mensuel 100 000 FCFA/site,
> 24 mois d'engagement) → client demande un modèle sans récurrent engageant
> → v3 (développement 1,5 à 2,4M selon 3 niveaux de périmètre + hébergement
> annuel 600 000) → négociation avec M. Kodjo → **v4 = accord FINAL signé**.

## Termes financiers définitifs

- **Développement (forfait unique, niveau "Complet")** : **2 000 000 FCFA**
  - Jalon 1 (signature) : 600 000 FCFA
  - Jalon 2 (recette fonctionnelle) : 800 000 FCFA
  - Jalon 3 (mise en production pilote) : 600 000 FCFA
- **Récurrent annuel unique** : **500 000 FCFA/an**, payé d'avance à la mise
  en production puis à chaque date anniversaire. Couvre :
  - Hébergement infogéré complet (serveur, supervision, TLS, correctifs,
    sauvegardes chiffrées quotidiennes isolées, test de restauration
    trimestriel, nom de domaine/certificats)
  - Maintenance de base (veille vulnérabilités, corrections d'anomalies,
    support jours ouvrés)
  - **Aucun engagement pluriannuel** : reconduction libre chaque année.
    Non-reconduction = arrêt du service + restitution intégrale des
    données en formats ouverts, sans frais.
- **Garantie corrective** : 3 mois à compter de la mise en production,
  incluse dans le forfait.

## Hors du récurrent de base (sur devis séparé)
- Évolutions fonctionnelles nouvelles hors périmètre CDC + note d'analyse :
  90 000 FCFA/jour-homme
- Astreinte formalisée hors jours ouvrés
- Audit d'intrusion indépendant (recommandé, non imposé) : 2 500 000 à
  4 000 000 FCFA, refacturé sans marge

## Déploiement de nouveaux sites clients (après le pilote)
- Mise en service par site : 400 000 FCFA (provisionnement tenant,
  paramétrage, comptes, enrôlement terminaux, prise en main)
- Hébergement additionnel : + 150 000 FCFA/an par site

## Sécurité — décision actée
Sigasécurité a choisi de **ne pas recourir à l'audit d'intrusion externe**
pour la phase pilote (décision du maître d'ouvrage, constatée par écrit).
En remplacement, inclus dans le forfait Jalon 3 :
- Recette de sécurité interne documentée (tous les scénarios REQ-SEC du CDC)
- Tests automatisés des fonctions critiques
- Analyse de vulnérabilités (OWASP Top 10, dépendances) avec rapport remis
  avant mise en production

L'audit externe reste **recommandé formellement** avant tout déploiement
chez un client tiers de Sigasécurité — la responsabilité du prestataire
étant alors limitée à la bonne exécution du dispositif de tests internes.

## Notifications : WhatsApp (pas SMS)
API officielle **WhatsApp Business Platform (Meta Cloud API)**. Accès API
gratuit ; messages "utility" facturés au réel (3-8 FCFA/message, zone
"Rest of Africa", ≈ 7 000-12 000 FCFA/mois pour un site à 50 visiteurs/jour).
Repli automatique par email. Prérequis à initier dès le cadrage : compte
Meta Business vérifié au nom de Sigasécurité, numéro dédié, approbation des
templates (24-72h, catégorie "Utility" recommandée pour le coût et la
facilité d'approbation).

## Perspective non engageante
Sigasécurité a exprimé son intention de confier ensuite au prestataire la
reprise de son logiciel de gestion de la télésurveillance — pas encore
chiffré, cadrage dédié à venir après NovAcces.

## Engagements de méthode envers le client
- Démonstration d'avancement **toutes les deux semaines**
- Procès-verbal simple à chaque jalon, signé des deux parties
- Mise en production après encaissement du dernier jalon uniquement
- Propriété intellectuelle transférée au parfait paiement du forfait

## Ce que Claude Code doit en retenir concrètement
- Ne pas développer de fonctionnalité d'audit d'intrusion automatisé
  poussé — le dispositif de sécurité est la recette interne + OWASP, pas
  un outil de pentest à construire.
- Le canal de notification à implémenter est **WhatsApp**, pas de code SMS
  à écrire.
- Le récurrent étant unique (pas de distinction hébergement/maintenance
  facturée séparément), aucune logique de facturation différenciée n'est
  nécessaire côté produit — c'est purement contractuel.
- Respecter le rythme de démonstration bimensuelle en priorisant un
  parcours fonctionnel complet plutôt que des couches isolées.
