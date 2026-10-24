# Authentification et comptes

Implémente [l’issue #1](https://github.com/OliveLeb/shelf/issues/1) et le transport
web persistant de [l’issue #44](https://github.com/OliveLeb/shelf/issues/44).
Aucun email n’est envoyé.

## Configuration et migrations

SDK .NET 10 et PostgreSQL sont requis. Voir [le lancement](../apps/backend/README.md).
Les variables d’environnement suivantes remplacent les valeurs de configuration :

| Variable | Usage |
| --- | --- |
| `ConnectionStrings__Shelf` | Connexion Npgsql, obligatoire pour les opérations de compte |
| `Authentication__ClientOrigin` | Origine du navigateur, obligatoire au démarrage HTTP ; sans chemin, query, identifiants ou fragment ; un slash final est normalisé ; HTTPS pour le web |
| `Authentication__PublicPermitLimit` | Requêtes publiques par IP et fenêtre ; défaut 10 |
| `Authentication__PublicWindowSeconds` | Durée de fenêtre en secondes ; défaut 60 |
| `ASPNETCORE_URLS` | Adresse d’écoute HTTP standard ASP.NET Core |

`dotnet run --project apps/backend/Shelf.Api --no-launch-profile -- migrate`
applique les migrations SQL embarquées, en transaction, avec suivi dans
`schema_migrations`. Réexécuter la commande est sûr. Appliquer les migrations
avant de démarrer une nouvelle version ; elles ne s’exécutent pas au démarrage
HTTP. Les nouvelles évolutions doivent ajouter une migration numérotée sans
modifier une migration déjà déployée. Sauvegarder la base avant une mise à jour.

En production, terminer TLS sur le serveur ou un proxy de confiance. Ne pas
journaliser les corps d’authentification, les réponses de génération de liens,
les en-têtes Authorization, Cookie ou Set-Cookie, ni la configuration PostgreSQL. Les logs applicatifs
standards ne contiennent pas ces secrets. Ne pas activer la journalisation des
paramètres Npgsql ou des corps HTTP. Garder la limite de corps à 16 Kio aussi au
niveau du proxy. Les liens sont construits exclusivement depuis l’origine
configurée ; l’en-tête Host ne la remplace jamais.

La limitation est commune aux opérations publiques d’authentification, par IP de connexion,
et locale à chaque processus API. Derrière un proxy, sans configuration explicite
des proxys de confiance, son IP partage cette limite entre clients. Pour plusieurs
réplicas ou une exposition publique, appliquer également une limite partagée au
proxy d’entrée ; ne pas faire confiance aux en-têtes Forwarded arbitraires.
Les seuils doivent être strictement positifs.

## Commandes d’exploitation

Préfixe commun : `dotnet run --project apps/backend/Shelf.Api --no-launch-profile --`.
En déploiement publié, employer `dotnet Shelf.Api.dll` avec les mêmes arguments.
Les commandes nécessitent la connexion à la même base et les migrations préalables.

| Arguments | Effet et sortie |
| --- | --- |
| `migrate` | Applique les migrations ; confirmation textuelle |
| `admin create <username> <email>` | Crée le premier ou un autre administrateur actif ; retourne son UUID |
| `admin grant <account-id>` | Attribue le rôle administrateur à un compte existant |
| `admin revoke <account-id>` | Retire ce rôle, sauf au dernier administrateur actif |
| `admin reset-password <account-id>` | Change le mot de passe d’un administrateur, invalide tous ses liens et sessions ; ne réactive pas un compte désactivé |

La création et la récupération lisent le nouveau mot de passe sur le terminal
sans écho. En automatisation, fournir une seule ligne sur l’entrée standard
depuis un gestionnaire de secrets ou un descripteur protégé. Ne jamais placer un
mot de passe dans les arguments, l’historique shell, une variable versionnée ou
un exemple réel. Aucun argument mot de passe n’est accepté. Les autres commandes
confirment leur réussite sans divulguer les informations d’authentification.

Codes de sortie : `0` succès ; `2` usage ou validation ; `3` compte absent ; `4`
conflit (identifiant déjà utilisé ou dernier administrateur actif) ; `5`
configuration/base indisponible ou migration requise. Les erreurs attendues sont
émises sur stderr avec un code textuel, sans détail SQL ni secret.
Aucun endpoint HTTP n’attribue ou ne retire le rôle administrateur.

## Transport des sessions web et mobile

Les deux transports réutilisent les mêmes sessions opaques de 256 bits ; la base
ne conserve que l’empreinte SHA-256 du secret. Aucun JWT ou second registre de
sessions n’est ajouté. Le rôle administrateur ne donne aucun accès supplémentaire
aux espaces personnels des autres comptes, quel que soit le transport.

### Mobile : Bearer inchangé

`POST /auth/login` reçoit `Login` et retourne `{account, sessionId, token, expiresAt}`.
Le mobile conserve le secret dans le stockage sécurisé du système, puis fournit
`Authorization: Bearer <token>` à chaque requête protégée. Sans cookie web, il
n’a pas à fournir les en-têtes CSRF. Chaque connexion mobile reste indépendante.
Si un cookie web accompagne `/auth/login`, le serveur exige d’abord les en-têtes
web ci-dessous, puis refuse avec `400 web_login_required` : cette route ne doit
pas permettre au navigateur de récupérer le secret dans du JSON.

### Web : cookie persistant

`POST /auth/web/login` reçoit le même JSON `Login`, mais ne retourne que le compte
(`id`, `username`, `email`, `isAdmin`, `isActive`), sans enveloppe, jeton ou UUID de
session. Le secret est envoyé uniquement dans `Set-Cookie` :

```http
Set-Cookie: __Host-shelf-session=<opaque>; expires=<UTC>; path=/; secure; samesite=strict; httponly
```

Le cookie est persistant, host-only, sans `Domain` ; le préfixe `__Host-` impose
`Secure` et `Path=/`. JavaScript ne doit ni lire le secret, ni le copier dans
localStorage/sessionStorage. Les requêtes de compte **et de stockage** acceptent
ce cookie à la place du Bearer. Une connexion web réussie remplace atomiquement
la session de l’ancien cookie, même si le compte change ; les autres appareils
restent connectés. Un cookie expiré ou déjà révoqué n’empêche pas une nouvelle
connexion. Un échec de connexion ne crée pas de session et ne révoque pas l’ancienne.

La présence simultanée du cookie et d’`Authorization` est toujours rejetée avec
`401 ambiguous_credentials`, même si l’un est invalide ou le cookie vide : aucun
transport n’a priorité et aucune tentative ne retombe sur l’autre. Un cookie seul
invalide donne `401 authentication_required`. Les `401` avec cookie le suppriment
avec les mêmes attributs de sécurité et une expiration passée. Un logout web
réussi révoque uniquement sa session et supprime également le cookie.

### CSRF et déploiement HTTPS

Toute requête web non sûre (`POST`, `PUT`, `PATCH`, `DELETE`), sur les opérations
d’authentification, de compte, d’administration et de stockage, doit fournir :

```http
Origin: https://shelf.example.test
X-Shelf-CSRF: 1
```

L’origine doit être **exactement** l’origine normalisée depuis
`Authentication__ClientOrigin` (schéma, hôte et port, sans slash final) ; une origine absente, `null`, étrangère, avec
slash final ou plusieurs valeurs est refusée. L’en-tête CSRF doit être unique et
valoir exactement `1`. Sinon : `403 csrf_rejected`, avant le traitement du JSON
et toute mutation. Cette règle s’applique **toujours à `/auth/web/login`**, même
sans cookie, et aux autres opérations non sûres dès qu’un cookie web est présent.
Les lectures `GET` ne demandent pas ces en-têtes.

L’en-tête personnalisé + l’origine exacte, avec **aucun CORS permissif sur l’API**,
constituent volontairement la défense CSRF ; `SameSite=Strict` est une protection
supplémentaire, pas son remplacement. Ne pas ajouter de CORS autorisant des
origines arbitraires ou reflétant `Origin`. Le CORS S3 pour les transferts directs
est distinct et ne transporte pas le cookie de l’API.

Servir le web et son proxy `/api` sous la même origine HTTPS. Le proxy retire le
préfixe `/api` pour l’API et conserve `Origin`, `Cookie` et `Set-Cookie` **sans
réécrire Domain ou Path**. L’API interne peut rester HTTP loopback derrière un
proxy TLS de confiance : `Secure` reste toujours émis, indépendamment du schéma
de la connexion interne. Ne pas exposer ce port HTTP directement sur Internet.
Configurer l’origine publique HTTPS du navigateur (pas l’adresse interne de
l’API). En développement : `https://localhost:3000`, avec certificat de confiance
et proxy vers le port API 5050. Ne pas désactiver `Secure` pour contourner HTTPS.
N’accepter des en-têtes Forwarded que de proxys explicitement approuvés si leur
prise en charge est ajoutée ; le contrat CSRF ne déduit jamais l’origine depuis Host.

### Expiration et activité

Chaque requête protégée vérifie la session et le statut actuel du compte en base.
La fenêtre d’inactivité reste de trente jours, borne exclue. Après une activité
authentifiée réussie, le cookie est réémis avec l’expiration réelle de la session
en base ; les réponses de refus ne le renouvellent pas. Le stockage conserve ses
deux transactions : activité validée avant l’opération longue, puis revalidation
et verrouillage des droits jusqu’au commit ; un échec de stockage peut donc avoir
mis à jour l’activité en base, mais n’allonge pas le cookie du navigateur.
Une expiration atteinte rend la session inutilisable ; seule une nouvelle
connexion peut la remplacer. Une révocation, désactivation ou suppression prend
effet dès la requête protégée suivante. Les sessions expirées ne figurent plus
dans la liste des sessions actives. Les réponses de compte et stockage utilisent
`Cache-Control: no-store`. Ne jamais placer les secrets dans les URL.

## Contrat HTTP

JSON UTF-8 ; dates ISO 8601 UTC ; identifiants UUID. Les champs supplémentaires
ne donnent aucun privilège : inscription ignore notamment email et rôle fournis.
Les corps indiqués sont des objets JSON. Aucun endpoint d’inscription publique
sans invitation, de réactivation ou de modification d’un autre compte n’existe.

Un compte retourné contient uniquement `id`, `username`, `email`, `isAdmin`,
`isActive`. Le username est non vide, limité à 100 caractères sans `@` ni caractère
de contrôle, l’email à 320 caractères et doit être une adresse valide. Les
identifiants sont nettoyés des espaces extérieurs ; recherche et unicité sont
insensibles à la casse, y compris sur les comptes désactivés. Le mot de passe
compte entre 12 et 1024 caractères. L’ID du compte ne change jamais.

| Méthode et chemin | Accès | Corps | Réussite |
| --- | --- | --- | --- |
| `GET /health` | Public | — | 200, santé du processus |
| `POST /auth/register` | Public limité | `token`, `username`, `password` | 201, compte ordinaire ; email imposé par invitation |
| `POST /auth/login` | Public limité | `identifier` (username ou email), `password`, `deviceName` facultatif (200 caractères max) | 200, `{account, sessionId, token, expiresAt}` |
| `POST /auth/web/login` | Public limité + en-têtes web | même `Login` | 200, compte seulement + cookie persistant |
| `POST /auth/reset-password` | Public limité | `token`, `password` | 204 ; toutes les sessions révoquées |
| `POST /auth/logout` | Utilisateur | — | 204 ; session courante révoquée |
| `GET /account` | Utilisateur | — | 200, compte courant |
| `PATCH /account/username` | Utilisateur | `username` | 200, compte |
| `PATCH /account/email` | Utilisateur | `email`, `currentPassword` | 200, compte ; autres sessions révoquées |
| `PATCH /account/password` | Utilisateur | `password`, `currentPassword` | 204 ; autres sessions et liens de récupération révoqués |
| `DELETE /account` | Utilisateur | `currentPassword` | 204 ; suppression définitive |
| `GET /account/sessions` | Utilisateur | — | 200, tableau de sessions actives du compte |
| `DELETE /account/sessions/{id}` | Propriétaire | — | 204 ; session ciblée révoquée |
| `DELETE /account/sessions` | Utilisateur | — | 204 ; toutes ses sessions révoquées, courante comprise |
| `GET /admin/accounts` | Admin actif | — | 200, tableau de comptes |
| `POST /admin/accounts/{id}/disable` | Admin actif | — | 204 ; compte conservé, sessions et récupération révoquées |
| `DELETE /admin/accounts/{id}` | Admin actif | — | 204 ; suppression sans mot de passe du destinataire |
| `POST /admin/accounts/{id}/password-reset` | Admin actif | — | 200, `{link, expiresAt}` |
| `POST /admin/invitations` | Admin actif | `email` | 201, `{id, email, link, expiresAt}` |
| `GET /admin/invitations` | Admin actif | — | 200, tableau de métadonnées |
| `DELETE /admin/invitations/{id}` | Admin actif | — | 204 ; révocation d’une invitation non consommée |

Une session listée contient `id`, `deviceName`, `createdAt`, `lastSeenAt`,
`expiresAt`, `isCurrent`. Une invitation listée contient `id`, `email`,
`createdAt`, `expiresAt`, `revoked`, `consumed`. Aucune liste ne contient de
secret, d’empreinte de mot de passe ou de lien secret récupérable.

Les réponses d’erreur métier sont `{ "error": "code" }` : `400` validation,
jeton inutilisable ou mot de passe actuel incorrect ; `401` authentification
absente/invalide ; `403` privilèges insuffisants ; `404` ressource inexistante ou
session appartenant à autrui ; `409` unicité, compte désactivé pour génération de
récupération ou protection du dernier administrateur ; `429` limitation ; `503`
base indisponible. ASP.NET Core renvoie également `400` sur un JSON invalide et
`413` si le corps dépasse la limite. Toutes les erreurs de connexion utilisent
`401 invalid_credentials`, sans indiquer si l’identifiant existe.

## Invitations, récupération et atomicité

Une invitation expire après sept jours ; une récupération après une heure,
borne exclue. Les liens sont rendus uniquement lors de leur création :
`<origine>/accept-invitation#token=<secret>` et
`<origine>/reset-password#token=<secret>`. Le futur client lit le fragment et
transmet le secret dans le corps POST ; le fragment n’est pas envoyé au serveur
par le navigateur. Ces pages clientes restent à construire.

Une nouvelle invitation révoque les précédentes pour le même email. Une nouvelle
récupération remplace la précédente pour ce compte. Une validation rejetée ou un
conflit ne consomme pas le secret et ne modifie pas partiellement le compte ni
les autres sessions. Une réinitialisation réussie consomme le lien, invalide tous
les accès existants et exige une nouvelle connexion. Aucun parcours ne réactive
un compte désactivé.

Les mots de passe utilisent `PasswordHasher` d’ASP.NET Core Identity. Les jetons
sont générés par `RandomNumberGenerator`, et seule leur empreinte est persistée.
Les contraintes uniques PostgreSQL portent sur `upper(username)` et `upper(email)`.
Les opérations de compte, authentification et commandes partagent un verrou
consultatif transactionnel PostgreSQL ; il sérialise autorisation et mutation,
y compris entre processus. Ce choix simple convient à une instance personnelle,
mais limite le débit global des opérations de compte. Les autres opérations
futures (stockage, santé) ne doivent pas utiliser ce verrou. Une évolution du
verrouillage devra conserver les tests concurrents de l’issue.

## Suppression et stockage

Une suppression efface le compte, toutes ses sessions, ses données de récupération
et les invitations consommées qui lui sont rattachées, même après changement
d’email. Elle révoque aussi les invitations pour son email actuel. Une
désactivation conserve son compte, ses identifiants et les métadonnées associées.
Toute suppression, désactivation ou rétrogradation qui retirerait le dernier
administrateur actif est refusée, même par commande serveur ou en concurrence.

Une suppression volontaire ou administrative efface définitivement l’espace
personnel, sa corbeille et tous ses éléments, quel que soit l’auteur de leur
ajout. Les partages accordés et reçus disparaissent. Les clés des objets S3 à
effacer sont enregistrées dans la même transaction PostgreSQL ; la tâche de
purge les supprime avec reprise après panne S3 ou redémarrage. La réponse `204`
confirme la suppression des métadonnées et la programmation durable du travail,
pas la fin de l’effacement physique. Voir [la purge du stockage](storage.md#corbeille-et-purge).

Les éléments ajoutés dans l’espace commun, y compris ceux transférés depuis
l’espace personnel, sont conservés ; leur auteur devient anonyme (`addedBy: null`).
Les contributions du compte dans l’espace personnel d’un autre propriétaire
restent aussi dans cet espace, avec un auteur anonyme.

Une désactivation conserve l’espace personnel, sa corbeille, ses objets et ses
partages. Les destinataires gardent leurs droits de Lecture ou d’Écriture ;
seul le compte désactivé perd son propre accès. Les règles ordinaires d’expiration
de la corbeille et des envois continuent de s’appliquer.

L’authentification du stockage prend un verrou partagé sur la ligne du compte
avant de renouveler sa session, sans prendre le verrou consultatif global des
opérations de compte. Les opérations d’authentification prennent d’abord un
verrou exclusif sur leur compte pour éviter une conversion de verrou après
avoir verrouillé la session. Une suppression ou désactivation qui détient déjà
le verrou du compte fait attendre puis refuser une confirmation d’envoi (`401`).
Si l’opération de stockage est autorisée la première, elle termine avant la
suppression ou désactivation ; une suppression purge alors aussi ses ajouts
personnels. Aucun envoi ne peut être publié au nom du compte après son effacement.

Les envois non confirmés survivent en base pour le nettoyage : à leur expiration
configurée, leurs objets sont supprimés et leurs envois multipart annulés, avec
reprise en cas de panne. Ce délai empêche les URL signées déjà délivrées de
recréer immédiatement des objets après leur purge. Le balayage des orphelins
reprend aussi les transferts tardifs terminés après expiration. Ces règles sont
vérifiées par les routes existantes de suppression et désactivation, avec
PostgreSQL et un bucket MinIO réels, panne S3 et redémarrage compris.
