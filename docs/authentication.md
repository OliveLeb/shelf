# Authentification et comptes

Implémente [l’issue #1](https://github.com/OliveLeb/shelf/issues/1).
Cette livraison fournit uniquement le backend ; aucun email n’est envoyé.

## Configuration et migrations

SDK .NET 10 et PostgreSQL sont requis. Voir [le lancement](../apps/backend/README.md).
Les variables d’environnement suivantes remplacent les valeurs de configuration :

| Variable | Usage |
| --- | --- |
| `ConnectionStrings__Shelf` | Connexion Npgsql, obligatoire pour les opérations de compte |
| `Authentication__ClientOrigin` | Origine HTTP(S) des futurs clients, obligatoire au démarrage HTTP ; sans chemin, query, identifiants ou fragment |
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
les en-têtes Authorization ou la configuration PostgreSQL. Les logs applicatifs
standards ne contiennent pas ces secrets. Ne pas activer la journalisation des
paramètres Npgsql ou des corps HTTP. Garder la limite de corps à 16 Kio aussi au
niveau du proxy. Les liens sont construits exclusivement depuis l’origine
configurée ; l’en-tête Host ne la remplace jamais.

La limitation est commune aux trois opérations publiques, par IP de connexion,
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

`POST /auth/login` retourne un secret opaque de 256 bits, l’UUID de session,
son expiration et le compte. Fournir ensuite `Authorization: Bearer <token>` à
chaque requête protégée. Chaque connexion crée une session indépendante.
La base conserve uniquement l’empreinte SHA-256 du secret. Il n’y a aucun cookie
d’authentification et les cookies ne sont jamais utilisés pour authentifier.
Aucune protection CSRF fondée sur les cookies n’est donc nécessaire pour ce contrat.

Le futur client web conserve le secret en mémoire et ajoute explicitement
l’en-tête ; une persistance éventuelle doit être conçue avec la protection XSS du
client. Le mobile utilise le stockage sécurisé du système. Ne pas placer ces
secrets dans les URL. Les réponses des opérations de compte utilisent `no-store`.

Chaque requête protégée réussie vérifie la session et le statut actuel du compte
en base, puis renouvelle l’expiration à trente jours. Une expiration atteinte
rend la session inutilisable ; seule une nouvelle connexion peut la remplacer.
Une révocation, désactivation ou suppression prend effet dès la requête protégée
suivante. Les sessions expirées ne figurent plus dans la liste des sessions actives.

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

## Suppression et futur stockage

Une suppression efface le compte, toutes ses sessions, ses données de récupération
et les invitations consommées qui lui sont rattachées, même après changement
d’email. Elle révoque aussi les invitations pour son email actuel. Une
désactivation conserve son compte, ses identifiants et les métadonnées associées.
Toute suppression, désactivation ou rétrogradation qui retirerait le dernier
administrateur actif est refusée, même par commande serveur ou en concurrence.

Aucun fichier utilisateur n’existe dans ce modèle. Le futur stockage devra purger
définitivement tous les fichiers du compte lors d’une suppression et les conserver
lors d’une désactivation. Intégrer alors cette purge au cycle de suppression et
la tester avec le stockage réel. Les tests actuels ne prétendent pas vérifier
une suppression physique de fichiers.
