# Backend Shelf

API ASP.NET Core .NET 10 ; PostgreSQL 18 via Npgsql. L’authentification commune
web/mobile est décrite dans [le contrat et le guide d’exploitation](../../docs/authentication.md).
Les interfaces clientes et le stockage de fichiers ne sont pas encore construits.

Depuis la racine du dépôt :

```sh
cp .env.example .env
docker compose up -d --wait postgres
export ConnectionStrings__Shelf='Host=localhost;Database=shelf;Username=shelf'
export Authentication__ClientOrigin='http://localhost:3000'
dotnet restore apps/backend/Shelf.slnx
dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- migrate
dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- admin create owner owner@example.test
dotnet run --project apps/backend/Shelf.Api --launch-profile http
```

La commande `admin create` demande le mot de passe sans l’afficher.
`GET http://localhost:5050/health` conserve son accès public.
La connexion sans mot de passe ci-dessus est réservée au PostgreSQL Docker local,
lié à l’interface loopback. En production, fournir une connexion PostgreSQL
avec authentification et TLS depuis la configuration secrète du déploiement.

## Vérification

```sh
dotnet build apps/backend/Shelf.slnx --no-restore
dotnet test apps/backend/Shelf.slnx --no-restore
```

Les tests d’intégration utilisent une vraie instance PostgreSQL. Par défaut :
`Host=localhost;Username=shelf;Database=postgres`. Pour une autre instance,
fournir `SHELF_TEST_POSTGRES` via l’environnement. Le rôle de test doit pouvoir
créer et supprimer des bases. Chaque cas crée une base `shelf_test_<uuid>`, lance
les migrations et le bootstrap par processus serveur, puis supprime uniquement
cette base. Ne pas utiliser un rôle de production. Les tests font avancer une
horloge injectée dans l’hôte HTTP ; aucune attente de jours n’est nécessaire.
