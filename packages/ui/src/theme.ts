import type { NuxtUIOptions } from '@nuxt/ui/vite'

// Shared Nuxt UI configuration: every Shelf app passes it to the `ui()` Vite plugin
// so web and mobile render the same design system.
export const shelfUi = {
  ui: {
    colors: {
      primary: 'indigo',
      neutral: 'zinc',
    },
  },
  // Lets Nuxt UI resolve its components inside this package's components.
  scanPackages: ['@shelf/ui'],
} satisfies NuxtUIOptions
