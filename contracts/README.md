# Contrats API

- `openapi.json` : contrat OpenAPI de l'API, généré à chaque build de `Shelf.Api`.
- `bruno/` : collection Bruno générée depuis `openapi.json`, avec l'environnement `Local`
  (`baseUrl` = http://localhost:5050). Pour Postman, importer directement `openapi.json`.

Ne pas modifier ces fichiers à la main ; les régénérer avec :

```sh
./contracts/generate.sh
```

Les routes protégées utilisent la variable `{{token}}` : l'ajouter à l'environnement
(de préférence en secret) avec le `token` renvoyé par `POST /auth/login`.

Règles de mise à jour : voir [docs/agents/api-contract.md](../docs/agents/api-contract.md).
