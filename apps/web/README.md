# Application web

Vue 3, Vue Router et Nuxt UI (plugin Vite, sans Nuxt), thème et composants
partagés fournis par `@shelf/ui`. Seule une page d'attente existe pour l'instant.

Depuis la racine du dépôt :

```sh
docker compose --profile front up web   # http://localhost:3000
docker compose run --rm node pnpm --filter @shelf/web build
```

`vp run typecheck` construit l'application avant `vue-tsc` : le plugin Nuxt UI
génère pendant le build les déclarations `components.d.ts` et `auto-imports.d.ts`
(ignorées par Git) dont dépend la vérification des types.
