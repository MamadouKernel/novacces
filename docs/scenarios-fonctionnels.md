# Scénarios fonctionnels — Spécification comportementale (issue de la maquette démontrée le 22/07/2026)

> Ce document décrit le comportement EXACT attendu, validé par le client
> lors de la démonstration. C'est la référence fonctionnelle la plus fiable
> de ce projet — plus précise que le CDC pour les cas limites. Chaque
> scénario ci-dessous a un test correspondant (ou doit en avoir un) dans
> `tests/NovAcces.UnitTests/`.

## 1. Cycle de vie d'une visite (mode Unique)

1. L'hôte crée une demande → QR généré et signé, envoyé par **email
   uniquement** (WhatsApp abandonné — décision M. Kodjo du 01/08/2026, voir
   `docs/accord-commercial.md`).
   > Canal à sens unique : NovAcces envoie une notification, il n'y a aucun
   > traitement d'une éventuelle réponse du destinataire.
2. Fenêtre de validité = **rendez-vous −20 min à +15 min**, calculée côté
   serveur exclusivement (jamais l'heure du téléphone de l'agent).
3. **Poste ENTRÉE**, scan dans la fenêtre → écran **VERT**, "ACCÈS AUTORISÉ",
   visiteur marqué présent, hôte notifié de l'arrivée, statut passe à
   "consommé".
4. **Poste ENTRÉE**, scan hors fenêtre (trop tôt) → écran **ROUGE**,
   "TROP TÔT — fenêtre à HH:MM", **événement de sécurité**.
5. **Poste ENTRÉE**, scan hors fenêtre (trop tard) → écran **ROUGE**,
   "HORS FENÊTRE DE VALIDITÉ", **événement de sécurité**.
6. **Poste SORTIE**, scan du visiteur présent → écran **BLEU**,
   "SORTIE ENREGISTRÉE", durée de présence affichée, hôte notifié du
   départ, cycle marqué comme **clos** (pour le mode Unique).
7. **Poste SORTIE**, scan d'un visiteur qui n'est pas présent → refus
   simple "AUCUNE ENTRÉE ENREGISTRÉE" — **pas** un événement de sécurité
   (erreur opérationnelle banale, pas une fraude).
8. Après le cycle clos, tout nouveau scan (n'importe quel poste) → écran
   **ROUGE**, "CYCLE ENTRÉE/SORTIE CLOS" ou "QR DÉJÀ CONSOMMÉ", **événement
   de sécurité**.

## 2. Le point le plus subtil : poste directionnel et copie volée

**Le comportement dépend du SENS du poste (Entrée vs Sortie), pas d'une
simple bascule "re-scan = sortie".**

- **Poste ENTRÉE**, scan d'un QR dont le titulaire est déjà marqué présent
  (`IsOnSite = true`) → **PAS une sortie**. C'est une **anomalie
  d'usurpation** : écran ROUGE "DÉJÀ SUR SITE — suspicion de copie",
  **événement de sécurité**, alerte immédiate à la sûreté ET à l'hôte
  ("vérifiez si votre visiteur est bien arrivé").
  - Scénario type : un QR authentique est copié (capture d'écran) : le
    voleur entre en premier avec la copie (écran vert, signature valide —
    indiscernable de l'original) ; quand le vrai titulaire se présente à
    l'entrée, c'est CE scan qui révèle l'anomalie.
- **Poste SORTIE**, scan d'un visiteur présent → sortie normale (voir
  section 1, point 6), quel que soit qui a présenté le QR — la sortie n'a
  pas la même charge de preuve que l'entrée.

**Principe de sûreté absolu, jamais dérogé** : on ne bloque **jamais**
physiquement une sortie, même si le QR a été révoqué entre-temps. Un
visiteur présent dont le QR est révoqué pendant sa présence peut sortir
normalement (écran bleu) ; c'est la **ré-entrée** suivante qui sera
refusée.

## 3. Mode 30 jours

- Valide uniquement les **jours ouvrés** (lun-ven) ; un scan un
  jour non ouvré → refus "JOUR NON OUVRÉ", événement de sécurité.
- Contrairement au mode Unique, **chaque scan alterne** entrée/sortie
  normalement (pas de "cycle clos" après une seule sortie) — le visiteur
  peut entrer et sortir plusieurs fois sur la période.
- **Doit expirer après 30 jours calendaires** depuis la création (gap
  identifié lors de l'audit du 23/07/2026, corrigé dans le scaffold —
  vérifier que ce comportement est bien conservé si le code évolue).

## 4. Liste d'exclusion (moindre privilège)

- Une personne en liste d'exclusion qui se présente (via une demande ou un
  scan) → refus **générique** à l'agent : "ACCÈS REFUSÉ — voir poste de
  garde". **Le motif d'exclusion n'est JAMAIS communiqué à l'agent.**
- Le motif et l'alerte détaillée ne sont visibles que côté **dashboard
  sûreté**, avec la mention explicite de la présentation de cette personne.

## 5. QR falsifié

- Un QR dont la signature cryptographique est invalide (contenu altéré,
  forgé, ou simplement incohérent) → refus "SIGNATURE INVALIDE — QR
  ALTÉRÉ", **événement de sécurité**.
- **Cette vérification doit fonctionner même hors ligne** : c'est une
  opération purement mathématique (vérification de signature avec la clé
  publique), qui ne nécessite ni base de données ni réseau. C'est un
  argument de vente fort à préserver absolument dans l'implémentation.

## 6. Mode dégradé (coupure réseau)

1. À l'activation : l'app charge une **liste des QR valides du jour**,
   signée par le serveur, avec un **TTL affiché** (cible 4h).
2. Les scans en mode dégradé fonctionnent normalement (entrée/sortie/refus)
   MAIS :
   - Un QR **absent de la liste locale** (ex. créé après le début de la
     coupure) → refus "VÉRIFICATION IMPOSSIBLE — hors ligne", **pas**
     nécessairement un événement de sécurité (c'est une limite technique,
     pas une fraude avérée).
   - Chaque validation en mode dégradé est **marquée comme telle** dans le
     journal (`RecordedInDegradedMode`).
3. Si le **TTL expire** pendant la coupure → **plus aucune validation
   locale possible**, même pour un QR par ailleurs valide : "VALIDATION
   IMPOSSIBLE — liste locale expirée".
4. Si un QR est **révoqué pendant la coupure** (depuis le dashboard sûreté,
   qui lui reste en ligne) → le poste hors ligne ne le sait pas encore. La
   révocation **prend effet à la reconnexion suivante**.
5. **À la resynchronisation** (retour en ligne) :
   - Tous les scans effectués hors ligne sont confrontés au registre
     central.
   - Si un scan hors ligne concernait un QR révoqué pendant la coupure →
     **conflit détecté, remonté comme événement de sécurité** au
     responsable sûreté.
   - Si aucun conflit → confirmation simple ("N validations confrontées,
     aucun conflit détecté").

## 7. Supervision des dépassements de durée (fonctionnalité à valeur
ajoutée, hors CDC original — voir note-analyse.md)

- Chaque visite a une **durée prévue** (ex. 1h), définie à la création.
- Si le visiteur dépasse l'heure de sortie prévue **alors qu'il est
  toujours présent** :
  - **Alerte niveau 1** immédiate (hôte + sûreté) dès le dépassement détecté.
  - **Rappels périodiques** tant que le visiteur reste présent, avec un
    niveau qui s'incrémente (rappel n°2, n°3...).
  - **À partir du niveau 3**, le rappel devient un **événement de
    sécurité** avec la recommandation "vérification physique par un agent".
  - **Jamais bloquant** : ce n'est que de la supervision, aucun impact sur
    la capacité du visiteur à sortir normalement.
  - Les alertes et le niveau sont **remis à zéro dès la sortie** (par scan
    ou manuellement depuis le dashboard sûreté).
  - L'intervalle entre rappels est un paramètre (démo réglée à 2 minutes
    pour la présentation ; en production, viser plutôt 15 minutes,
    paramétrable par site).

## 8. Visiteurs récurrents et garde-fou anti-doublon (côté portail hôte,
hors CDC original)

- Si l'hôte tape le nom d'un visiteur déjà venu → **autocomplétion** :
  entreprise, motif habituel et durée pré-remplis automatiquement.
- **Impossible de créer un second QR actif pour la même personne** tant
  qu'une demande est déjà valide ou que le visiteur est présent — le
  système le signale et pointe vers la demande existante plutôt que de
  créer un doublon silencieux.
- Bouton "Ré-inviter" sur toute demande close (consommée/expirée/révoquée) :
  pré-remplit une nouvelle demande à partir de l'historique.

## 9. Journal et synthèse (dashboard sûreté)

- Journal **inaltérable** (INSERT-only en base), recherche par nom/
  entreprise/agent/motif.
- Chaque ligne distingue : autorisé / refusé / sortie / dépassement, avec
  pastille "SÉCURITÉ" pour les événements de sécurité et "dégradé" pour
  les validations hors ligne.
- **Synthèse quotidienne générée automatiquement** (hors CDC original) :
  nombre de scans, pic d'affluence, taux de refus avec appréciation
  ("dans la normale" / "inhabituel, à surveiller"), résumé des événements
  de sécurité, présences en dépassement, recommandation textuelle qui
  s'adapte à la situation (aucune action requise → vérifier une présence →
  renforcer la vigilance).
- Export CSV réel (téléchargement d'un fichier, pas une promesse).

## 10. Administration multi-sites (hors CDC original, argument commercial)

- Vue consolidée : sites déployés, visiteurs présents tous sites, état de
  chaque poste (en ligne / dégradé), disponibilité du service.
- Le message clé à préserver dans l'architecture : **ajouter un nouveau
  site client = provisionner un nouveau tenant (schéma PostgreSQL), sans
  toucher aux sites existants, sans redéveloppement**.

## 11. Application agent — ergonomie (contrainte de conception)

- Verdict affiché **plein écran**, non ambigu (vert/bleu/rouge), en moins
  de 2 secondes.
- Signal sonore + vibration au verdict (l'agent ne doit pas dépendre de la
  lecture visuelle seule).
- **Liste minimale des "attendus aujourd'hui"** consultable par l'agent :
  nom + statut (attendu/sur site/sorti/révoqué/non venu) + fenêtre horaire
  UNIQUEMENT — **jamais** le motif, l'entreprise détaillée ou les
  coordonnées (moindre privilège). Sert notamment à gérer un visiteur sans
  QR (téléphone déchargé) : l'agent cherche le nom et demande une
  validation manuelle à la sûreté.
- Bouton de bascule **poste directionnel** (Entrée ⇄ Sortie) toujours
  visible.
