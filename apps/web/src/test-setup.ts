import ui from '@nuxt/ui/vue-plugin'
import { config } from '@vue/test-utils'

// Install the same plugins and bundled icons as main.ts, without remote icon requests.
config.global.plugins.push(ui)
