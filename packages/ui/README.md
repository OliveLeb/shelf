# @shelf/ui

Design system partagé par `apps/web` et `apps/mobile`, construit sur Nuxt UI.
Les applications consomment directement les sources du paquet :

- `@shelf/ui/theme` : options du plugin Vite `ui()` de Nuxt UI (couleurs,
  variantes par défaut). Chaque application les passe à `ui(shelfUi)`.
- `@shelf/ui/style.css` : Tailwind CSS, Nuxt UI et jetons de design ; importé par
  la feuille de style de chaque application.
- `@shelf/ui` : composants Vue partagés (`src/components/`). Ils peuvent utiliser
  les composants Nuxt UI (`UButton`…) sans import, grâce à `scanPackages`.

Tests : `vp test` (Vitest, happy-dom, `@vue/test-utils`). `vp run typecheck`
lance d'abord un build en mode bibliothèque, non publié, pour que le plugin Nuxt UI
génère les déclarations de composants nécessaires à `vue-tsc`.
