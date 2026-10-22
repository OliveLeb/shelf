# Stockage des fichiers

Spécification : [issue #3](https://github.com/OliveLeb/shelf/issues/3). Livré par
[l’issue #4](https://github.com/OliveLeb/shelf/issues/4) (envoi et téléchargement
d’un fichier dans son **espace personnel**) et
[l’issue #5](https://github.com/OliveLeb/shelf/issues/5) (**dossiers**, renommage,
déplacement et noms uniques), et
[l’issue #6](https://github.com/OliveLeb/shelf/issues/6) (envoi en plusieurs parties
reprenable et expiration des envois non confirmés). Le vocabulaire suit [`CONTEXT.md`](../CONTEXT.md).

Le contenu des fichiers vit dans un bucket compatible S3 privé et transite
directement entre le client et le S3 par URL signées de courte durée
([ADR 0001](adr/0001-direct-s3-transfers-with-signed-urls.md)). PostgreSQL
conserve les espaces, les éléments et les envois en cours. La clé d’objet d’un
fichier est un identifiant opaque (`files/<uuid>`), indépendant de son nom et de
son espace : renommer ou déplacer un **élément** ne touche pas au S3.

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
| `Storage__PartSizeMiB` | Taille des parties d’un envoi en plusieurs parties, et seuil au-delà duquel il s’applique ; défaut 16, minimum 5 (minimum S3) |
| `Storage__UploadExpiryHours` | Délai après la déclaration au-delà duquel un envoi non confirmé expire ; défaut 24 |
| `Storage__UploadCleanupSeconds` | Intervalle de la tâche de fond qui purge les envois expirés ; défaut 300 |

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
| `GET /spaces/personal/elements?sort=&order=` | — | 200, éléments de la racine de son espace personnel |
| `GET /folders/{id}/elements?sort=&order=` | — | 200, éléments du dossier |
| `POST /folders` | `name`, `parentId` (dossier parent ; absent ou `null` : racine) | 201, dossier créé |
| `POST /elements/{id}/rename` | `name` | 200, élément renommé |
| `POST /elements/{id}/move` | `parentId` (dossier de destination ; `null` : racine) | 200, élément déplacé |
| `POST /uploads` | `name`, `size`, `contentType` (défaut `application/octet-stream`), `parentId`, `onConflict` | 201, `{id, method, headers, partSize, parts, expiresAt}` |
| `GET /uploads/{id}` | — | 200, même forme, avec les seules parties manquantes |
| `POST /uploads/{id}/complete` | — | 201, fichier créé |
| `GET /files/{id}/download` | — | 200, `{url, expiresAt}` |

Un élément contient `id`, `kind` (`file` ou `folder`), `name`, `parentId`
(`null` à la racine), `size` et `contentType` (`null` pour un dossier), `addedAt`
et `addedBy` (`{id, username}`, ou `null` si le compte n’existe plus).

### Dossiers

Les dossiers s’imbriquent sans limite de profondeur. Deux éléments d’un même
dossier, racine comprise, ne portent jamais le même nom, sans tenir compte de la
casse : un index unique PostgreSQL le garantit, y compris sous concurrence. Créer,
renommer ou déplacer vers un nom pris répond `409 name_conflict`.

Le déplacement reste dans l’espace de l’élément. Déplacer un dossier dans
lui-même ou dans un de ses descendants répond `409 folder_cycle` ; les
déplacements d’un même espace s’exécutent l’un après l’autre, si bien que deux
déplacements concurrents ne peuvent pas former un cycle.

Le contenu d’un dossier se trie avec `sort` : `name` (défaut, sans tenir compte
de la casse), `addedAt` ou `size`, et `order` : `asc` (défaut) ou `desc`. Les
dossiers précèdent toujours les fichiers.

### Envoi

1. Déclarer l’envoi avec `POST /uploads`. L’API réserve une clé d’objet et
   renvoie la liste `parts` des parties à envoyer, chacune avec son numéro
   `number` et une URL signée `url`, valables jusqu’à `expiresAt`.
2. Envoyer chaque partie avec la méthode `method` (`PUT`) sur son `url`, avec
   exactement les en-têtes `headers`. La partie `n` contient les octets
   `[(n-1)·partSize, n·partSize)` du fichier ; la dernière est plus courte. Les
   parties peuvent partir en parallèle et dans n’importe quel ordre.
3. Confirmer avec `POST /uploads/{id}/complete`. Le fichier n’apparaît qu’après
   cette confirmation.

Un fichier d’au plus `Storage__PartSizeMiB` est envoyé en une seule partie : un
`PUT` simple dont l’en-tête `Content-Type` fait partie de la signature. Au-delà,
l’API ouvre un envoi S3 en plusieurs parties ; `headers` est alors vide. La taille
des parties grandit, par MiB entiers, pour rester sous la limite S3 de 10 000
parties. Il n’y a ni limite applicative de taille ni quota : seules les limites
du fournisseur s’appliquent (5 Tio par objet chez AWS).

**Reprise.** Après une coupure, ou quand les URL ont expiré, `GET /uploads/{id}`
renvoie de nouvelles URL pour les seules parties absentes du S3 ou reçues avec
une taille différente de celle attendue. Une liste vide signifie que tout est
reçu. Chaque réponse liste au plus 1 000 parties : les envoyer, puis redemander
les suivantes.

**Confirmation.** L’API liste auprès du S3 les parties reçues : une partie
manquante répond `upload_incomplete`, une partie de taille inattendue
`size_mismatch`. Elle assemble ensuite l’objet, puis vérifie que l’objet existe et
que sa taille égale la taille déclarée. Le client n’a pas à fournir les ETag des
parties ; le CORS du bucket n’a donc pas besoin de les exposer.

`parentId` désigne le dossier de destination (absent ou `null` : racine de
l’espace personnel). Le nom est vérifié dans la destination à la déclaration,
puis de nouveau à la confirmation par une contrainte PostgreSQL, car un autre
élément a pu le prendre entre-temps. Sans `onConflict`, un nom pris répond
`409 name_conflict`. Avec `"onConflict": "keepBoth"` (« garder les deux »), le
fichier est renommé automatiquement à la confirmation : `photo.jpg` devient
`photo (1).jpg`, puis `photo (2).jpg`… Le nom définitif figure dans la réponse
de la confirmation.

**Expiration.** Un envoi non confirmé expire `Storage__UploadExpiryHours` après
sa déclaration : il répond alors `upload_not_found`, et les URL signées ne
restent jamais valables au-delà. Une tâche de fond annule ses parties en attente
et supprime son objet du bucket. En cas de panne de PostgreSQL ou du S3, elle
réessaie au tour suivant. Un envoi dont le compte ou la destination a disparu ne
peut plus être confirmé ; il est purgé de même à son expiration, quand plus
aucune URL signée ne permet d’y écrire.

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
| 400 | `invalid_conflict_policy` | `onConflict` autre que `keepBoth` |
| 400 | `invalid_sort`, `invalid_order` | `sort` ou `order` inconnu |
| 401 | `authentication_required` | Session absente, expirée ou compte désactivé |
| 404 | `upload_not_found` | Envoi inconnu, expiré, sans destination, déjà confirmé ou déclaré par un autre utilisateur |
| 404 | `element_not_found` | Élément inexistant ou invisible pour l’utilisateur (ou dossier passé au téléchargement) |
| 404 | `folder_not_found` | Dossier parent, de destination ou listé inexistant, invisible, ou qui est un fichier |
| 409 | `upload_incomplete` | Objet ou partie absent du bucket à la confirmation ; reprendre l’envoi puis confirmer de nouveau |
| 409 | `size_mismatch` | Taille de l’objet ou d’une partie différente de l’attendu ; reprendre l’envoi puis confirmer de nouveau |
| 409 | `name_conflict` | Un élément du même nom, sans tenir compte de la casse, existe déjà dans le dossier |
| 409 | `folder_cycle` | Déplacement d’un dossier dans lui-même ou un de ses descendants |
| 503 | `database_unavailable`, `storage_unavailable` | PostgreSQL ou S3 indisponible |

Un élément d’un autre espace personnel répond `404`, y compris à un
administrateur : le rôle administrateur ne donne aucun accès au contenu.

## Limites actuelles

L’espace commun, les partages, la corbeille, la politique « remplacer », les miniatures et la purge des
objets à la suppression d’un compte sont livrés par les autres sous-issues de #3.
En attendant #15, la suppression d’un compte efface ses métadonnées de stockage
mais laisse ses objets dans le bucket.
