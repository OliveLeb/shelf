import ui from '@nuxt/ui/vite'
import { shelfUi } from '@shelf/ui/theme'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite-plus'

export default defineConfig({
  plugins: [vue(), ui(shelfUi)],
  // Port 3000 matches the API's documented Authentication__ClientOrigin.
  server: { port: 3000, strictPort: true },
  run: {
    tasks: {
      // Vite writes the Nuxt UI component and auto-import declarations vue-tsc relies on.
      typecheck: { command: 'vue-tsc --noEmit', dependsOn: ['build'] },
    },
  },
  preview: { port: 3000, strictPort: true },
})
