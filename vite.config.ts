import { defineConfig } from 'vite-plus'

// Workspace-wide Vite+ settings: `vp check` formats (Oxfmt) and lints (Oxlint) the
// TypeScript workspaces only. Vue templates are type-checked by vue-tsc (`typecheck`).
const outsideFrontend = [
  'apps/backend/**',
  'contracts/**',
  'docs/**',
  'infra/**',
  '.github/**',
  '.agents/**',
  '**/*.md',
  '*.yaml',
]

export default defineConfig({
  fmt: {
    semi: false,
    singleQuote: true,
    printWidth: 100,
    ignorePatterns: [...outsideFrontend, '**/auto-imports.d.ts', '**/components.d.ts'],
  },
  lint: {
    plugins: ['eslint', 'typescript', 'unicorn', 'oxc', 'import', 'vue', 'vitest'],
    jsPlugins: [{ name: 'vite-plus', specifier: 'vite-plus/oxlint-plugin' }],
    rules: {
      'vite-plus/prefer-vite-plus-imports': 'error',
    },
    ignorePatterns: outsideFrontend,
  },
  run: {
    cache: true,
  },
})
