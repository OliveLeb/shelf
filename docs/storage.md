# Stockage des fichiers

Spécification : [issue #3](https://github.com/OliveLeb/shelf/issues/3). Livré par
[l’issue #4](https://github.com/OliveLeb/shelf/issues/4) (envoi et téléchargement
d’un fichier dans son **espace personnel**),
[l’issue #5](https://github.com/OliveLeb/shelf/issues/5) (**dossiers**, renommage,
déplacement et noms uniques) et [l’issue #8](https://github.com/OliveLeb/shelf/issues/8)
(**espace commun**) et [l’issue #9](https://github.com/OliveLeb/shelf/issues/9)
(**partages en Lecture** et **Partagés avec moi**), puis
[l’issue #10](https://github.com/OliveLeb/shelf/issues/10) (**corbeille et purge**) et
[l’issue #12](https://github.com/OliveLeb/shelf/issues/12) (**partages en Écriture**).
L’[issue #11](https://github.com/OliveLeb/shelf/issues/11) ajoute la **copie indépendante des éléments**.
Le vocabulaire suit [`CONTEXT.md`](../CONTEXT.md).

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
| `Storage__PartSizeMiB` | Taille des parties d’envoi ; défaut 16 Mio, minimum 5 Mio, augmentée pour rester sous 10 000 parties S3 |
| `Storage__UploadExpiryHours` | Expiration d’un envoi non confirmé ; défaut 24 heures |
| `Storage__UploadCleanupSeconds` | Intervalle de nettoyage des envois expirés ; défaut 300 secondes |
| `Storage__S3__SingleCopyLimitBytes` | Limite de copie S3 simple du fournisseur ; défaut 5368709120 (5 Gio), entre 5 Mio et 5 Gio |
| `Storage__S3__CopyPartSizeBytes` | Taille des parties de copie ; défaut 134217728 (128 Mio), entre 5 Mio et 5 Gio ; augmentée si nécessaire pour rester sous 10 000 parties |

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
| `POST /elements/{id}/copy` | `parentId` (dossier de destination ; `null` : racine), `space` (personnel par défaut) | 201, nouvel élément, avec son nom définitif |
| `POST /uploads` | `name`, `size`, `contentType` (défaut `application/octet-stream`), `parentId`, `onConflict`, `space` ; ou `replaceFileId`, `size`, `contentType` pour remplacer un fichier précis | 201, `{id, method, headers, partSize, parts: [{number, url}], expiresAt}` |
| `GET /uploads/{id}` | — | 200, même ticket avec uniquement les parties manquantes ou de taille incorrecte |
| `POST /uploads/{id}/complete` | — | 201, fichier créé |
| `GET /files/{id}/download` | — | 200, `{url, expiresAt}` |
| `PUT /elements/{id}/shares/{recipientId}` | `level` (`read` ou `write`) | 200, partage créé ou mis à jour |
| `GET /elements/{id}/shares` | — | 200, partages directs et hérités de l’élément, réservés au propriétaire |
| `DELETE /elements/{id}/shares/{recipientId}` | — | 204, partage direct retiré |
| `GET /shares/received` | — | 200, racines des partages reçus avec leur propriétaire et leur niveau |
| `DELETE /elements/{id}` | — | 204, élément et contenu placés en corbeille |
| `GET /spaces/{space}/trash` | — | 200, corbeille `personal` ou `common` |
| `POST /trash/{id}/restore` | — | 200, élément restauré |
| `DELETE /trash/{id}` | — | 204, élément purgé définitivement |
| `DELETE /spaces/{space}/trash` | — | 204, corbeille vidée |

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
de l’espace personnel peut effectuer ce transfert ; un destinataire de partage
reçoit `403`, un tiers sans accès (administrateur compris) reçoit `404`.

Le transfert conserve les identifiants et toutes les métadonnées des descendants,
sauf l’emplacement de l’élément déplacé et l’espace de son contenu. Les envois en
cours dans les dossiers transférés suivent leur destination et restent confirmables
par leur déclarant. Aucune copie ni modification d’objet S3 n’a lieu.

Un élément commun ne peut jamais être déplacé vers un espace personnel
(`403 common_to_personal_forbidden`). Sans `space` ni `parentId`, un déplacement
va à la racine de l’espace actuel de l’élément, donc à la racine commune pour un
élément commun. Les noms uniques, le tri et la politique `keepBoth` s’appliquent
de la même façon dans les deux espaces.

### Partages en Lecture et en Écriture

Seul le **propriétaire** d’un élément personnel peut créer, modifier, consulter
ou retirer ses partages. `PUT /elements/{id}/shares/{recipientId}` accorde la
Lecture (`read`) ou l’Écriture (`write`) à un autre compte actif. Le couple élément/destinataire est unique :
répéter le `PUT` met à jour le niveau du partage sans le dupliquer. Le niveau
est stocké et modifiable par cette même route. La migration `008_write_shares`
ajoute le niveau Écriture et les envois ciblant un fichier à remplacer.

Un partage contient `elementId` (racine du partage), `recipient` (`{id, username}`)
et `level`. La consultation par le propriétaire inclut les partages directs et
ceux hérités des dossiers parents. Pour modifier ou retirer un partage hérité,
utiliser son `elementId`. Retirer un partage direct absent répond aussi `204`.
Les éléments communs n’ont aucun propriétaire et ne peuvent pas être partagés.

Un partage de dossier couvre tous ses descendants actuels et futurs. Les droits
sont recalculés depuis les ancêtres à chaque accès : déplacer un fichier dans
un dossier partagé lui donne la Lecture ; l’en sortir lui retire cet héritage.
Les partages qui se chevauchent ne se réduisent pas entre eux : retirer celui
d’un dossier laisse actif un éventuel partage direct sur un de ses fichiers.
Le transfert vers l’espace commun retire les partages du sous-arbre transféré.

`GET /shares/received` renvoie une liste de `{element, owner, level}` ; `level`
est le droit effectif le plus élevé, partages hérités compris. `owner`
contient l’identifiant et le nom du propriétaire, indépendamment de l’auteur de
l’ajout. Seules les racines explicitement partagées apparaissent dans cette vue ;
les descendants se parcourent avec `GET /folders/{id}/elements`. Un fichier
isolé peut être téléchargé sans rendre ses dossiers parents visibles. Ces
éléments restent dans l’espace du propriétaire et n’apparaissent jamais dans
l’espace personnel du destinataire.

Le destinataire peut parcourir et télécharger ; renommer, déplacer, créer un
dossier ou déclarer un envoi dans un dossier en Lecture répond `403`.
Consulter, créer, modifier ou retirer des partages est aussi interdit au
destinataire (`403`). Un tiers sans partage, administrateur compris, reçoit
`404` pour un élément invisible. Retirer le dernier partage couvrant un élément
le rend de nouveau invisible. Une URL S3 déjà délivrée reste toutefois valide
jusqu’à son expiration ; le retrait interdit immédiatement d’en obtenir une nouvelle.

### Copie

Toute personne ayant au moins la Lecture peut copier un fichier ou un dossier
avec `POST /elements/{id}/copy`. Un corps `{}` copie à la racine de **son propre
espace personnel**, même si la source est commune ou partagée. `space: "common"`
sélectionne la racine commune ; `parentId` sélectionne un dossier où l’utilisateur
peut ajouter. Un dossier visible en Lecture seule répond `403 write_forbidden` ;
une source ou destination invisible répond `404`, administrateur compris.
Les dossiers partagés en Écriture sont aussi des destinations possibles.

Un dossier est copié avec tous ses descendants, y compris les dossiers vides.
Les nouveaux éléments ont de nouveaux identifiants, une date d’ajout courante
et l’utilisateur qui copie comme `addedBy`. Le propriétaire est celui de
l’espace de destination. Aucun partage de la source, direct ou hérité, n’est
recopié. Les droits de l’emplacement de destination restent applicables.
Le nom de la racine copiée est conservé si disponible, sinon renommé selon
la même règle que `keepBoth` (`photo (1).jpg`, `photo (2).jpg`…), même sous
concurrence et en cas de conflit avec un dossier. Les noms des descendants
restent inchangés.

Chaque fichier reçoit une nouvelle clé S3 : `CopyObject` jusqu’à la limite
configurée, puis `UploadPartCopy` et finalisation en plusieurs parties au-delà.
Aucun octet du fichier ne transite par l’API. La taille est vérifiée avant la
copie et l’ETag de la source conditionne chaque requête, pour ne pas assembler
des parties de contenus différents. Une source modifiée pendant la copie
répond `409 source_changed`. Modifier ou supprimer un objet ne touche pas à
l’autre.

La copie est synchrone. Elle verrouille les espaces concernés dans un ordre
stable et revérifie les droits après l’attente des verrous. Les écritures,
déplacements et changements de partages attendent sa fin ; les lectures restent
possibles. Les métadonnées du nouvel arbre ne deviennent visibles qu’après la
réussite de toute la copie. Copier un dossier dans lui-même ou un descendant
copie un instantané fini de l’arbre source, sans inclure les nouveaux éléments.
Configurer les délais du proxy pour les copies longues.

En cas d’échec, la transaction est annulée, les objets déjà copiés sont supprimés
au mieux et l’envoi en plusieurs parties est annulé. Le balayage des objets
orphelins attend la fin des copies en cours avant de comparer les références
PostgreSQL : il ne supprime pas une copie dont la transaction n’est pas encore
validée. Après un arrêt du processus ou un échec du nettoyage immédiat, ce
balayage reprend les objets sans référence. Voir les
[précautions d’exploitation](../infra/storage.md#copies-côté-serveur).

### Écriture sur les éléments partagés

En Écriture, le destinataire crée des dossiers, envoie, remplace, renomme,
déplace et supprime les éléments à l’intérieur d’un dossier partagé. Les ajouts
appartiennent au propriétaire de l’espace, même si `addedBy` désigne le
destinataire. Une suppression rejoint la corbeille du propriétaire, que lui seul
peut restaurer. Sur un fichier partagé isolément, l’Écriture autorise le
remplacement de son contenu, sans donner accès au dossier parent.

La racine de tout partage reste sous le seul contrôle du propriétaire : un
destinataire ne peut ni la renommer, ni la déplacer, ni la supprimer (`403`).
Déplacer ou supprimer un dossier contenant une autre racine partagée est aussi
refusé : cela ne doit pas contourner cette protection. Un partage en Lecture
ne réduit jamais une Écriture héritée ; inversement, une Écriture directe sur
un fichier prévaut sur la Lecture de son dossier.

Un destinataire peut déplacer les éléments non protégés entre deux dossiers
du même propriétaire si la source et la destination lui accordent l’Écriture.
Une destination en Lecture seule est refusée. Il ne peut jamais déplacer un
élément vers son espace personnel, l’espace d’un autre propriétaire ou l’espace
commun. Les changements de partage, confirmations et déplacements sont
sérialisés avec les écritures sur l’espace concerné, y compris quand le
destinataire n’en est pas le propriétaire.

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
   renvoie les URL signées dans `parts`, avec des numéros à partir de 1.
2. Envoyer chaque tranche de `partSize` octets (la dernière peut être plus courte)
   avec la méthode `method` (`PUT`) sur son `url`, avec exactement les en-têtes
   `headers`. Les petits fichiers utilisent un PUT unique dont le type de contenu
   fait partie de la signature ; les gros utilisent un envoi multipart S3.
3. Après une interruption, `GET /uploads/{id}` renouvelle les URL des parties
   manquantes ou de taille incorrecte, par lots d’au plus 1 000 parties. Répéter
   jusqu’à recevoir une liste vide.
4. Confirmer avec `POST /uploads/{id}/complete`. L’API vérifie les parties,
   assemble l’objet puis vérifie que sa taille égale la taille déclarée. Le fichier
   n’apparaît qu’après cette confirmation.

Shelf n’impose aucune limite applicative de taille ni quota ; les limites du
fournisseur S3 restent applicables. Les URL ne dépassent jamais l’expiration
de l’envoi. La reprise et la confirmation revérifient le droit d’écriture sur
la destination ou le fichier remplacé, sous les verrous d’espace.

La migration `009_multipart_uploads` suit les partages en Écriture. Les envois
survivent à la disparition du compte, de l’espace, du dossier ou du fichier ciblé,
sans pouvoir être confirmés. Après le délai configurable, une tâche de fond
annule les envois multipart et supprime leurs objets, avec reprise au prochain
passage si S3 est indisponible. Ce délai s’applique aussi après une révocation
du droit d’écriture : les URL déjà délivrées doivent expirer avant la purge.

`parentId` désigne le dossier de destination (absent ou `null` : racine de
l’espace sélectionné, personnel par défaut). Le nom est vérifié dans la destination à la déclaration,
puis de nouveau à la confirmation par une contrainte PostgreSQL, car un autre
élément a pu le prendre entre-temps. Sans `onConflict`, un nom pris répond
`409 name_conflict`. Avec `"onConflict": "keepBoth"` (« garder les deux »), le
fichier est renommé automatiquement à la confirmation : `photo.jpg` devient
`photo (1).jpg`, puis `photo (2).jpg`… Le nom définitif figure dans la réponse
de la confirmation.

Avec `"onConflict": "replace"`, la confirmation conserve l’identifiant, le nom
et les partages du fichier existant et en remplace les octets, la taille, le type,
la date et l’auteur de l’ajout. L’ancien contenu devient un élément distinct,
sans partage, dans la corbeille du même espace. La déclaration et un transfert
incomplet ne modifient jamais le fichier existant. Si le nom est libre à la
confirmation, un nouveau fichier est créé ; un dossier du même nom reste un
conflit `409`. Les noms sont revérifiés à la confirmation, sous le même verrou
que les suppressions, restaurations et déplacements.

Pour remplacer un fichier partagé sans droit d’ajout dans son dossier, fournir
`{ "replaceFileId": "<id>", "size": 123, "contentType": "image/jpeg" }`
à `POST /uploads`, sans `name`, `parentId`, `space` ni `onConflict`. Le fichier
est ciblé par son identifiant : un renommage ou déplacement par le propriétaire
avant confirmation ne fait jamais remplacer un autre fichier ni créer un ajout.
Le destinataire doit conserver l’Écriture sur ce fichier lors de la déclaration
et de la confirmation. Un retrait de partage, une rétrogradation en Lecture ou
un passage en corbeille empêche la confirmation. Les mêmes revérifications
s’appliquent aux ajouts dans les dossiers partagés.

Le bucket doit autoriser le CORS de l’origine cliente pour les transferts depuis
un navigateur : voir [la règle CORS](../infra/storage.md#cors-du-bucket-des-fichiers).

### Corbeille et purge

La migration `007_trash` ajoute la corbeille et la file persistante de suppression
S3. `DELETE /elements/{id}` conserve un instantané du sous-arbre, sa date de
suppression, son auteur et son emplacement d’origine. Le nom redevient disponible
dans le dossier. Le sous-arbre disparaît des dossiers, du téléchargement et de
**Partagés avec moi** ; ses partages sont conservés mais inactifs. Les nouvelles
écritures et confirmations d’envoi dans ses dossiers répondent `404`.

Une entrée de corbeille contient `id`, `element`, `originalParentId`,
`originalPath` (chemin du dossier parent au moment de la suppression), `deletedAt`
et `deletedBy` (`{id, username}`, ou `null` si le compte n’existe plus).
Seul le propriétaire gère sa corbeille personnelle ; les destinataires de
partages et les administrateurs ne peuvent pas y accéder. Tout utilisateur actif
peut consulter, restaurer, purger ou vider la corbeille commune.

La restauration est possible strictement avant `deletedAt + 30 jours`. Elle
replace le sous-arbre dans le dossier d’origine s’il existe, reste actif et
appartient toujours au même espace ; sinon elle le replace à la racine de cet
espace. Un nom pris provoque le même renommage que `keepBoth`. Les éléments déjà
supprimés séparément ne font pas partie de l’instantané d’un dossier supprimé
ensuite. Restaurer ce dossier ne les restaure pas implicitement.

La purge anticipée, le vidage et l’expiration suppriment les métadonnées et
inscrivent les clés S3 à supprimer **dans la même transaction PostgreSQL**, par
des déclencheurs de suppression. Les envois en cours dont le dossier est purgé
sont conservés jusqu’à leur expiration pour annuler aussi leurs parties S3.
La confirmation d’un envoi transfère sa
clé au fichier avant de supprimer l’envoi : elle ne programme pas sa suppression.

Une tâche hébergée dans chaque serveur HTTP vérifie l’expiration toutes les
secondes puis traite jusqu’à 100 suppressions S3. Plusieurs serveurs se
répartissent la file avec `FOR UPDATE SKIP LOCKED`. Une panne S3 conserve la
clé, avec une nouvelle tentative après 30 secondes puis un délai exponentiel
plafonné à une heure. Le redémarrage reprend la file PostgreSQL. Une panne
après la suppression S3 mais avant le commit répète un `DELETE` idempotent.
La réponse `204` garantit donc la purge des métadonnées et l’enregistrement du
travail S3 ; l’effacement physique suit en tâche de fond, sans dépendre de la
disponibilité immédiate du fournisseur. Les journaux `object_deletion_retry` et
`storage_cleanup_database_retry` signalent les reprises sans exposer de secret.

Une ancienne URL d’envoi peut recréer un objet déjà purgé, même si l’envoi
commencé avant son expiration se termine après. La tâche compare donc aussi
les objets du préfixe `files/` aux fichiers (corbeille comprise) et aux envois
en cours, dans un même instantané PostgreSQL. Les clés sans référence rejoignent
la même file persistante. Le balayage traite au plus une page de 1 000 clés par
seconde, recommence une minute après la fin et repart du début au redémarrage.
Une panne de liste produit `object_reconciliation_retry`, sans empêcher le
traitement de la file existante. Le préfixe `files/` est réservé à Shelf : toute
écriture doit réserver sa clé en base avant d’envoyer les octets ; la confirmation
transfère cette référence au fichier dans une transaction unique.

Le bucket doit rester sans versionnement, comme indiqué dans le guide
d’exploitation. Les miniatures seront intégrées au même mécanisme par #13.

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
| 400 | `invalid_conflict_policy` | `onConflict` autre que `keepBoth` ou `replace` |
| 400 | `invalid_upload_destination` | `replaceFileId` combiné avec `name`, `parentId`, `space` ou `onConflict` |
| 400 | `invalid_share_level` | Niveau autre que `read` ou `write` (y compris absent) |
| 400 | `invalid_recipient` | Destinataire inexistant, désactivé ou identique au propriétaire |
| 400 | `invalid_space` | `space` autre que `personal` ou `common` |
| 400 | `invalid_sort`, `invalid_order` | `sort` ou `order` inconnu |
| 401 | `authentication_required` | Session absente, expirée ou compte désactivé |
| 403 | `write_forbidden` | Écriture en Lecture seule, modification d’une racine partagée ou déplacement interdit hors de l’espace du propriétaire |
| 403 | `sharing_forbidden` | Gestion des partages par un destinataire ou sur un élément commun |
| 403 | `common_to_personal_forbidden` | Déplacement d’un élément commun vers un espace personnel |
| 404 | `upload_not_found` | Envoi inconnu, expiré, sans destination, déjà confirmé ou déclaré par un autre utilisateur |
| 404 | `element_not_found` | Élément inexistant ou invisible pour l’utilisateur (ou dossier passé au téléchargement) |
| 404 | `folder_not_found` | Dossier parent, de destination ou listé inexistant, invisible, qui est un fichier ou ne correspond pas à `space` |
| 404 | `trash_not_found` | Entrée de corbeille inexistante, inaccessible ou expirée lors d’une restauration |
| 409 | `upload_incomplete` | Objet absent du bucket à la confirmation ; envoyer puis confirmer de nouveau |
| 409 | `size_mismatch` | Taille de l’objet différente de la déclaration ; renvoyer le contenu puis confirmer de nouveau |
| 409 | `name_conflict` | Un élément du même nom, sans tenir compte de la casse, existe déjà dans le dossier |
| 409 | `folder_cycle` | Déplacement d’un dossier dans lui-même ou un de ses descendants |
| 409 | `source_changed` | Taille de la source incohérente ou contenu modifié pendant la copie ; réessayer après stabilisation |
| 503 | `database_unavailable`, `storage_unavailable` | PostgreSQL ou S3 indisponible |

Un élément d’un autre espace personnel sans partage couvrant cet élément répond
`404`, y compris à un administrateur : le rôle administrateur ne donne aucun
accès au contenu.

## Limites actuelles

Les miniatures restent livrées par les autres sous-issues de #3.
La file de suppression reçoit aussi les objets effacés par cascade lors de la
suppression d’un compte ; la validation complète du cycle de vie des comptes et
sa documentation d’authentification relèvent de #15.
