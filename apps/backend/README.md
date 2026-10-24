# Backend Shelf

API ASP.NET Core .NET 10 ; PostgreSQL 18 via Npgsql. L’authentification commune
web/mobile est décrite dans [le contrat et le guide d’exploitation](../../docs/authentication.md).
Le stockage des fichiers (S3) est décrit dans [son contrat](../../docs/storage.md).
Les interfaces clientes ne sont pas encore construites.

Depuis la racine du dépôt :

```sh
cp .env.example .env
docker compose up -d --wait postgres minio
export ConnectionStrings__Shelf='Host=localhost;Database=shelf;Username=shelf'
export Authentication__ClientOrigin='https://localhost:3000'
# Configuration S3 : voir infra/README.md (MinIO en développement).
dotnet restore apps/backend/Shelf.slnx
dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- migrate
dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- admin create owner owner@example.test
dotnet run --project apps/backend/Shelf.Api --launch-profile http
```

La commande `admin create` demande le mot de passe sans l’afficher.
`GET http://localhost:5050/health` conserve son accès public.
Le client web de développement utilise HTTPS sur le port 3000 et relaie `/api`
vers cette API HTTP sur le port 5050. Le navigateur doit voir HTTPS pour le cookie
`__Host-shelf-session` ; ne pas supprimer son attribut `Secure` pour développer.
L’origine configurée est celle du navigateur, sans `/api` ni slash final, et non
celle du backend. Les requêtes web mutantes, connexion comprise, envoient
`Origin` et `X-Shelf-CSRF: 1`. Voir les [règles de transport](../../docs/authentication.md#transport-des-sessions-web-et-mobile).
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
cette base. Ne pas utiliser un rôle de production.

Ils utilisent aussi un vrai service S3, MinIO par défaut :
`http://localhost:9000` avec les identifiants de `.env.example`. Pour un autre
service ou d’autres identifiants, fournir `SHELF_TEST_S3_ENDPOINT`,
`SHELF_TEST_S3_ACCESS_KEY` et `SHELF_TEST_S3_SECRET_KEY`. Chaque cas crée un
bucket `shelf-test-<uuid>`, y transfère réellement les octets par URL signées,
puis le vide et le supprime.

Les tests font avancer une horloge injectée dans l’hôte HTTP ; aucune attente de
jours n’est nécessaire.
