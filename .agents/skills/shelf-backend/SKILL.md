---
name: shelf-backend
description: "Workflow backend .NET de Shelf : build, run et vérification de l'API ASP.NET Core (solution Shelf.slnx, .NET 10, PostgreSQL via Docker Compose). Utiliser dès qu'on travaille dans apps/backend, sur l'API Shelf.Api, ou qu'on parle de build/test/run du backend dotnet."
---

# Backend Shelf (.NET 10)

## Faits clés

- SDK .NET 10, version épinglée dans `global.json` (`rollForward: latestFeature`).
- Solution : `apps/backend/Shelf.slnx` (format `.slnx`, pas `.sln`).
- Projet API : `apps/backend/Shelf.Api` (`Microsoft.NET.Sdk.Web`, `Nullable` et `ImplicitUsings` activés).
- Base de données : PostgreSQL via `compose.yaml` (données dans un volume Docker).
- L'API écoute sur http://localhost:5050 (profil de lancement `http`).

## Commandes

Préparer la base avant de lancer l'API :

```sh
cp .env.example .env
docker compose up -d --wait postgres
```

Restaurer, builder, lancer :

```sh
dotnet restore apps/backend/Shelf.slnx
dotnet build apps/backend/Shelf.slnx --no-restore
dotnet run --project apps/backend/Shelf.Api --launch-profile http
```

Vérification que l'API tourne :

```sh
curl --fail http://localhost:5050/health
```

Dans un orb Amp, lancer le serveur via `amp orb services ensure` (ou `amp orb service start`) plutôt qu'en tâche de fond, et ne jamais donner d'URL localhost brute à l'utilisateur — partager l'URL de portail.

## Tests

Aucun projet de test n'existe encore. Quand on en crée un :

- Un seul projet de test par app, à côté du projet testé : `apps/backend/Shelf.Api.Tests`.
- Référencer le projet de test dans `Shelf.slnx`.
- Suivre le skill global `tdd` : tester aux coutures (seams) convenues, via l'interface publique de l'API, pas les détails d'implémentation.
- Framework : MSTest (voir le skill global `writing-mstest-tests`), exécution via `dotnet test apps/backend/Shelf.slnx`.

## Documentation de domaine

Avant d'explorer ou modifier le code, lire `CONTEXT.md` à la racine et les ADR dans `docs/adr/` s'ils existent ; employer le vocabulaire du glossaire dans les spécifications, issues et noms de tests (voir `docs/agents/domain.md`).
