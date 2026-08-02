# Note de décision — évolutions hors périmètre à arbitrer

**Version** : 1.0 · **Date** : 25/07/2026 · **Pour** : Sigasécurité — Direction des Opérations (M. Kodjo)
**De** : Mamadou KONATE (prestataire)

Trois évolutions ont été identifiées pendant le développement. Elles **renforcent
le produit** (surtout pour la revente multi-tenant) mais **sortent du périmètre
contractuel actuel** (CDC + note d'analyse). Aucune n'est bloquante pour le pilote
SICOPA. Cette note les présente pour décision ; les chiffrages sont **indicatifs**
(base avenant : 90 000 FCFA / jour-homme) et à confirmer.

## 1. Identification individuelle de l'agent (« prise de poste ») — ✅ réalisé (26/07/2026)

- **Aujourd'hui** : l'app agent authentifie le **terminal** (clé API), pas la
  personne. Le journal attribue les scans au poste, pas à un agent nommé.
- **Enjeu** : la maquette affiche une identité agent ; le CDC (REQ-F-07, §8.5)
  mentionne « agent » dans la traçabilité. Pour une société agréée, savoir **qui**
  tenait le poste lors d'un incident a une valeur réglementaire et assurantielle.
- **Recommandation** : ajouter une **prise de poste** en début de service
  (badge agent scanné, ou matricule + PIN **vérifié serveur**), qui tamponne
  chaque scan avec l'agent. Léger, sans mot de passe/2FA sur le terminal partagé.
- **Chiffrage indicatif** : ~3 à 5 jours-homme.
- **Réalisé** : matricule + PIN vérifié serveur (`PasswordHasher` PBKDF2), jeton
  de poste signé joint à chaque scan, gestion des agents dans la console Admin.
  Extension additionnelle : un terminal peut désormais servir **plusieurs
  sites** (liste blanche en configuration serveur, l'agent choisit le site à la
  prise de poste, revalidé côté serveur) — décision prise avec Mamadou le
  26/07/2026, gardée en configuration (pas de table d'enrôlement) pour ne pas
  empiéter sur le périmètre de l'évolution #3 ci-dessous.
  **Addendum (01/08/2026)** : un agent (personne) n'est pas figé sur un site
  — il peut faire une période sur le site A puis être affecté au site B.
  Comme chaque site est un schéma isolé (frontière de cloisonnement
  multi-tenant, §7.3), l'agent reste rattaché à un site à la fois : la
  réaffectation crée un nouveau matricule+PIN sur le site B (déjà possible
  via la console Admin), et **désactive** l'ancien enregistrement sur le
  site A pour ne pas laisser un PIN valide indéfiniment sur un site quitté
  (capacité ajoutée le 01/08/2026 — `Agent.Deactivate()` existait côté
  domaine mais n'était exposée par aucun endpoint).

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

## 3. Console de gestion des terminaux + QR d'enrôlement — ✅ réalisé (31/07/2026)

- La console Admin crée, liste et révoque les terminaux par site.
- L'Admin génère un ticket QR temporaire, valable quelques minutes et utilisable une seule fois.
- Le Mobile scanne le QR, génère une paire de clés, active le device et reçoit automatiquement une nouvelle clé API.
- Le ticket est hashé en base, invalidé après activation ou lors de la génération d'un nouveau QR.
- L'activation, la rotation de clé, la révocation et chaque requête API sont tracées.

## 4. Nom commercial de l'application — ✅ décidé (02/08/2026)

- **Décision de Sigasécurité** : l'application s'appelle désormais **SigasAcces**
  (remplace « NovAcces », nom de travail utilisé depuis le cahier des charges
  original du 17/07/2026).
- **Domaine acheté** : `sigasacces.com` (VPS Contabo, Hub Europe — voir
  `docs/deploiement.md` §1).
- **Portée du changement** : tout ce que voit l'utilisateur — logo, titres de
  page, pied de page, badge visiteur imprimé, emails automatiques (QR
  d'invitation, notifications hôte), nom affiché dans l'application
  d'authentification (2FA), titre de l'app mobile agent. Les noms techniques
  internes (projets .NET, espaces de noms C#, dépôt Git) restent `NovAcces` —
  changement cosmétique uniquement, aucun impact fonctionnel ni migration de
  données nécessaire.

## 5. 2FA rendu optionnel pour tous les comptes — ✅ décidé (02/08/2026)

- **CDC §7.2** : « authentification forte + 2FA obligatoire pour comptes à
  privilèges (admin Sigasécurité, responsables sûreté client) ». L'application
  imposait donc un enrôlement 2FA forcé à la connexion pour les rôles
  Admin/SuperAdmin/Sûreté (`Auth:RequireTwoFactorForPrivileged`), avec
  ré-enrôlement automatique si désactivé entre-temps.
- **Décision** : Mamadou KONATE (prestataire, également porteur d'un compte
  SuperAdmin sur ce déploiement) a choisi de rendre le 2FA optionnel pour
  **tous** les rôles, y compris Admin/SuperAdmin/Sûreté — écart assumé au
  CDC §7.2, décidé en direct sans consultation écrite préalable de M. Kodjo.
  **À faire confirmer par écrit auprès de Sigasécurité avant le pilote**,
  puisque le CDC signé impose explicitement le contraire pour les comptes à
  privilèges.
- **Portée** : `Auth:RequireTwoFactorForPrivileged=false` (Api). Le 2FA reste
  activable/désactivable librement par n'importe quel compte, à tout moment,
  via son profil (`/api/auth/2fa/setup`, `/2fa/enable`, `/2fa/disable`) — ce
  self-service existait déjà pour tous les rôles, seul le caractère
  **obligatoire** pour les comptes à privilèges a été retiré.

## Synthèse

| # | Évolution | Priorité | Indicatif (j-h) |
|---|---|---|---|
| 1 | Prise de poste agent | ✅ Réalisée (26/07/2026) | 3–5 |
| 2 | Clé de signature par site | Avant phase revente | 3–4 |
| 3 | Console terminaux + QR d'enrôlement | ✅ Réalisée | 5–8 |
| 4 | Renommage SigasAcces + domaine | ✅ Décidé/réalisé (02/08/2026) | < 1 |
| 5 | 2FA optionnel pour tous les comptes | ⚠️ Décidé (02/08/2026), écart CDC §7.2 à confirmer par écrit | < 1 |

**Décision attendue** : confirmer si l'une ou l'autre entre dans le périmètre du
pilote (auquel cas avenant), ou est planifiée pour la phase de déploiement
multi-clients. Le pilote SICOPA peut démarrer **sans** ces trois évolutions.
