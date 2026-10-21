# Transfert direct entre client et S3 par URL signées

L'API hébergée sur le VPS ne fait pas transiter le contenu des fichiers : elle vérifie les droits puis délivre des URL signées de courte durée, et le client envoie ou télécharge directement auprès du fournisseur S3 (envoi en plusieurs parties reprenable pour les gros fichiers). Les vidéos de plusieurs Go satureraient sinon la bande passante et le disque d'un petit VPS ; en contrepartie, le bucket doit autoriser le CORS des clients et les fonctions qui exigeraient de relayer les octets par le serveur, comme le téléchargement d'un dossier en ZIP, sont écartées de la première version.

## Considered Options

- Relayer envois et téléchargements par l'API : plus simple et indépendant des capacités du fournisseur, rejeté pour le coût en bande passante du VPS.
