# Shelf

Application de stockage de photos, vidéos et fichiers.

## Organisation du monorepo

- `apps/backend/` : API ASP.NET Core (.NET 10) et solution .NET.
- `apps/web/` : future application web, technologie à choisir.
- `apps/mobile/` : future application mobile, technologie à choisir.
- `contracts/` : contrat OpenAPI généré et collection Bruno.
- `compose.yaml` : PostgreSQL pour le développement local.
- `infra/` : documentation des services et du déploiement.
- `docs/` : documentation du projet et décisions techniques.

Chaque application conserve ses dépendances et son déploiement propres.

## Démarrer le backend

Prérequis : SDK .NET 10 et Docker avec Docker Compose.

Préparer la base PostgreSQL :

```sh
cp .env.example .env
docker compose up -d --wait postgres
```

Les données sont conservées dans un volume Docker. Voir [la documentation
d'infrastructure](infra/README.md) pour la connexion, l'arrêt et les mises à jour.

Lancer ensuite l'API :

```sh
dotnet restore apps/backend/Shelf.slnx
dotnet build apps/backend/Shelf.slnx --no-restore
export ConnectionStrings__Shelf='Host=localhost;Database=shelf;Username=shelf'
export Authentication__ClientOrigin='http://localhost:3000'
dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- migrate
dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- admin create owner owner@example.test
dotnet run --project apps/backend/Shelf.Api --launch-profile http
```

L'API est accessible sur http://localhost:5050.
Vérification : `curl --fail http://localhost:5050/health`.

Le backend fournit la santé, l’authentification web/mobile et la gestion des
comptes sur invitation. Voir [le contrat et les commandes](docs/authentication.md).
Le stockage et les applications clientes restent à implémenter.
Ne pas ajouter de fichiers personnels ni de secrets au dépôt.

## Intégration continue

GitHub Actions vérifie les PR vers toutes les branches, les poussées sur `main`,
les groupes de fusion et les lancements manuels. Le statut à rendre obligatoire
pour protéger `main` est **Backend and contracts**.

Un seul job Ubuntu restaure les dépendances NuGet verrouillées (avec cache),
compile la solution en Release, vérifie OpenAPI et Bruno puis lance les tests
sur le PostgreSQL réel de Compose. Si Compose déclare MinIO, il est démarré et
sa disponibilité S3 est vérifiée avant les tests. Aucun secret GitHub n'est requis.
Les nouvelles poussées annulent les exécutions précédentes du même contexte.
Les résultats de tests et diagnostics de blocage sont conservés sept jours en
cas d'échec.

Les changements limités à la documentation ou aux emplacements web/mobile
évitent l'installation des outils, les services et les tests ; le statut reste
présent pour les protections de branche. Les lancements manuels et les historiques
non comparables exécutent toutes les vérifications. Quand une application cliente
sera implémentée, ajouter ses vérifications et adapter le filtre
`.github/scripts/ci-scope.sh`.

Pour reproduire les vérifications localement, avec les services Compose démarrés :

```sh
dotnet restore apps/backend/Shelf.slnx --locked-mode
dotnet build apps/backend/Shelf.slnx --configuration Release --no-restore
./contracts/generate.sh --no-build
git diff --exit-code -- contracts/
dotnet test apps/backend/Shelf.slnx --configuration Release --no-build --no-restore
```

Après une modification de dépendance, lancer `dotnet restore apps/backend/Shelf.slnx`
et committer les `packages.lock.json` actualisés. Les mises à jour des actions
GitHub épinglées par SHA sont proposées chaque semaine par Dependabot.
