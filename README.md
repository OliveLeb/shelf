# Shelf

Application de stockage de photos, vidéos et fichiers.

## Organisation du monorepo

- `apps/backend/` : API ASP.NET Core (.NET 10) et solution .NET.
- `apps/web/` : future application web, technologie à choisir.
- `apps/mobile/` : future application mobile, technologie à choisir.
- `contracts/` : futurs contrats OpenAPI et génération des clients.
- `infra/` : future configuration des services et du déploiement.
- `docs/` : documentation du projet et décisions techniques.

Chaque application conserve ses dépendances et son déploiement propres.

## Démarrer le backend

Prérequis : SDK .NET 10.

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
