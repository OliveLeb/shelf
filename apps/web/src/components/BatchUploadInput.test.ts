import { mount } from '@vue/test-utils'
import { expect, it } from 'vite-plus/test'
import BatchUploadInput from './BatchUploadInput.vue'

it('selects several files for the labelled destination and permits selecting them again', async () => {
  const input = mount(BatchUploadInput, { props: { destinationLabel: 'Photos / Été' } })
  const picker = input.get('input[type="file"]')
  expect(picker.attributes()).toHaveProperty('multiple')
  expect(input.text()).toContain('Photos / Été')
  const files = [new File(['a'], 'a.txt'), new File(['bc'], 'b.txt')]
  Object.defineProperty(picker.element, 'files', { value: files })
  await picker.trigger('change')
  expect(input.emitted('files')).toEqual([[files]])
  expect((picker.element as HTMLInputElement).value).toBe('')
  input.unmount()
})

it('ignores internal drags and rejects dropped folders without rejecting neighbouring files', async () => {
  const input = mount(BatchUploadInput, { props: { destinationLabel: 'Espace personnel' } })
  const zone = input.get('[aria-label="Envoi depuis votre ordinateur"]')
  await zone.trigger('drop', { dataTransfer: { types: ['application/x-shelf-elements'] } })
  expect(input.emitted('files')).toBeUndefined()
  const file = new File(['bonjour'], 'bonjour.txt')
  await zone.trigger('drop', {
    dataTransfer: {
      types: ['Files'],
      items: [
        {
          kind: 'file',
          webkitGetAsEntry: () => ({ isDirectory: true }),
          getAsFile: () => new File([], 'Photos'),
        },
        { kind: 'file', webkitGetAsEntry: () => ({ isDirectory: false }), getAsFile: () => file },
      ],
    },
  })
  expect(input.emitted('files')).toEqual([[[file]]])
  expect(input.get('[role="alert"]').text()).toContain('Les dossiers ne sont pas importés')
  input.unmount()
})
