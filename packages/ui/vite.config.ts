import ui from '@nuxt/ui/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite-plus'
import { shelfUi } from './src/theme'

export default defineConfig({
  plugins: [vue(), ui(shelfUi)],
  // Apps consume the sources. This build only makes the Nuxt UI plugin write the
  // component declarations that vue-tsc needs for every exported component.
  build: {
    lib: { entry: 'src/index.ts', formats: ['es'] },
    rolldownOptions: { external: [/^vue/, /^@nuxt\/ui/] },
  },
  run: {
    tasks: {
      typecheck: { command: 'vue-tsc --noEmit', dependsOn: ['build'] },
    },
  },
  test: {
    environment: 'happy-dom',
  },
})
