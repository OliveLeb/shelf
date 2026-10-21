# Pas de chiffrement de bout en bout

Les fichiers sont stockés en clair du point de vue de Shelf dans un bucket S3 privé, chiffré au repos par le fournisseur, et ne sont accessibles qu'au travers d'URL signées de courte durée. Un chiffrement côté client protégerait contre le fournisseur S3 ou un VPS compromis, mais empêcherait le serveur de générer les miniatures, compliquerait la lecture en streaming et le client mobile, et ferait de la perte d'une clé une perte définitive des photos de famille ; ce compromis est jugé inadapté à une instance personnelle à deux utilisateurs.
