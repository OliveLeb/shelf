import ui from '@nuxt/ui/vite'
import { shelfUi } from '@shelf/ui/theme'
import basicSsl from '@vitejs/plugin-basic-ssl'
import vue from '@vitejs/plugin-vue'
import { defineConfig, loadEnv } from 'vite-plus'

export default defineConfig(({ mode }) => ({
  plugins: [vue(), ui({ ...shelfUi, icon: { clientBundle: { scan: true } } }), basicSsl()],
  server: {
    port: 3000,
    strictPort: true,
    proxy: {
      '/api': {
        target: loadEnv(mode, '.', 'SHELF_').SHELF_API_TARGET || 'http://localhost:5050',
        rewrite: (path) => path.replace(/^\/api/, ''),
      },
    },
  },
  test: { environment: 'happy-dom', setupFiles: ['./src/test-setup.ts'] },
  run: {
    tasks: {
      // Vite writes the Nuxt UI component and auto-import declarations vue-tsc relies on.
      typecheck: { command: 'vue-tsc --noEmit', dependsOn: ['build'] },
    },
  },
  preview: { port: 3000, strictPort: true },
}))
