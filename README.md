# Shelf

Application de stockage de photos, vidéos et fichiers.

## Organisation du monorepo

- `apps/backend/` : API ASP.NET Core (.NET 10) et solution .NET.
- `apps/web/` : future application web, technologie à choisir.
- `apps/mobile/` : future application mobile, technologie à choisir.
- `contracts/` : futurs contrats OpenAPI et génération des clients.
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
dotnet run --project apps/backend/Shelf.Api --launch-profile http
```

L'API est accessible sur http://localhost:5050.
Vérification : `curl --fail http://localhost:5050/health`.

Le backend fournit uniquement un endpoint de santé pour démarrer. Le stockage,
l'authentification et les applications clientes restent à implémenter.
Ne pas ajouter de fichiers personnels ni de secrets au dépôt.
