# Runbook — démonstration d'avancement au client (Jalon 2)

Scénario de démo prêt à jouer devant Sigasécurité, calé sur les comportements de
la maquette validée le 22/07/2026. Durée ~15 min. Tout tourne en local.

## 0. Préparation (avant le client)

```bash
# 1. PostgreSQL local en marche (base 'novacces').
# 2. Clés ES256 + connection string dans user-secrets de l'API (voir README).
# 3. Provisionner le site pilote si besoin :
cd src/NovAcces.Api && dotnet run -- provision-site sicopa

# 4. Lancer l'API (laisser tourner) :
dotnet run
# 5. Dans un autre terminal, lancer le portail web :
cd src/NovAcces.Web && dotnet run --launch-profile http   # http://localhost:5282
```

Comptes de démo (créés par l'Admin amorcé `admin@novacces.local`) :
- Hôte : `hote@sicopa.local`
- Sûreté : `surete@sicopa.local`

## 1. Le message d'ouverture (30 s)

> « NovAcces ne délivre pas un service numérique : il ouvre une porte physique
> sur un site sensible. Chaque écran vert engage la responsabilité de l'agent.
> Tout ce que vous allez voir applique cette rigueur — la sécurité prime sur la
> rapidité. »

## 2. Parcours Hôte — créer une visite (2 min)

1. Ouvrir `http://localhost:5282`, se connecter en **Hôte**.
2. Créer une demande (nom, société, motif, rendez-vous). Montrer
   l'**autocomplétion** d'un visiteur déjà venu (entreprise/motif pré-remplis).
3. Cliquer « Générer le QR » → **le QR s'affiche**.
   > « En production, ce QR part automatiquement au visiteur par WhatsApp. »
4. Montrer « Mes demandes » + le bouton **Révoquer** (et « Ré-inviter » sur une
   demande close).

## 3. Le cœur : le scan directionnel et la copie volée (4 min)

> C'est le point le plus subtil, validé à la maquette.

À jouer via l'API (Swagger `https://localhost:54980/swagger` avec en-tête
`X-Api-Key`, ou un client REST) OU en montrant les tests :

- **Entrée dans la fenêtre** → `GRANTED` (vert), visiteur marqué présent.
- **Re-scan à l'ENTRÉE alors que déjà présent** → `DENIED_SuspectedDuplicate`
  (rouge), **événement de sécurité** : « c'est la signature d'une copie volée —
  le vrai titulaire arrive après le voleur, et c'est CE scan qui le révèle. »
- **Sortie** → `CHECKED_OUT` (bleu). « On ne bloque jamais une sortie, même si le
  QR a été révoqué entre-temps. »
- **Réutilisation après cycle** → refusé (rouge).

Alternative sans client REST : `dotnet test --filter VisitScanTests` et montrer
les noms de tests — ils SONT les scénarios de la maquette.

## 4. Dashboard sûreté — le temps réel (3 min)

1. Se connecter en **Sûreté** → dashboard.
2. Déclencher un scan (API) pendant que le dashboard est ouvert → **il apparaît
   instantanément** dans le flux (SignalR).
3. Montrer : présents sur site, **synthèse du jour** (appréciation du taux de
   refus + recommandation), **recherche** dans le journal, **export CSV**.
4. Montrer la **liste d'exclusion** : ajouter un nom (motif interne) — expliquer
   que l'agent, lui, ne verra qu'un refus générique « voir poste de garde ».
5. Si un visiteur dépasse sa durée : montrer l'**alerte de dépassement** (badge +
   bannière ; niveau 3 = événement de sécurité).

## 5. Administration multi-sites (1 min)

Se connecter en **Admin** → **vue consolidée** (présents et scans par site) +
provisionnement d'un nouveau site en un clic.
> « Ajouter un client = provisionner un nouveau tenant, sans toucher aux sites
> existants, sans redéveloppement. »

## 6. Sécurité & hors-ligne (2 min)

- Rappeler : signature **ES256**, QR sans donnée personnelle, journal
  **inaltérable**, cloisonnement strict par site.
- **Mode dégradé** : « la vérification du QR est purement mathématique — elle
  fonctionne **sans réseau**. » Montrer les tests `OfflineQrVerifierTests` /
  `OfflineScanEvaluatorTests` (compatibilité serveur ↔ agent prouvée).
- Remettre le **dossier de recette de sécurité** (`docs/rapport-recette-securite.md`).

## 7. Clôture

> « Le périmètre API + Web est complet et couvert par 74 tests automatisés.
> La prochaine étape est l'application agent sur terminal, dont le cœur
> cryptographique hors-ligne est déjà prêt et testé. »

## Annexe — vérifier que tout est vert avant la démo
```bash
dotnet build     # 0 warning
dotnet test      # 74 tests au vert (PostgreSQL local requis)
```
