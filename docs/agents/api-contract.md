# Contrat API : OpenAPI et collection Bruno

Le contrat HTTP de l'API est versionné dans `contracts/` :

- `contracts/openapi.json` : document OpenAPI, généré par le build de `Shelf.Api`.
- `contracts/bruno/` : collection [Bruno](https://www.usebruno.com/) générée depuis ce document
  (importable aussi dans Postman via `contracts/openapi.json`).

Ces fichiers sont générés : ne pas les modifier à la main.

## Règle

Toute modification qui ajoute, modifie ou supprime une route HTTP du contrat client
(chemin, verbe, paramètres, corps de requête ou de réponse, codes de succès, accès)
s'accompagne, **dans le même commit**, de la mise à jour de `contracts/openapi.json` et de
`contracts/bruno/`. Une route hors contrat client (`/health`, routes de développement)
n'est pas concernée.

Chaque route du contrat déclare dans son mapping :

- `.WithSummary("...")` : résumé court en anglais ; il nomme la requête dans la collection.
- `.WithTags("...")` (sur la route ou son groupe) : dossier de la collection.
- Ses réponses de succès : `.Produces<T>(status)` pour un corps, `.Produces(204)` sinon,
  quand le type de retour du handler ne les décrit pas déjà.
- `.AllowAnonymous()` si elle est publique ; sinon le document exige le jeton `Bearer`.

## Génération

```sh
./contracts/generate.sh
```

Le script builde `Shelf.Api` (ce qui réécrit `contracts/openapi.json`), puis recrée
`contracts/bruno/` avec le CLI Bruno (`npx`, Node.js requis). Vérifier le diff de
`contracts/` avant de committer : il doit refléter exactement les routes modifiées.
