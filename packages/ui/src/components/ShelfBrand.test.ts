import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vite-plus/test'
import ShelfBrand from './ShelfBrand.vue'

describe('ShelfBrand', () => {
  it('shows the tagline only when one is given', () => {
    expect(mount(ShelfBrand).text()).toBe('Shelf')
    expect(mount(ShelfBrand, { props: { tagline: 'Vos fichiers' } }).text()).toBe(
      'ShelfVos fichiers',
    )
  })
})
