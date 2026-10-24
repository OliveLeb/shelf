import './main.css'

import ui from '@nuxt/ui/vue-plugin'
import { createPinia } from 'pinia'
import { createApp } from 'vue'
import App from './App.vue'
import { makeRouter } from './router'

const pinia = createPinia()
const router = makeRouter(pinia)

createApp(App).use(pinia).use(router).use(ui).mount('#app')
