# Infrastructure

Ce guide couvre le développement local. Le déploiement du stockage sur un VPS
(fournisseur compatible S3, bucket privé, CORS, sauvegarde quotidienne de
PostgreSQL et restauration) est décrit dans
[l'exploitation du stockage](storage.md).

## PostgreSQL en développement

Depuis la racine du dépôt, avec Docker et Docker Compose installés :

```sh
cp .env.example .env
docker compose up -d --wait postgres
docker compose ps
```

Le fichier `.env` est ignoré par Git. L'authentification PostgreSQL utilise
`trust` : aucun mot de passe n'est demandé. Cette configuration est réservée au
développement local ; le port publié reste lié à `127.0.0.1`.

L'image officielle `postgres:18-bookworm` fixe la version majeure et la variante
Debian, tout en permettant de récupérer les correctifs de PostgreSQL 18.
PostgreSQL ne propose pas d'édition LTS distincte : chaque version majeure reçoit
cinq ans de support. PostgreSQL 18 est maintenu jusqu'au 14 novembre 2030.
Voir la [politique officielle](https://www.postgresql.org/support/versioning/).

### Connexion

La base est accessible uniquement depuis la machine locale :

| Paramètre | Valeur par défaut |
| --- | --- |
| Hôte | `127.0.0.1` |
| Port | `5432` (modifiable avec `POSTGRES_PORT`) |
| Base | `shelf` |
| Utilisateur | `shelf` |
| Mot de passe | aucun |

Pour ouvrir une console SQL dans le conteneur :

```sh
docker compose exec postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
```

La future connexion .NET utilisera la forme
`Host=127.0.0.1;Port=5432;Database=shelf;Username=shelf`.
Le backend ne consomme pas encore cette connexion : il ne possède ni pilote
PostgreSQL, ni modèle de persistance, ni migrations. Le fichier `.env` configure
Docker Compose ; il n'est pas chargé automatiquement par ASP.NET Core.

### Données et maintenance

Le volume nommé `postgres_data` est monté sur `/var/lib/postgresql`, conformément
à l'[image officielle pour PostgreSQL 18](https://hub.docker.com/_/postgres).
Il conserve les données après un redémarrage ou une recréation du conteneur.
Les variables de base, d'utilisateur et d'authentification initialisent uniquement
un volume vide ; modifier `.env` ou `POSTGRES_HOST_AUTH_METHOD` ne modifie pas une
base existante. Pour passer une base existante à `trust`, adapter ses règles dans
`pg_hba.conf` et recharger la configuration PostgreSQL.
L'utilisateur créé est administrateur PostgreSQL, pour le développement local.

Arrêter les services en conservant les données :

```sh
docker compose down
```

Récupérer les correctifs de la version 18 puis recréer le conteneur :

```sh
docker compose pull postgres
docker compose up -d --wait postgres
```

Sauvegarder les données avant une mise à jour. Un changement de version majeure
nécessite une migration avec `pg_upgrade` ou un export/import. La commande
`docker compose down --volumes` supprime définitivement les données locales.

## MinIO en développement

`compose.yaml` fournit MinIO, service compatible S3, pour le développement et
les tests d'intégration du [stockage](../docs/storage.md) :

```sh
cp .env.example .env   # puis choisir MINIO_ROOT_USER et MINIO_ROOT_PASSWORD
docker compose up -d --wait minio
```

L'API S3 écoute sur `127.0.0.1:9000` (`MINIO_PORT`) et la console web sur
`127.0.0.1:9001` (`MINIO_CONSOLE_PORT`). Les identifiants racine viennent de
`.env`, ignoré par Git ; les valeurs de `.env.example` sont réservées au poste
local. Les objets sont conservés dans le volume `minio_data`.

### Origine du navigateur et envois directs

Compose fixe explicitement le CORS global de MinIO avec
`MINIO_API_CORS_ALLOW_ORIGIN`, lu depuis `.env`, par défaut
`https://localhost:3000` comme le [serveur web local](../apps/web/README.md).
Pas de joker `*` : utiliser l'origine exacte affichée dans le navigateur
(schéma, hôte, port, sans chemin ni `/` final), identique à
`Authentication__ClientOrigin` côté API. Pour un autre hôte, port ou portail,
modifier cette valeur dans `.env`, puis recréer **uniquement le MinIO local**
depuis la racine du dépôt :

```sh
docker compose up -d --no-deps --force-recreate minio
```

Un simple `docker compose restart minio` ne recharge pas les variables
d'environnement. La recréation conserve le volume `minio_data` ; ne pas lancer
cette commande sur un déploiement partagé.

Les octets sont envoyés directement au S3 par URL signées, pas via `/api` :
`Storage__S3__Endpoint` doit être joignable par l'API **et par le navigateur**.
En production, utiliser un endpoint HTTPS avec un certificat approuvé, jamais
une adresse interne Docker telle que `http://minio:9000`. Le HTTP sur la boucle
locale (`http://localhost:9000` ou `http://127.0.0.1:9000`) est une exception
réservée au développement local, reconnue par les navigateurs modernes ; elle
ne vaut pas pour un hôte distant ou une adresse réseau. Depuis un navigateur
distant, `localhost` désigne sa propre machine : prévoir un endpoint S3 HTTPS
accessible depuis celui-ci, sans désactiver les protections du navigateur.

MinIO n'est plus publié par son éditeur (dépôt archivé, images officielles
retirées). Compose utilise `chainguard/minio`, reconstruit depuis les sources de
MinIO. Ce service ne sert qu'au développement ; en production, utiliser un
fournisseur compatible S3 maintenu.

Créer le bucket de développement, puis configurer l'API :

```sh
curl --aws-sigv4 aws:amz:us-east-1:s3 --user "$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD" \
  -X PUT http://localhost:9000/shelf
export Storage__S3__Endpoint=http://localhost:9000
export Storage__S3__Region=us-east-1
export Storage__S3__Bucket=shelf
export Storage__S3__AccessKey="$MINIO_ROOT_USER"
export Storage__S3__SecretKey="$MINIO_ROOT_PASSWORD"
export Storage__S3__ForcePathStyle=true
```

Les tests d'intégration créent et suppriment leur propre bucket par cas de test
(voir [le backend](../apps/backend/README.md)).
