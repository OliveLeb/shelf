# Authentification et gestion des comptes sur invitation

## Problem Statement

Shelf est une application personnelle de stockage de fichiers, photos et vidéos, comparable dans son usage à Google Drive ou Nextcloud. Son propriétaire doit pouvoir réserver l’accès aux personnes qu’il choisit, depuis des clients web et mobile, sans inscription publique ni service d’envoi d’emails obligatoire.

Le backend ASP.NET Core .NET 10 ne fournit actuellement qu’un endpoint de santé. Il ne possède ni authentification, ni persistance, ni stockage de fichiers. Les clients web et mobile restent à développer. Il faut établir les règles d’accès et le cycle de vie des comptes avant de construire les fonctions de stockage.

## Solution

Ajouter une authentification backend commune au web et au mobile, persistée dans PostgreSQL. Un opérateur crée le premier administrateur par commande serveur. Les administrateurs génèrent des invitations associées à un email et transmettent eux-mêmes leurs liens aux destinataires. Chaque destinataire choisit un username et un mot de passe ; aucune inscription sans invitation valide n’est possible.

Chaque utilisateur peut gérer ses informations, ses sessions et supprimer son compte. Les administrateurs peuvent inviter, révoquer les invitations, générer des liens de réinitialisation, désactiver et supprimer les comptes. Plusieurs administrateurs sont possibles ; leur rôle est attribué ou retiré uniquement par commande serveur. Le dernier administrateur actif est protégé contre toute opération qui supprimerait le dernier accès administratif.

## User Stories

1. As a server operator, I want to create the first administrator through a server command, so that I can bootstrap a private instance without public registration.
2. As a server operator, I want to create additional administrators through a server command, so that administration does not depend on one account.
3. As a server operator, I want to grant or remove administrator privileges through server commands, so that role management remains under server control.
4. As a server operator, I want removal of the last active administrator role to be rejected, so that the instance remains administrable.
5. As a server operator, I want to reset an administrator password through a server command, so that I can recover access without email delivery.
6. As an administrator, I want to create an invitation for a specific email address, so that I decide who can register.
7. As an administrator, I want to obtain an invitation link to transmit myself, so that I can invite someone without configuring an email service.
8. As an administrator, I want invitations to expire after seven days, so that unused links do not grant indefinite access.
9. As an administrator, I want to revoke an unused invitation, so that I can withdraw access before registration.
10. As an administrator, I want to create a new invitation after an earlier invitation expires, so that I can invite the recipient again.
11. As an invitee, I want to register through a valid invitation, so that I can access the private instance.
12. As an invitee, I want my email to be fixed by the invitation, so that the resulting account matches the intended recipient address.
13. As an invitee, I want to choose my username and password, so that I control my login credentials.
14. As an invitee, I want to use my account without a separate email verification step, so that registration remains simple.
15. As an instance owner, I want each invitation to be usable only once, so that forwarding or replaying a consumed link cannot create more accounts.
16. As an instance owner, I want public registration and invalid, expired or revoked invitations to be rejected, so that access remains invitation-only.
17. As an instance owner, I want invitation-based registration to create ordinary users, so that an invitation never grants administrator privileges.
18. As a user, I want my email and username to be unique, so that my account is unambiguous.
19. As a user, I want to sign in with either my username or email and my password, so that I can use whichever identifier I remember.
20. As a web user, I want to authenticate from a browser, so that I can use the web client.
21. As a mobile user, I want to authenticate from a mobile application, so that I can use the same account on my phone.
22. As a user, I want several devices to remain connected simultaneously, so that I can use the web and mobile clients together.
23. As a user, I want a session to remain valid for thirty days without activity and to renew when used, so that I do not need to sign in repeatedly during normal use.
24. As a user, I want to see my active sessions and identify the current one, so that I can decide which devices should retain access.
25. As a user, I want to disconnect one of my devices, so that I can revoke access to a device I no longer use.
26. As a user, I want to disconnect all my devices, so that I can invalidate all existing sessions at once.
27. As a user, I want to change my username while signed in, so that I can update my account identity.
28. As a user, I want to change my email after supplying my current password, so that I can update my login address securely.
29. As a user, I want a new email to become usable immediately without verification, so that changing it does not depend on email delivery.
30. As a user, I want to change my password after supplying my current password, so that I can replace my credentials securely.
31. As a user, I want email and password changes to disconnect my other devices, so that old sessions cannot continue accessing my account.
32. As a user, I want duplicate email or username changes to be rejected without changing my account, so that uniqueness is preserved.
33. As an administrator, I want to generate a password reset link for a user and transmit it myself, so that I can help someone who has forgotten their password.
34. As a user, I want to choose a new password through a reset link that expires after one hour, so that I can regain access.
35. As an instance owner, I want reset links to be single-use, so that they cannot be replayed after recovery.
36. As a user, I want password reset to disconnect every session, so that old sessions lose access after recovery.
37. As a user, I want to delete my own account after supplying my password, so that I can leave the instance intentionally.
38. As a user, I want account deletion to disconnect all my devices immediately, so that a deleted account can no longer access the instance.
39. As a user, I want deletion of my account to delete my files permanently, so that my personal data is not retained with a removed account.
40. As an administrator, I want to identify the user accounts I administer, so that I can act on the intended account.
41. As an administrator, I want to disable a user and immediately revoke their sessions, so that I can stop access while retaining their account and files.
42. As an administrator, I want to delete a user account and its files, so that I can remove access and associated data permanently.
43. As an instance owner, I want deletion or disabling of the last active administrator to be rejected, so that administrative access remains available.
44. As an ordinary user, I want administrative operations to be inaccessible to my account, so that all users are subject to the same access rules.
45. As a user, I want other users to be unable to edit my account or revoke my sessions, so that my account management remains private.
46. As an instance owner, I want repeated login attempts to be limited, so that password guessing is constrained.
47. As an instance owner, I want account and invitation rules to hold during concurrent requests, so that simultaneous operations cannot bypass restrictions.

## Implementation Decisions

### Architecture et persistance

- Étendre le backend ASP.NET Core .NET 10 existant ; conserver le fonctionnement de l’endpoint de santé.
- Utiliser PostgreSQL pour les comptes, rôles, invitations, sessions et données de réinitialisation. Prévoir des migrations reproductibles et documenter la configuration et l’initialisation.
- Organiser les responsabilités autour de l’authentification et des sessions, du compte courant, des opérations administratives et des commandes d’exploitation. Les opérations HTTP et les commandes doivent appliquer les mêmes règles métier.
- Utiliser des identifiants de compte stables, indépendants du username et de l’email modifiables.
- Ne pas exposer les mots de passe, leurs empreintes, les secrets de session ou les jetons de récupération dans les réponses de consultation, les listes ou les logs.
- Utiliser les primitives de hachage de mots de passe maintenues par la plateforme ; ne pas concevoir de cryptographie personnalisée.

### Comptes et identifiants

- Un compte possède un username, un email, une empreinte de mot de passe, un statut actif/désactivé et un rôle utilisateur/administrateur.
- Username et email sont chacun uniques globalement, y compris pour les comptes désactivés. Des contraintes PostgreSQL doivent garantir cette unicité malgré les accès concurrents.
- La connexion accepte soit le username soit l’email, avec le mot de passe.
- Les valeurs par défaut proposées dans le récapitulatif de l’entretien sont : unicité et recherche insensibles à la casse, interdiction de `@` dans les usernames et mot de passe d’au moins 12 caractères. Les appliquer de façon cohérente à l’inscription, aux modifications, aux réinitialisations et aux commandes serveur.
- Ne pas imposer de vérification d’email à l’inscription ni après un changement d’email. L’adresse imposée par l’invitation n’est pas une preuve technique de possession de la boîte mail : toute personne détenant le lien peut l’utiliser pour cette adresse.

### Invitations

- Seul un administrateur actif peut générer, consulter les métadonnées ou révoquer des invitations.
- Une invitation est associée à un email précis, expire sept jours après sa création et ne peut être consommée qu’une fois. Le client ne peut pas remplacer l’adresse au moment de l’inscription.
- Le backend retourne un lien à l’administrateur lors de la génération ; il n’envoie aucun email. L’origine des liens destinés aux clients doit être configurable et ne pas dépendre d’un en-tête de requête non fiable.
- Générer des jetons secrets aléatoires, non devinables ; ne conserver qu’une empreinte permettant de les vérifier. Ne pas fournir de mécanisme de relecture du secret après génération.
- Valider l’invitation et créer le compte dans une opération atomique. Deux requêtes concurrentes sur un même lien ne doivent jamais créer deux comptes. Une tentative de création rejetée, notamment pour doublon, ne doit pas consommer l’invitation.
- Refuser les invitations destinées à un email déjà associé à un compte. Pour un même email, n’autoriser qu’une invitation utilisable à la fois ; la création d’un lien de remplacement invalide le précédent. Cette dernière règle est un choix d’implémentation pour rendre la réémission prévisible.
- L’inscription crée toujours un utilisateur ordinaire, même si une requête fournit un rôle administratif.

### Sessions web et mobile

- Le contrat d’authentification doit prendre en charge les deux types de clients dès cette livraison ; leur interface utilisateur n’est pas à construire ici.
- Autoriser plusieurs sessions simultanées pour un même compte. Chaque session a une identité propre et une expiration glissante de trente jours d’inactivité. Une session expirée ne peut pas être réactivée par son utilisation.
- Une utilisation authentifiée valide renouvelle la durée d’inactivité. La liste des sessions fournit les informations nécessaires pour identifier une session, son activité et la session courante, sans exposer de secret.
- Fournir la déconnexion de la session courante, la révocation d’une session choisie et la révocation de toutes les sessions, y compris la session courante.
- La révocation, la désactivation et la suppression doivent interdire l’accès dès la requête protégée suivante, sans attendre l’expiration d’un jeton autonome. Le mécanisme choisi doit donc vérifier l’état de révocation et du compte côté serveur.
- Le choix précis des cookies, jetons et de leur transport reste à l’implémentation. Il doit permettre cette révocation immédiate, protéger les secrets et inclure une protection CSRF si l’authentification web repose sur des cookies automatiquement transmis.
- Vérifier la propriété des sessions lors de leur consultation et révocation ; un utilisateur ne peut pas agir sur les sessions d’un autre.

### Compte courant

- Fournir la consultation du compte courant et les modifications de username, d’email et de mot de passe.
- Le changement de username nécessite une session active. Le changement d’email ou de mot de passe exige aussi le mot de passe actuel, puis révoque les autres sessions ; la session courante reste utilisable de manière sûre.
- Un changement d’email réussi rend immédiatement la nouvelle adresse utilisable à la connexion et retire l’ancienne comme identifiant email. Le username reste utilisable.
- Appliquer les contraintes de validation et d’unicité à toutes les modifications. Une opération rejetée ne modifie pas partiellement le compte et ne révoque pas ses sessions.
- La suppression volontaire exige le mot de passe actuel et supprime le compte et ses données associées ; toutes ses sessions et possibilités de récupération deviennent invalides.

### Récupération de compte

- Un administrateur actif peut générer un lien de réinitialisation pour un compte existant ; il le transmet lui-même.
- Le lien est secret, utilisable une seule fois et valable une heure. La vérification doit utiliser une empreinte persistée, comme pour les invitations.
- La génération d’un lien de remplacement invalide les liens précédents pour ce compte. Un changement ou une réinitialisation de mot de passe réussi invalide également les liens de récupération antérieurs.
- La consommation du lien et le changement du mot de passe sont atomiques. Après réinitialisation, toutes les sessions sont révoquées et l’utilisateur doit se reconnecter.
- Un compte désactivé ne doit pas retrouver l’accès via un ancien lien d’invitation, de session ou de récupération ; une réinitialisation ne réactive jamais un compte.
- Une commande serveur permet de réinitialiser le mot de passe d’un administrateur, sans session valide et sans service email. Elle applique la politique de mot de passe et la révocation de toutes les sessions et liens de récupération.

### Administration et commandes serveur

- Les commandes serveur couvrent la création d’administrateurs, l’attribution et le retrait du rôle administrateur et la récupération du mot de passe administrateur.
- Les invitations créent uniquement des utilisateurs ordinaires. Aucun endpoint HTTP ne doit permettre d’attribuer le rôle administrateur dans cette première version.
- Fournir aux administrateurs une liste des comptes avec leurs identifiants, username, email, rôle et statut, ainsi que les opérations de désactivation et de suppression.
- La désactivation conserve le compte et ses fichiers, bloque les nouvelles connexions et révoque immédiatement les sessions existantes. Le mécanisme de récupération ne contourne pas ce blocage.
- La suppression administrative efface le compte et ses données associées. Elle ne requiert pas le mot de passe de l’utilisateur supprimé ; elle exige une session administrateur autorisée.
- Refuser toute suppression, désactivation ou rétrogradation qui laisserait l’instance sans administrateur actif. Appliquer cette garantie aux commandes comme à l’API et aux opérations sur soi-même.
- Garantir cette protection en présence de requêtes concurrentes : deux administrateurs ne doivent pas pouvoir supprimer simultanément les deux derniers accès administratifs.
- Ne jamais accepter un mot de passe secret dans des logs ou des exemples versionnés. Documenter une saisie sûre pour les commandes serveur, leurs résultats et leurs codes de sortie.

### Contrats et exploitation

- Documenter les opérations HTTP et leurs entrées/sorties : connexion, inscription sur invitation, compte courant, modifications, suppression, sessions, invitations, administration et récupération. La forme exacte des routes n’a pas été arrêtée pendant l’entretien.
- Les réponses distinguent de manière cohérente validation, absence d’authentification, absence d’autorisation, conflit et limitation de débit. Les échecs de connexion ne doivent pas révéler si le username ou l’email existe.
- Limiter les tentatives de connexion ; protéger aussi les opérations publiques de consommation d’invitation et de réinitialisation contre les essais répétés. Les seuils précis restent configurables et relèvent de l’implémentation.
- Utiliser une configuration externe pour PostgreSQL et les origines de liens ; ne versionner aucun secret. Documenter le lancement, les migrations, le bootstrap admin, les commandes et les parcours web/mobile.

### Suppression des fichiers : limite de cette livraison

- La règle produit validée est la suppression définitive des fichiers lors de la suppression d’un compte ; une désactivation les conserve.
- Aucun stockage de fichiers ni modèle de fichiers n’existe actuellement. Cette spécification ne demande pas de construire le moteur de stockage pour livrer l’authentification.
- Dans cette livraison, supprimer les données de compte effectivement présentes et invalider tous les accès associés. Documenter explicitement la règle que le futur stockage devra respecter et ne pas prétendre avoir vérifié la suppression physique de fichiers inexistants.
- Lors de l’ajout du stockage, intégrer la purge définitive des fichiers au cycle de suppression de compte et la vérifier par des tests incluant ce stockage.

## Testing Decisions

- Points d’entrée validés : tests d’intégration HTTP du backend avec une vraie base PostgreSQL isolée ; tests complémentaires des commandes serveur en les exécutant comme un opérateur.
- Tester les comportements externes et les invariants observables, pas les méthodes privées, l’organisation interne des classes ou une copie des calculs de l’implémentation. Privilégier ces deux points d’entrée à une multiplication d’interfaces créées uniquement pour les tests.
- Aucun précédent de tests n’existe dans le dépôt. Mettre en place le minimum d’infrastructure reproductible pour exécuter ces parcours avec migrations, données isolées et nettoyage maîtrisé.
- Couvrir l’inscription valide et tous les refus : absence d’invitation, lien falsifié, expiré, révoqué ou consommé, email imposé, rôle injecté et collisions d’identifiants. Vérifier qu’une inscription rejetée ne consomme pas le lien.
- Couvrir la connexion avec username et email, les erreurs de mot de passe, les comptes désactivés, l’autorisation des routes et la limitation des tentatives.
- Couvrir plusieurs appareils, renouvellement après activité, expiration après trente jours sans activité, interdiction de renouveler une session expirée, révocation ciblée, globale et protection des sessions d’autrui.
- Couvrir le changement de username, email et mot de passe, les conflits d’unicité, le mot de passe actuel erroné, le nouvel email immédiatement utilisable et la révocation des autres sessions uniquement après succès.
- Couvrir la génération administrative, l’expiration après une heure, l’usage unique et le remplacement des liens de récupération ; vérifier la révocation de toutes les sessions après réinitialisation et l’impossibilité de réactiver un compte désactivé.
- Couvrir les commandes de bootstrap, création d’un autre admin, attribution/retrait de rôle et récupération admin, leurs erreurs et leurs effets visibles par l’API.
- Couvrir la suppression volontaire avec mot de passe, la désactivation et la suppression administratives, la perte immédiate d’accès et la conservation ou suppression des données présentes selon l’opération.
- Couvrir la protection du dernier administrateur actif pour chaque entrée concernée, y compris les commandes, les opérations sur soi-même et les cas avec des administrateurs désactivés.
- Exécuter des scénarios concurrents contre PostgreSQL : même invitation, mêmes identifiants, même lien de récupération et opérations visant les derniers administrateurs. Une suite qui ne teste que des requêtes successives ne valide pas ces invariants.
- Vérifier les limites de temps sans attentes réelles de plusieurs jours, au moyen d’un contrôle de l’horloge de l’application hôte tout en gardant les assertions au niveau HTTP et commandes.
- Vérifier que les réponses de consultation et les logs ne révèlent pas les secrets. Si des cookies sont choisis pour le web, couvrir aussi le rejet des requêtes sensibles dépourvues de la protection CSRF attendue.
- La suppression physique des fichiers sera testée avec le futur stockage ; elle ne peut pas être déclarée couverte par les tests d’authentification actuels.

## Out of Scope

- Construction des interfaces web et mobile ; cette livraison fournit leur backend commun.
- Inscription publique, auto-attribution du rôle administrateur et gestion des rôles par HTTP.
- Envoi d’emails, SMTP, vérification d’email et récupération automatique par email.
- MFA, SSO, OAuth social et authentification sans mot de passe.
- Moteur de stockage, upload/download, partage de fichiers, quotas et synchronisation.
- Corbeille, délai de grâce et restauration après suppression : la règle demandée est une suppression définitive.
- Endpoint de réactivation des comptes désactivés : aucun parcours de réactivation n’a été demandé pendant l’entretien.
- Expiration absolue supplémentaire des sessions et options de durée par utilisateur : seule la durée glissante validée de trente jours est prévue.

## Further Notes

- Les choix issus de l’entretien sont synthétisés ici ; les routes précises, bibliothèques, schéma détaillé, transport des sessions et seuils de limitation ne sont pas des décisions déjà prises par l’utilisateur.
- Les règles complémentaires explicitement qualifiées de choix d’implémentation rendent les parcours reproductibles sans ouvrir un nouvel entretien produit.
- Le périmètre de tests HTTP/PostgreSQL et commandes serveur a été validé par l’utilisateur avant publication.
- La publication de cette spécification ne constitue pas l’implémentation de l’authentification. Aucun code d’authentification n’est livré avec cette issue.
