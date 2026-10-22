# Stockage des fichiers

Spécification : [issue #3](https://github.com/OliveLeb/shelf/issues/3). Livré par
[l’issue #4](https://github.com/OliveLeb/shelf/issues/4) (envoi et téléchargement
d’un fichier dans son **espace personnel**),
[l’issue #5](https://github.com/OliveLeb/shelf/issues/5) (**dossiers**, renommage,
déplacement et noms uniques) et [l’issue #8](https://github.com/OliveLeb/shelf/issues/8)
(**espace commun**). Le vocabulaire suit [`CONTEXT.md`](../CONTEXT.md).

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
| `GET /spaces/common/elements?sort=&order=` | — | 200, éléments de la racine de l’espace commun |
| `GET /folders/{id}/elements?sort=&order=` | — | 200, éléments du dossier |
| `POST /folders` | `name`, `parentId` (dossier parent ; absent ou `null` : racine), `space` | 201, dossier créé |
| `POST /elements/{id}/rename` | `name` | 200, élément renommé |
| `POST /elements/{id}/move` | `parentId` (dossier de destination ; `null` : racine), `space` | 200, élément déplacé |
| `POST /uploads` | `name`, `size`, `contentType` (défaut `application/octet-stream`), `parentId`, `onConflict`, `space` | 201, `{id, url, method, headers, expiresAt}` |
| `POST /uploads/{id}/complete` | — | 201, fichier créé |
| `GET /files/{id}/download` | — | 200, `{url, expiresAt}` |

Un élément contient `id`, `kind` (`file` ou `folder`), `name`, `parentId`
(`null` à la racine), `size` et `contentType` (`null` pour un dossier), `addedAt`
et `addedBy` (`{id, username}`, ou `null` si le compte n’existe plus).

### Espace commun

La migration `005_common_space` crée l’unique espace commun, sans propriétaire.
Tout utilisateur actif peut y parcourir, créer, envoyer, télécharger, renommer
et déplacer les éléments, quel que soit l’utilisateur indiqué dans `addedBy`.
Cet auteur reste celui de l’ajout initial, même après un renommage ou un
déplacement. La suppression de son compte conserve les éléments communs et rend
leur auteur anonyme (`addedBy: null`).

Pour créer un dossier ou déclarer un envoi à la racine commune, fournir
`"space": "common"`. Sans `parentId`, l’absence de `space` (ou `"personal"`)
sélectionne son espace personnel. Avec `parentId`, le dossier détermine l’espace ;
si `space` est fourni, il doit correspondre à celui du dossier.

Pour déplacer un élément personnel vers la racine commune, fournir
`{ "space": "common" }` à `POST /elements/{id}/move`. Pour le placer dans un
dossier commun, fournir son `parentId`, avec ou sans `space`. Seul le propriétaire
de l’espace personnel peut effectuer ce transfert ; un tiers, administrateur
compris, reçoit `404`.

Le transfert conserve les identifiants et toutes les métadonnées des descendants,
sauf l’emplacement de l’élément déplacé et l’espace de son contenu. Les envois en
cours dans les dossiers transférés suivent leur destination et restent confirmables
par leur déclarant. Aucune copie ni modification d’objet S3 n’a lieu.

Un élément commun ne peut jamais être déplacé vers un espace personnel
(`403 common_to_personal_forbidden`). Sans `space` ni `parentId`, un déplacement
va à la racine de l’espace actuel de l’élément, donc à la racine commune pour un
élément commun. Les noms uniques, le tri et la politique `keepBoth` s’appliquent
de la même façon dans les deux espaces.

### Dossiers

Les dossiers s’imbriquent sans limite de profondeur. Deux éléments d’un même
dossier, racine comprise, ne portent jamais le même nom, sans tenir compte de la
casse : un index unique PostgreSQL le garantit, y compris sous concurrence. Créer,
renommer ou déplacer vers un nom pris répond `409 name_conflict`.

Un déplacement reste dans l’espace de l’élément, sauf transfert de son espace
personnel vers l’espace commun. Déplacer un dossier dans
lui-même ou dans un de ses descendants répond `409 folder_cycle` ; les
déplacements s’exécutent l’un après l’autre, si bien que deux déplacements
concurrents ne peuvent pas former un cycle. Ils verrouillent les deux espaces
accessibles dans un ordre stable. Les autres écritures prennent des verrous
partagés compatibles entre eux, mais attendent la fin d’un déplacement : aucun
descendant ne peut être ajouté au milieu d’un transfert. Les clés étrangères des
parents sont vérifiées à la fin de la transaction pour transférer ensemble un
dossier, ses descendants et ses envois en cours.

Le contenu d’un dossier se trie avec `sort` : `name` (défaut, sans tenir compte
de la casse), `addedAt` ou `size`, et `order` : `asc` (défaut) ou `desc`. Les
dossiers précèdent toujours les fichiers.

### Envoi

1. Déclarer l’envoi avec `POST /uploads`. L’API réserve une clé d’objet et
   renvoie une URL signée.
2. Envoyer les octets avec la méthode `method` (`PUT`) sur `url`, avec exactement
   les en-têtes `headers` : le type de contenu fait partie de la signature.
3. Confirmer avec `POST /uploads/{id}/complete`. L’API vérifie auprès du S3 que
   l’objet existe et que sa taille égale la taille déclarée. Le fichier
   n’apparaît qu’après cette confirmation.

`parentId` désigne le dossier de destination (absent ou `null` : racine de
l’espace sélectionné, personnel par défaut). Le nom est vérifié dans la destination à la déclaration,
puis de nouveau à la confirmation par une contrainte PostgreSQL, car un autre
élément a pu le prendre entre-temps. Sans `onConflict`, un nom pris répond
`409 name_conflict`. Avec `"onConflict": "keepBoth"` (« garder les deux »), le
fichier est renommé automatiquement à la confirmation : `photo.jpg` devient
`photo (1).jpg`, puis `photo (2).jpg`… Le nom définitif figure dans la réponse
de la confirmation.

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
| 400 | `invalid_space` | `space` autre que `personal` ou `common` |
| 400 | `invalid_sort`, `invalid_order` | `sort` ou `order` inconnu |
| 401 | `authentication_required` | Session absente, expirée ou compte désactivé |
| 403 | `common_to_personal_forbidden` | Déplacement d’un élément commun vers un espace personnel |
| 404 | `upload_not_found` | Envoi inconnu, déjà confirmé ou déclaré par un autre utilisateur |
| 404 | `element_not_found` | Élément inexistant ou invisible pour l’utilisateur (ou dossier passé au téléchargement) |
| 404 | `folder_not_found` | Dossier parent, de destination ou listé inexistant, invisible, qui est un fichier ou ne correspond pas à `space` |
| 409 | `upload_incomplete` | Objet absent du bucket à la confirmation ; envoyer puis confirmer de nouveau |
| 409 | `size_mismatch` | Taille de l’objet différente de la déclaration ; renvoyer le contenu puis confirmer de nouveau |
| 409 | `name_conflict` | Un élément du même nom, sans tenir compte de la casse, existe déjà dans le dossier |
| 409 | `folder_cycle` | Déplacement d’un dossier dans lui-même ou un de ses descendants |
| 503 | `database_unavailable`, `storage_unavailable` | PostgreSQL ou S3 indisponible |

Un élément d’un autre espace personnel répond `404`, y compris à un
administrateur : le rôle administrateur ne donne aucun accès au contenu.

## Limites actuelles

Les partages, la corbeille, l’envoi en plusieurs
parties, l’expiration des envois abandonnés, la politique « remplacer », les miniatures et la purge des
objets à la suppression d’un compte sont livrés par les autres sous-issues de #3.
En attendant #15, la suppression d’un compte efface ses métadonnées de stockage
mais laisse ses objets dans le bucket.
