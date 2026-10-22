# Exploitation du stockage

Guide de l'opérateur qui déploie Shelf sur un VPS avec un fournisseur compatible
S3. Le contrat HTTP et les variables de l'API sont décrits dans
[le stockage](../docs/storage.md) ; MinIO en développement dans
[l'infrastructure](README.md#minio-en-développement).

Le déploiement utilise deux buckets privés distincts :

| Bucket | Contenu | Accès |
| --- | --- | --- |
| Fichiers (ex. `shelf-files`) | Contenu des fichiers, sous des clés opaques `files/<uuid>` | API Shelf ; clients par URL signées |
| Sauvegardes (ex. `shelf-backups`) | Sauvegardes quotidiennes de PostgreSQL | Tâche de sauvegarde (écriture seule) ; opérateur pour restaurer |

## Configuration S3

L'API n'utilise que l'API S3 standard (signature V4) : tout fournisseur
compatible convient, sans fonction propre à AWS. Fournir les variables
`Storage__S3__*` décrites dans [le stockage](../docs/storage.md#configuration)
par l'environnement du service ou un gestionnaire de secrets, jamais dans un
fichier versionné.

- **Endpoint** : URL HTTPS du service S3 de la région du bucket, sans le nom du
  bucket. Les URL signées la reprennent : elle doit être joignable par les
  navigateurs et applications mobiles, pas seulement par le VPS. En production,
  pas de HTTP : un client web servi en HTTPS refuserait le contenu mixte.
- **Région** : celle annoncée par le fournisseur ; elle entre dans la signature.
- **Style d'adressage** (`Storage__S3__ForcePathStyle`) :
  - `false` (défaut), par sous-domaine : `https://<bucket>.<endpoint>/<clé>`.
    Style attendu par la plupart des fournisseurs ; le nom du bucket ne doit pas
    contenir de point, sans quoi le certificat TLS générique ne le couvre pas.
  - `true`, par chemin : `https://<endpoint>/<bucket>/<clé>`. Pour MinIO et les
    services sans DNS par bucket.
- **Identifiants** : une clé dédiée à l'API, limitée au bucket des fichiers
  (voir [la politique de bucket privé](#bucket-privé)). Ne jamais réutiliser la
  clé racine du compte.
- **Horloge** : synchroniser l'horloge du VPS (NTP, par exemple
  `systemd-timesyncd`). Le S3 refuse les signatures décalées de plus de
  quelques minutes et vérifie l'expiration des URL signées sur son horloge.

Exemples, à confirmer dans la documentation du fournisseur :

| Fournisseur | `Endpoint` | `Region` | `ForcePathStyle` |
| --- | --- | --- | --- |
| AWS S3 | `https://s3.eu-west-3.amazonaws.com` | `eu-west-3` | `false` |
| Scaleway | `https://s3.fr-par.scw.cloud` | `fr-par` | `false` |
| MinIO local | `http://localhost:9000` | `us-east-1` | `true` |

Les commandes de ce guide utilisent `curl` (7.76 ou plus récent) et sa signature
S3 intégrée, sans autre outil. Elles supposent l'adressage par chemin ; pour le
style par sous-domaine, remplacer `$S3_ENDPOINT/$BUCKET` par
`https://$BUCKET.<hôte de l'endpoint>`. Définir dans un shell d'administration,
avec une clé d'administration du compte S3 :

```sh
export S3_ENDPOINT=https://s3.fr-par.scw.cloud S3_REGION=fr-par
read -r S3_ACCESS_KEY; read -rs S3_SECRET_KEY
# Identifiants passés par un fichier de configuration curl, pas en argument (visible dans ps).
s3() {
  curl --silent --show-error --fail-with-body --aws-sigv4 "aws:amz:$S3_REGION:s3" \
    --config <(printf 'user = "%s:%s"\n' "$S3_ACCESS_KEY" "$S3_SECRET_KEY") "$@"
}
EMPTY_SHA256=e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
```

Certaines versions de `curl` n'envoient pas l'en-tête `x-amz-content-sha256`,
exigé par AWS : les commandes le fournissent explicitement. Créer les deux
buckets s'ils n'existent pas (ou depuis la console du fournisseur) :

```sh
s3 -X PUT -H "x-amz-content-sha256: $EMPTY_SHA256" "$S3_ENDPOINT/shelf-files"
s3 -X PUT -H "x-amz-content-sha256: $EMPTY_SHA256" "$S3_ENDPOINT/shelf-backups"
```

Hors de `us-east-1`, AWS exige en plus un corps
`<CreateBucketConfiguration><LocationConstraint>…</LocationConstraint></CreateBucketConfiguration>`.

## Bucket privé

Aucun objet n'est public : l'API vérifie les droits de l'utilisateur, puis
délivre une URL signée de courte durée (`Storage__SignedUrlMinutes`, 15 minutes
par défaut). C'est le seul accès au contenu pour les clients
([ADR 0001](../docs/adr/0001-direct-s3-transfers-with-signed-urls.md),
[ADR 0002](../docs/adr/0002-no-end-to-end-encryption.md)).

- Ne rendre public ni le bucket ni un objet : pas d'ACL `public-read`, pas de
  politique de bucket accordant un accès à `"Principal": "*"`, pas de site
  statique ni de CDN public devant le bucket.
- Activer le blocage de l'accès public quand le fournisseur le propose.
- Laisser le chiffrement au repos du fournisseur activé.
- Ne pas activer le versionnement sur le bucket des fichiers : une version
  conservée survivrait à la purge de la corbeille et à la suppression d'un
  compte.

La clé de l'API ne reçoit que les opérations dont l'API a besoin sur ce bucket.
Avec une politique au format IAM (AWS, MinIO et de nombreux fournisseurs) :

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "s3:GetObject",
        "s3:PutObject",
        "s3:DeleteObject",
        "s3:AbortMultipartUpload",
        "s3:ListMultipartUploadParts"
      ],
      "Resource": "arn:aws:s3:::shelf-files/*"
    },
    {
      "Effect": "Allow",
      "Action": ["s3:ListBucket", "s3:ListBucketMultipartUploads"],
      "Resource": "arn:aws:s3:::shelf-files"
    }
  ]
}
```

Une URL signée porte les droits de la clé qui l'a signée : sans `s3:PutObject`,
aucun envoi n'aboutit. `s3:GetObject` couvre aussi la vérification de taille à
la confirmation. Sans `s3:ListBucket`, AWS répond `403` au lieu de `404` pour un
objet absent, et la confirmation d'un envoi inachevé échoue au lieu de répondre
`upload_incomplete`. La liste couvre aussi l'envoi en plusieurs parties, la
purge et les copies côté serveur prévus par le stockage. Les autres fournisseurs
proposent des clés restreintes à un bucket depuis leur console.

Vérifier qu'un accès anonyme est refusé (`403` attendu sur le bucket et sur un
objet) et qu'aucune politique n'est attachée (`NoSuchBucketPolicy`, `404`) :

```sh
curl -s -o /dev/null -w '%{http_code}\n' "$S3_ENDPOINT/shelf-files/"
curl -s -o /dev/null -w '%{http_code}\n' "$S3_ENDPOINT/shelf-files/files/test"
s3 -H "x-amz-content-sha256: $EMPTY_SHA256" "$S3_ENDPOINT/shelf-files?policy="
```

## CORS du bucket des fichiers

Les navigateurs envoient et téléchargent directement auprès du S3 : le bucket des
fichiers doit autoriser l'origine du client web, **et elle seule**. Cette origine
est la valeur de `Authentication__ClientOrigin` (`Authentication:ClientOrigin`),
à reprendre à l'identique : schéma, hôte et port, sans chemin ni `/` final. Les
applications mobiles natives ne sont pas soumises au CORS. Le bucket des
sauvegardes ne reçoit aucune règle CORS.

`cors.xml`, ici pour l'origine `https://shelf.example.org` :

```xml
<CORSConfiguration>
  <CORSRule>
    <AllowedOrigin>https://shelf.example.org</AllowedOrigin>
    <AllowedMethod>GET</AllowedMethod>
    <AllowedMethod>HEAD</AllowedMethod>
    <AllowedMethod>PUT</AllowedMethod>
    <AllowedHeader>Content-Type</AllowedHeader>
    <AllowedHeader>Range</AllowedHeader>
    <ExposeHeader>ETag</ExposeHeader>
    <MaxAgeSeconds>3600</MaxAgeSeconds>
  </CORSRule>
</CORSConfiguration>
```

- `PUT` et `Content-Type` : envoi par URL signée, dont le type de contenu fait
  partie de la signature ; `PUT` sert aussi aux parties d'un envoi en plusieurs
  parties.
- `GET`, `HEAD` et `Range` : téléchargement et lecture vidéo par requêtes
  partielles.
- `ETag` exposé : le client le lit pour chaque partie d'un envoi en plusieurs
  parties.
- Aucune autre origine, ni `*`. Changer `Authentication__ClientOrigin` impose de
  mettre à jour cette règle.

Appliquer la règle, puis la relire :

```sh
s3 -X PUT "$S3_ENDPOINT/shelf-files?cors=" \
  -H "x-amz-content-sha256: $(sha256sum cors.xml | cut -d' ' -f1)" \
  -H "Content-MD5: $(openssl md5 -binary cors.xml | base64)" \
  --data-binary @cors.xml
s3 -H "x-amz-content-sha256: $EMPTY_SHA256" "$S3_ENDPOINT/shelf-files?cors="
```

Vérifier par une requête préliminaire qu'une autre origine ne reçoit pas
d'en-tête `Access-Control-Allow-Origin` :

```sh
for origin in https://shelf.example.org https://other.example; do
  curl -s -i -X OPTIONS "$S3_ENDPOINT/shelf-files/files/test" -H "Origin: $origin" \
    -H 'Access-Control-Request-Method: PUT' -H 'Access-Control-Request-Headers: content-type' |
    grep -i '^access-control-allow-origin' || echo "$origin: refusée"
done
```

MinIO n'implémente pas la configuration CORS par bucket (`NotImplemented`) : il
applique une règle globale, qui autorise toutes les origines par défaut. Cela
suffit en développement ; pour reproduire la restriction en local, démarrer
MinIO avec `MINIO_API_CORS_ALLOW_ORIGIN=http://localhost:3000` (l'origine
cliente locale).

## Sauvegarde quotidienne de PostgreSQL

PostgreSQL détient les noms, dossiers, espaces, partages et la corbeille ; le
bucket ne contient que des clés opaques. Sans la base, les fichiers restent dans
le bucket mais ne sont plus identifiables. Une tâche planifiée sur le VPS
exporte donc chaque jour la base vers le bucket des sauvegardes, distinct du
bucket des fichiers et accessible avec d'autres identifiants.

[`backup/shelf-pg-backup.sh`](backup/shelf-pg-backup.sh) exécute `pg_dump` au
format personnalisé, vérifie que l'archive est lisible, puis l'envoie sous
`postgres/shelf-<horodatage UTC>.dump`. Un échec de PostgreSQL ou du S3 termine
le script en erreur, sans rien envoyer de partiel. Prérequis sur le VPS :
`curl` 7.76 ou plus récent, et les clients `pg_dump`/`pg_restore` de même
version majeure que le serveur ou plus récente (PostgreSQL 18, paquet
`postgresql-client-18` du dépôt PGDG).

### Identifiants de sauvegarde

Créer pour la sauvegarde une clé distincte de celle de l'API, limitée à
l'écriture dans le bucket des sauvegardes. Un VPS compromis ne peut alors ni
lire, ni lister, ni supprimer les sauvegardes ; il peut en revanche écraser une
sauvegarde dont il devine le nom (voir [la rétention](#rétention)) :

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["s3:PutObject"], "Resource": "arn:aws:s3:::shelf-backups/postgres/*" }
  ]
}
```

Côté PostgreSQL, créer un rôle de lecture seule capable d'exporter toute la
base, avec mot de passe, et TLS hors de la machine locale :

```sh
createuser --pwprompt --role=pg_read_all_data shelf_backup
```

### Installation (systemd)

```sh
sudo install -m 0755 infra/backup/shelf-pg-backup.sh /usr/local/bin/shelf-pg-backup
sudo install -m 0644 infra/backup/shelf-pg-backup.service infra/backup/shelf-pg-backup.timer /etc/systemd/system/
sudo install -d -m 0700 /etc/shelf
sudo install -m 0600 /dev/null /etc/shelf/backup.env
sudoedit /etc/shelf/backup.env
```

Contenu de `/etc/shelf/backup.env` (lisible par root seulement) :

```sh
# PostgreSQL : variables libpq standard
PGHOST=127.0.0.1
PGPORT=5432
PGDATABASE=shelf
PGUSER=shelf_backup
PGPASSWORD=<mot de passe du rôle de sauvegarde>
# Bucket des sauvegardes
SHELF_BACKUP_S3_ENDPOINT=https://s3.fr-par.scw.cloud
SHELF_BACKUP_S3_REGION=fr-par
SHELF_BACKUP_S3_BUCKET=shelf-backups
SHELF_BACKUP_S3_FORCE_PATH_STYLE=false
SHELF_BACKUP_S3_ACCESS_KEY=<clé de sauvegarde>
SHELF_BACKUP_S3_SECRET_KEY=<secret de sauvegarde>
```

Le service s'exécute sous un utilisateur Unix éphémère (`DynamicUser`) : la
connexion à PostgreSQL passe par TCP avec mot de passe, pas par
l'authentification `peer`. Activer la tâche, lancer une première sauvegarde et
consulter son résultat :

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now shelf-pg-backup.timer
sudo systemctl start shelf-pg-backup.service
sudo journalctl -u shelf-pg-backup.service -n 20
systemctl list-timers shelf-pg-backup.timer
```

Le minuteur déclenche la sauvegarde chaque jour vers 03 h 30, heure du VPS ; une
exécution manquée pendant un arrêt du VPS est rattrapée au démarrage. Le journal
indique la clé et la taille de chaque sauvegarde ; surveiller les échecs avec
`systemctl --failed` ou un `OnFailure=` vers l'alerte de votre choix.

### Rétention

Le script ne supprime rien. Expirer les anciennes sauvegardes par une règle de
cycle de vie du bucket, ici après 30 jours :

```xml
<LifecycleConfiguration>
  <Rule>
    <ID>expire-postgres-backups</ID>
    <Filter><Prefix>postgres/</Prefix></Filter>
    <Status>Enabled</Status>
    <Expiration><Days>30</Days></Expiration>
  </Rule>
</LifecycleConfiguration>
```

```sh
s3 -X PUT "$S3_ENDPOINT/shelf-backups?lifecycle=" \
  -H "x-amz-content-sha256: $(sha256sum lifecycle.xml | cut -d' ' -f1)" \
  -H "Content-MD5: $(openssl md5 -binary lifecycle.xml | base64)" \
  --data-binary @lifecycle.xml
```

Si le fournisseur ne prend pas en charge cette opération, configurer la
rétention depuis sa console ou supprimer les anciennes sauvegardes à la main.
Pour protéger les sauvegardes contre l'écrasement par un VPS compromis, activer
sur ce bucket, quand le fournisseur le propose, le verrouillage d'objets (Object
Lock) ou le versionnement ; avec le versionnement, ajouter à la règle une
expiration des versions anciennes (`NoncurrentVersionExpiration`).

### Restauration

À faire après la perte du VPS ou de la base, depuis le nouveau serveur, avec la
clé d'administration S3 (la clé de sauvegarde ne peut pas lire).

1. Arrêter l'API et le minuteur de sauvegarde s'ils tournent, pour qu'aucune
   écriture ni sauvegarde partielle ne se produise pendant la restauration.
2. Lister les sauvegardes et choisir la plus récente (les noms se trient par
   date) :

   ```sh
   s3 -H "x-amz-content-sha256: $EMPTY_SHA256" \
     "$S3_ENDPOINT/shelf-backups?list-type=2&prefix=postgres%2F" |
     grep -o '<Key>[^<]*</Key>' | sed 's/<[^>]*>//g' | sort
   ```

3. Télécharger l'archive :

   ```sh
   s3 -H "x-amz-content-sha256: $EMPTY_SHA256" -o shelf.dump \
     "$S3_ENDPOINT/shelf-backups/postgres/shelf-20261022T033012Z.dump"
   ```

4. Restaurer dans une base **vide**, appartenant au rôle de l'API (ici `shelf`),
   en une seule transaction, avec un rôle administrateur de PostgreSQL. La
   sauvegarde ne contient pas les rôles : sur un nouveau serveur, créer d'abord
   celui de l'API (et celui de la sauvegarde, comme à l'installation) :

   ```sh
   createuser --pwprompt shelf
   createdb --owner=shelf shelf
   pg_restore --no-owner --role=shelf --single-transaction --dbname=shelf shelf.dump
   ```

   Pour remplacer une base existante, la renommer ou la supprimer d'abord
   (`dropdb shelf`) plutôt que restaurer par-dessus.

5. Appliquer les migrations de la version déployée, avec la configuration de
   l'API (`ConnectionStrings__Shelf`), sans risque si elles sont déjà
   présentes, puis redémarrer l'API et le minuteur :

   ```sh
   dotnet Shelf.Api.dll migrate
   ```

6. Vérifier qu'un utilisateur se connecte et télécharge un fichier connu.

La restauration ramène la base à l'état de la sauvegarde, au plus un jour plus
tôt. Les fichiers envoyés depuis restent dans le bucket sans nom ni dossier, et
les fichiers purgés depuis apparaissent mais ne se téléchargent plus. Les
comptes, sessions et liens actifs sont ceux de la sauvegarde.

La sauvegarde n'est pas testée automatiquement : rejouer cette procédure sur une
base jetable après l'installation, puis à chaque changement de version majeure
de PostgreSQL.

## Durabilité des fichiers

Shelf ne fait **aucune seconde copie des fichiers** : ni réplication vers un
autre bucket ou fournisseur, ni sauvegarde du bucket des fichiers. Leur
conservation repose entièrement sur la durabilité annoncée par le fournisseur
S3 ; seule la base PostgreSQL est sauvegardée. En conséquence :

- la suppression du bucket, de ses objets ou du compte chez le fournisseur est
  définitive ; seule la corbeille de Shelf, pendant 30 jours, protège contre une
  suppression faite depuis l'application ;
- choisir un fournisseur et une classe de stockage dont la durabilité convient
  aux photos de famille, et protéger l'accès au compte du fournisseur (clé
  racine hors du VPS, authentification à deux facteurs).
