# Stockage des fichiers

Spécification : [issue #3](https://github.com/OliveLeb/shelf/issues/3). Livré par
[l’issue #4](https://github.com/OliveLeb/shelf/issues/4) : envoi et téléchargement
d’un fichier à la racine de son **espace personnel**. Le vocabulaire suit
[`CONTEXT.md`](../CONTEXT.md).

Le contenu des fichiers vit dans un bucket compatible S3 privé et transite
directement entre le client et le S3 par URL signées de courte durée
([ADR 0001](adr/0001-direct-s3-transfers-with-signed-urls.md)). PostgreSQL
conserve les espaces, les éléments et les envois en cours. La clé d’objet d’un
fichier est un identifiant opaque (`files/<uuid>`), indépendant de son nom et de
son espace.

## Configuration

Seules les opérations standard de l’API S3 sont utilisées ; tout fournisseur
compatible convient. Fournir ces valeurs par l’environnement ou un gestionnaire
de secrets, jamais dans un fichier versionné :

| Variable | Usage |
| --- | --- |
| `Storage__S3__Endpoint` | URL HTTP(S) du service S3, joignable par l’API **et par les clients** (les URL signées la reprennent) |
| `Storage__S3__Region` | Région de signature (ex. `us-east-1` pour MinIO) |
| `Storage__S3__Bucket` | Bucket privé existant |
| `Storage__S3__AccessKey`, `Storage__S3__SecretKey` | Identifiants limités à ce bucket |
| `Storage__S3__ForcePathStyle` | `true` pour l’adressage par chemin (MinIO) ; défaut `false` (sous-domaine) |
| `Storage__SignedUrlMinutes` | Validité des URL signées ; défaut 15 |

Le démarrage HTTP échoue si la configuration S3 est incomplète ; les commandes
`migrate` et `admin` n’en ont pas besoin. La migration `002_storage` crée
l’espace personnel de chaque compte existant ; chaque nouveau compte, par
inscription ou commande serveur, reçoit le sien. Les URL signées et les
identifiants S3 ne sont pas journalisés ; ne pas activer de journalisation des
requêtes HTTP sortantes de l’API.

Le développement local utilise MinIO : voir [l’infrastructure](../infra/README.md).
Le déploiement (choix du fournisseur, bucket privé, CORS, sauvegarde de
PostgreSQL et restauration) est décrit dans
[l’exploitation du stockage](../infra/storage.md).

## Contrat HTTP

Toutes les opérations exigent `Authorization: Bearer <token>`, répondent en
JSON avec `Cache-Control: no-store` et signalent les erreurs par
`{ "error": "code" }`, comme [l’authentification](authentication.md).

| Méthode et route | Corps | Succès |
| --- | --- | --- |
| `GET /spaces/personal/elements` | — | 200, fichiers de la racine de son espace personnel, triés par nom |
| `POST /uploads` | `name`, `size`, `contentType` (défaut `application/octet-stream`) | 201, `{id, url, method, headers, expiresAt}` |
| `POST /uploads/{id}/complete` | — | 201, fichier créé |
| `GET /files/{id}/download` | — | 200, `{url, expiresAt}` |

Un fichier contient `id`, `name`, `size`, `contentType`, `addedAt` et
`addedBy` (`{id, username}`, ou `null` si le compte n’existe plus).

### Envoi

1. Déclarer l’envoi avec `POST /uploads`. L’API réserve une clé d’objet et
   renvoie une URL signée.
2. Envoyer les octets avec la méthode `method` (`PUT`) sur `url`, avec exactement
   les en-têtes `headers` : le type de contenu fait partie de la signature.
3. Confirmer avec `POST /uploads/{id}/complete`. L’API vérifie auprès du S3 que
   l’objet existe et que sa taille égale la taille déclarée. Le fichier
   n’apparaît qu’après cette confirmation.

Le nom doit être libre dans la destination, sans tenir compte de la casse : il
est vérifié à la déclaration, puis de nouveau à la confirmation par une
contrainte PostgreSQL, car un autre fichier a pu le prendre entre-temps.

Le bucket doit autoriser le CORS de l’origine cliente pour les transferts depuis
un navigateur : voir [la règle CORS](../infra/storage.md#cors-du-bucket-des-fichiers).

### Téléchargement

`GET /files/{id}/download` renvoie une URL signée de lecture. La réponse du S3
porte `Content-Disposition: attachment` avec le nom du fichier (encodé en UTF-8
selon RFC 5987). Les requêtes partielles (`Range`) sont prises en charge par le S3.

### Erreurs

| Statut | Code | Cas |
| --- | --- | --- |
| 400 | `invalid_name` | Nom vide, de plus de 255 caractères, `.`/`..`, avec `/`, `\` ou caractère de contrôle |
| 400 | `invalid_size` | Taille absente ou négative |
| 400 | `invalid_content_type` | Type de contenu invalide |
| 401 | `authentication_required` | Session absente, expirée ou compte désactivé |
| 404 | `upload_not_found` | Envoi inconnu, déjà confirmé ou déclaré par un autre utilisateur |
| 404 | `element_not_found` | Fichier inexistant ou invisible pour l’utilisateur |
| 409 | `upload_incomplete` | Objet absent du bucket à la confirmation ; envoyer puis confirmer de nouveau |
| 409 | `size_mismatch` | Taille de l’objet différente de la déclaration ; renvoyer le contenu puis confirmer de nouveau |
| 409 | `name_conflict` | Un élément du même nom, sans tenir compte de la casse, existe déjà |
| 503 | `database_unavailable`, `storage_unavailable` | PostgreSQL ou S3 indisponible |

Un fichier d’un autre espace personnel répond `404`, y compris à un
administrateur : le rôle administrateur ne donne aucun accès au contenu.

## Limites actuelles

Les dossiers, l’espace commun, les partages, la corbeille, l’envoi en plusieurs
parties, l’expiration des envois abandonnés, les miniatures et la purge des
objets à la suppression d’un compte sont livrés par les autres sous-issues de #3.
En attendant #15, la suppression d’un compte efface ses métadonnées de stockage
mais laisse ses objets dans le bucket.
