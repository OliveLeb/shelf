# Shelf

Stockage privé de photos, vidéos et fichiers pour un petit cercle de personnes invitées, à la manière de Google Drive ou Nextcloud.

## Langage

### Personnes

**Compte** :
Identité de connexion d'une personne invitée sur l'instance.
_À éviter_ : profil, login

**Utilisateur** :
Personne disposant d'un compte actif.
_À éviter_ : membre, client

**Administrateur** :
Utilisateur chargé de gérer les comptes et les invitations ; ce rôle ne donne aucun accès au contenu privé des autres utilisateurs.
_À éviter_ : superutilisateur, root

### Contenu

**Fichier** :
Tout contenu envoyé sur Shelf, photos et vidéos comprises.
_À éviter_ : document, média

**Dossier** :
Conteneur de fichiers et d'autres dossiers.
_À éviter_ : répertoire, album

**Élément** :
Un fichier ou un dossier.
_À éviter_ : item, objet, nœud

**Miniature** :
Petite image d'aperçu d'une photo ou d'une vidéo.
_À éviter_ : vignette, thumbnail

### Espaces et accès

**Espace personnel** :
Espace privé propre à chaque utilisateur, invisible des autres sauf partage.
_À éviter_ : espace privé, mon drive

**Espace commun** :
Espace unique de l'instance, visible et modifiable par tous les utilisateurs ; ses éléments n'ont pas de propriétaire.
_À éviter_ : espace partagé, espace public

**Propriétaire** :
Utilisateur dont l'espace personnel contient l'élément, quel que soit l'auteur de son ajout.
_À éviter_ : auteur, créateur

**Partage** :
Droit accordé par le propriétaire à un autre utilisateur sur un élément de son espace personnel ; pour un dossier, il couvre tout son contenu.
_À éviter_ : permission, ACL, lien de partage

**Partagés avec moi** :
Ensemble des éléments qu'on a partagés avec un utilisateur, présentés à part de son espace personnel.
_À éviter_ : reçus, boîte de partage

**Lecture** :
Niveau de partage permettant de voir et de télécharger.

**Écriture** :
Niveau de partage permettant en plus d'ajouter, renommer, déplacer et supprimer.

**Corbeille** :
Lieu où un élément supprimé reste restaurable pendant 30 jours avant sa purge définitive.
_À éviter_ : poubelle, archive
