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
