# NovAcces — Contexte projet pour Claude Code

Lis ce fichier en entier avant toute intervention. Il contient le contexte
métier, contractuel et technique nécessaire pour continuer ce projet sans
perdre les décisions déjà prises. Les documents sources complets sont dans
`docs/`.

## 1. Qui, quoi, pourquoi

- **Prestataire** : Mamadou KONATE, développeur .NET (5 ans d'expérience),
  indépendant, basé à Abidjan (Côte d'Ivoire). Travaille aussi en IT chez
  Côte d'Ivoire Terminal (port d'Abidjan).
- **Client** : Sigasécurité, entreprise de sécurité privée agréée
  (Agrément n° 344 MI), Abidjan + San Pedro. Contact signataire :
  M. Kodjo, Direction des Opérations.
- **Projet** : NovAcces — application de gestion des visiteurs par QR Code
  sécurisé, pour le contrôle d'accès sur les sites des clients de
  Sigasécurité (industriels, portuaires, agro-industriels).
- **Enjeu central, à ne jamais perdre de vue** : NovAcces ne délivre pas un
  service numérique, il conditionne un **accès physique**. Chaque écran vert
  ouvre une porte sur un site sensible sous la responsabilité d'un opérateur
  de sûreté agréé. La rigueur de sécurité prime sur la rapidité de
  livraison — en cas de doute entre "aller vite" et "être sûr", toujours
  choisir "être sûr" et le signaler à Mamadou.
- **Positionnement produit** : NovAcces est conçu pour être revendu par
  Sigasécurité à ses propres clients, site par site (architecture
  multi-tenant). Ce n'est pas qu'une app pour Sigasécurité elle-même.

## 2. État contractuel actuel (ce qui a été VENDU et SIGNÉ)

Voir `docs/accord-commercial.md` pour le détail complet. Résumé :

- **Développement forfaitaire : 2 000 000 FCFA**, payé en 3 jalons
  (600 000 signature / 800 000 recette / 600 000 mise en production pilote).
- **Récurrent annuel unique : 500 000 FCFA/an**, payé d'avance, couvrant
  hébergement infogéré + maintenance de base. Reconductible librement,
  aucun engagement pluriannuel.
- **Garantie corrective de 3 mois** après mise en production.
- **Pas d'audit d'intrusion externe** dans cette phase (décision du client,
  constatée par écrit) — remplacé par une recette de sécurité interne
  documentée + tests automatisés + analyse OWASP, avec rapport remis avant
  mise en production. L'audit externe reste recommandé avant tout
  déploiement chez un client tiers de Sigasécurité.
- **Perspective non engageante** : Sigasécurité envisage de confier ensuite
  à Mamadou la reprise de son logiciel de gestion de la télésurveillance —
  projet distinct, pas encore chiffré.
- **Hébergement** : VPS Contabo (Union européenne), ~8-15€/mois de coût
  réel. Nom de domaine à prévoir.
- **Notifications** : WhatsApp Business Platform (API officielle Meta Cloud
  API), PAS de SMS — décision prise en cours de négociation. QR envoyé en
  image dans la conversation. Email en repli automatique.

**Important pour toi (Claude Code) : le périmètre contractuel exact est
celui du CDC original (`docs/cahier-des-charges-original.md`) + la note
d'analyse (`docs/note-analyse.md`) annexée au contrat. Toute fonctionnalité
qui en sort doit être signalée à Mamadou avant d'être développée — elle
pourrait nécessiter un avenant chiffré (90 000 FCFA/jour-homme).**

## 3. Découpage en jalons (calendrier de la proposition)

| Jalon | Montant | Contenu attendu |
|---|---|---|
| **1 — Signature** (fait, scaffold initial) | 600 000 FCFA | Solution scaffoldée, multi-tenant, signature ES256, anti-rejeu, tests unitaires |
| **2 — Recette** | 800 000 FCFA | API complète, Web (portail hôte + dashboard sûreté + admin), App agent MAUI, mode dégradé, Identity+2FA, WhatsApp, SignalR |
| **3 — Production** | 600 000 FCFA | Déploiement VPS, recette de sécurité documentée, rapport de tests, mise en production site pilote (SICOPA) |

**Démonstration d'avancement promise au client toutes les deux semaines** —
Mamadou doit pouvoir montrer quelque chose de fonctionnel à ce rythme, en
garde ça en tête pour prioriser un vertical slice fonctionnel plutôt que
des couches complètes une par une.

## 4. Ce qui fait foi fonctionnellement : la maquette de démonstration

Une maquette HTML interactive a été développée et **démontrée au client le
22/07/2026**, validée par 50 tests de comportement. C'est la spécification
fonctionnelle de référence — voir `docs/scenarios-fonctionnels.md` pour le
détail exhaustif de chaque règle. En cas de doute sur un comportement
attendu, ce document prime sur toute reformulation ultérieure.

Comportements clés à ne jamais régresser :
- Fenêtre de validité -20/+15 min calculée **côté serveur exclusivement**
- Cycle entrée/sortie **directionnel** (poste Entrée vs poste Sortie) — ce
  n'est PAS un simple re-scan qui bascule, chaque poste a un sens
- Anti-rejeu porte sur le **cycle complet**, pas sur le scan brut : un
  titulaire qui se présente à l'entrée alors qu'il est déjà sur site =
  suspicion de copie volée (pas une sortie)
- La sortie n'est **jamais bloquée**, même si le QR a été révoqué entre
  temps — on ne bloque jamais physiquement quelqu'un
- Mode dégradé : liste locale signée, TTL ~4h, resynchronisation avec
  détection de conflits à la reconnexion
- Dépassement de durée de visite : alerte progressive avec escalade
  (niveau 1, rappels, niveau 3 = événement de sécurité), jamais bloquant
- Liste d'exclusion : refus générique à l'agent ("voir poste de garde"),
  motif réservé à la sûreté (moindre privilège)

## 5. Architecture posée (Jalon 1 — déjà scaffoldée)

Clean Architecture : `Domain` (aucune dépendance, logique pure) →
`Application` (cas d'usage) → `Infrastructure` (EF Core, PostgreSQL
multi-tenant par schéma, signature ES256) → `Api` (minimal API).
`Web` (Blazor) et `Mobile` (MAUI) sont en squelette, à construire au Jalon 2
(voir leurs README.md respectifs pour les commandes de démarrage).

**Décision technique actée, ne pas remettre en question sans discussion** :
signature **ECDSA P-256 (ES256)** via `System.Security.Cryptography` natif —
PAS Ed25519 (nécessiterait une dépendance tierce `NSec.Cryptography`,
volontairement écartée pour un système de sûreté : zéro dépendance
cryptographique externe à auditer).

## 6. Audit de conformité déjà réalisé — voir `docs/audit-conformite.md`

Un audit ligne par ligne contre le CDC a été fait le 23/07/2026. Bugs
trouvés et corrigés : expiration du QR jamais vérifiée, endpoint de
révocation manquant, mode 30 jours qui n'expirait jamais. Gaps identifiés
et volontairement laissés pour le Jalon 2 : interface de notification
(WhatsApp/email), hook de notification temps réel (SignalR).

**Première tâche à faire, avant tout nouveau développement :**
```bash
dotnet restore
dotnet build     # corriger les erreurs de compilation (versions de packages notamment)
dotnet test      # les 25 tests doivent TOUS passer avant de continuer
```
Si `dotnet test` échoue, ne continue pas le développement sans comprendre
pourquoi et sans en informer Mamadou si la cause touche à la logique de
sécurité (Domain/Visit.cs, Infrastructure/Security/).

## 7. Zones sensibles — relecture humaine obligatoire avant commit

1. `Domain/Entities/Visit.cs` — la logique de sûreté complète. Toute
   modification doit être accompagnée d'un test qui la couvre.
2. `Infrastructure/Security/Es256QrSigningService.cs` — cryptographie.
   Ne jamais logger la clé privée, ne jamais l'assouplir "pour débugger".
3. `Infrastructure/Persistence/NovAccesDbContext.
   EnsureTenantSchemaAppliedAsync` — le cloisonnement multi-tenant. Une
   erreur ici est une fuite de données entre clients de Sigasécurité.
4. `ScanLogEntryConfiguration` — le journal doit être INSERT-only en base
   (contrainte à appliquer via script SQL de provisionnement, hors EF Core).

## 8. Prochaines étapes concrètes (dans l'ordre)

1. `dotnet build` + `dotnet test`, corriger jusqu'à vert.
2. Ajouter `INotificationService` (Application) + implémentation WhatsApp
   Cloud API (Infrastructure) — gap identifié dans l'audit.
3. Ajouter le hook de notification temps réel (SignalR) sur `ScanQrHandler`.
4. ASP.NET Core Identity + 2FA TOTP + policies RBAC (Hôte / Agent / Sûreté
   / Admin) dans `NovAcces.Api`.
5. Construire `NovAcces.Web` (Blazor) — voir `src/NovAcces.Web/README.md`.
6. Construire `NovAcces.Mobile` (MAUI) — voir `src/NovAcces.Mobile/README.md`.
7. À chaque étape, comparer au tableau de `docs/scenarios-fonctionnels.md`
   pour ne rien perdre de ce qui a été démontré au client.

## 9. Documents de référence complets

- `docs/cahier-des-charges-original.md` — CDC v1.0 du client (texte intégral)
- `docs/note-analyse.md` — analyse et engagements du prestataire (v1.1)
- `docs/accord-commercial.md` — termes financiers et contractuels finaux
- `docs/scenarios-fonctionnels.md` — spécification comportementale exhaustive
  (issue de la maquette démontrée le 22/07/2026)
- `docs/audit-conformite.md` — état de conformité détaillé, bugs corrigés
- `README.md` (racine) — état technique du scaffold, commandes de build
