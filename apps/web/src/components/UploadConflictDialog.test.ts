import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, it } from 'vite-plus/test'
import UploadConflictDialog from './UploadConflictDialog.vue'

afterEach(() => {
  document.body.innerHTML = ''
})

it('defaults to keeping both and makes replacement an explicit choice for this batch', async () => {
  const dialog = mount(UploadConflictDialog, {
    props: { name: 'rapport.txt', destinationLabel: 'Archives' },
    attachTo: document.body,
  })
  await flushPromises()
  const content = document.querySelector('[role="dialog"]')!
  expect(content.textContent).toContain('rapport.txt')
  expect(content.textContent).toContain('Archives')
  const keepBoth = content.querySelector<HTMLInputElement>('input[value="keepBoth"]')!
  expect(keepBoth.checked).toBe(true)
  content.querySelector<HTMLFormElement>('form')!.requestSubmit()
  await flushPromises()
  expect(dialog.emitted('decide')).toEqual([['keepBoth', false]])

  const replace = content.querySelector<HTMLInputElement>('input[value="replace"]')!
  replace.click()
  content.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click()
  await flushPromises()
  content.querySelector<HTMLFormElement>('form')!.requestSubmit()
  await flushPromises()
  expect(dialog.emitted('decide')?.[1]).toEqual(['replace', true])
  dialog.unmount()
})
