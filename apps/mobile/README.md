# Application mobile

Vue 3, Vue Router et Nuxt UI, empaquetés pour Android et iOS avec
[Capacitor](https://capacitorjs.com/). Thème et composants partagés avec le web
via `@shelf/ui`. Seule une page d'attente existe pour l'instant.

Depuis la racine du dépôt, l'interface se développe dans le navigateur :

```sh
docker compose --profile front up mobile   # http://localhost:3001
```

Les projets natifs `android/` et `ios/` ne sont pas encore générés. Ils
nécessitent Android Studio et Xcode sur la machine hôte, hors conteneur :

```sh
pnpm --filter @shelf/mobile exec cap add android
pnpm --filter @shelf/mobile exec cap add ios
pnpm --filter @shelf/mobile sync   # build web puis copie dans les projets natifs
```

L'identifiant d'application `app.shelf.mobile` (`capacitor.config.ts`) est
provisoire : le fixer avant la première publication sur un store.
