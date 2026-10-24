# Web et mobile en Vue 3, mobile empaqueté avec Capacitor

Le web et le mobile sont deux applications Vue 3 (Vite, TypeScript) qui partagent un design system construit sur Nuxt UI, sans le framework Nuxt : le mobile est la même pile web, empaquetée pour Android et iOS avec Capacitor. Une seule technologie d'interface permet de partager composants, thème et logique client dans le workspace pnpm (`packages/`), au prix d'un rendu WebView plutôt que natif, acceptable pour une instance personnelle de stockage de fichiers.

L'outillage JavaScript est unifié par Vite+ (Vite, Vitest, Oxlint, Oxfmt, cache de tâches) avec une configuration racine unique. Vue-tsc reste nécessaire pour vérifier les types des composants `.vue`, que le contrôle de types intégré de Vite+ ne couvre pas. Node.js s'exécute dans un conteneur de développement (`infra/node/`) pour éviter une installation locale par contributeur.

## Considered Options

- Nuxt pour le web : rendu serveur et routage par fichiers inutiles derrière une connexion obligatoire, et moins direct à empaqueter avec Capacitor.
- Application mobile native ou React Native : meilleur rendu natif, rejeté car il empêche de partager l'interface avec le web.
- Outils séparés (Vite, Vitest, Oxlint, Oxfmt installés un par un) : équivalent fonctionnellement, mais plus de configurations et de montées de version à aligner.
