import ui from '@nuxt/ui/vue-plugin'
import { config } from '@vue/test-utils'
import { beforeEach } from 'vite-plus/test'

// Install the same plugins and bundled icons as main.ts, without remote icon requests.
config.global.plugins.push(ui)
beforeEach(() => localStorage.clear())
