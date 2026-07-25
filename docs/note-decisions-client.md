# Note de décision — évolutions hors périmètre à arbitrer

**Version** : 1.0 · **Date** : 25/07/2026 · **Pour** : Sigasécurité — Direction des Opérations (M. Kodjo)
**De** : Mamadou KONATE (prestataire)

Trois évolutions ont été identifiées pendant le développement. Elles **renforcent
le produit** (surtout pour la revente multi-tenant) mais **sortent du périmètre
contractuel actuel** (CDC + note d'analyse). Aucune n'est bloquante pour le pilote
SICOPA. Cette note les présente pour décision ; les chiffrages sont **indicatifs**
(base avenant : 90 000 FCFA / jour-homme) et à confirmer.

## 1. Identification individuelle de l'agent (« prise de poste »)

- **Aujourd'hui** : l'app agent authentifie le **terminal** (clé API), pas la
  personne. Le journal attribue les scans au poste, pas à un agent nommé.
- **Enjeu** : la maquette affiche une identité agent ; le CDC (REQ-F-07, §8.5)
  mentionne « agent » dans la traçabilité. Pour une société agréée, savoir **qui**
  tenait le poste lors d'un incident a une valeur réglementaire et assurantielle.
- **Recommandation** : ajouter une **prise de poste** en début de service
  (badge agent scanné, ou matricule + PIN **vérifié serveur**), qui tamponne
  chaque scan avec l'agent. Léger, sans mot de passe/2FA sur le terminal partagé.
- **Chiffrage indicatif** : ~3 à 5 jours-homme.

## 2. Clé de signature par site (isolation cryptographique multi-tenant)

- **Aujourd'hui** : une **seule** paire de clés ES256 signe les QR de **tous les
  sites** d'un déploiement. L'isolation est au niveau des données (schéma par
  tenant), pas de la cryptographie.
- **Enjeu** : en revente à des clients **indépendants**, une clé unique = « sort
  partagé » — une compromission toucherait tous les clients.
- **Recommandation** : passer à **une paire de clés par site/tenant**, pour
  confiner toute compromission à un seul client. Acceptable de garder la clé
  globale pour le **pilote** (un site) ; à faire évoluer avant la phase revente.
- **Chiffrage indicatif** : ~3 à 4 jours-homme.

## 3. Console de gestion des terminaux + QR d'enrôlement

- **Aujourd'hui** : les terminaux sont **statiques en configuration** serveur.
  Ajouter/retirer un terminal = éditer la config + redémarrer. Pas de révocation
  ni de rotation de clé ; la clé se recopie à la main.
- **Enjeu** : ingérable à l'échelle multi-tenant (plusieurs clients, plusieurs
  postes), et pas de réponse en cas de terminal volé/perdu.
- **Recommandation** : une **console d'administration des terminaux** (création,
  révocation, rotation, par site) qui **génère une clé unique** et produit un
  **QR d'enrôlement** ; l'agent **scanne ce QR** sur l'appareil pour l'enrôler
  (zéro saisie manuelle). Réutilise la crypto et le geste « scanner » déjà en place.
- **Chiffrage indicatif** : ~5 à 8 jours-homme.

## Synthèse

| # | Évolution | Priorité | Indicatif (j-h) |
|---|---|---|---|
| 1 | Prise de poste agent | Recommandée (traçabilité) | 3–5 |
| 2 | Clé de signature par site | Avant phase revente | 3–4 |
| 3 | Console terminaux + QR d'enrôlement | Avant phase revente | 5–8 |

**Décision attendue** : confirmer si l'une ou l'autre entre dans le périmètre du
pilote (auquel cas avenant), ou est planifiée pour la phase de déploiement
multi-clients. Le pilote SICOPA peut démarrer **sans** ces trois évolutions.
