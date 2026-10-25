export interface UploadTicket {
  id: string
  method: string
  headers: Record<string, string>
  partSize: number
  parts: { number: number; url: string }[]
  expiresAt: string
}

class StorageUploadError extends Error {
  constructor(public status: number) {
    super(`Le stockage a refusé l’envoi (HTTP ${status}).`)
  }
}

// Separate from the cookie-authenticated API transport: bytes go straight to S3.
export async function uploadFile(
  file: File,
  ticket: UploadTicket,
  options: {
    signal: AbortSignal
    onProgress: (loaded: number) => void
    resume?: () => Promise<UploadTicket>
  },
): Promise<void> {
  const id = ticket.id
  const partSize = ticket.partSize
  const count = file.size === 0 ? 1 : Math.ceil(file.size / partSize)
  if (!Number.isSafeInteger(count) || count < 1 || partSize < 0)
    throw new Error('Découpage de fichier invalide reçu du serveur.')
  const sent = new Set<number>()
  let loaded = 0
  let renewedWithoutProgress = false
  async function resume() {
    if (!options.resume) throw new Error('La reprise de cet envoi est indisponible.')
    options.signal.throwIfAborted()
    const next = await options.resume()
    options.signal.throwIfAborted()
    if (next.id !== id || next.partSize !== partSize)
      throw new Error('Le serveur a changé le transfert à reprendre.')
    return next
  }
  batches: while (true) {
    options.signal.throwIfAborted()
    for (const part of ticket.parts) {
      if (
        !Number.isInteger(part.number) ||
        part.number < 1 ||
        part.number > count ||
        sent.has(part.number)
      )
        throw new Error('La reprise ne progresse pas. Réessayez après avoir vérifié le stockage.')
      const body = file.slice(
        (part.number - 1) * partSize,
        Math.min(part.number * partSize, file.size),
      )
      try {
        if (Date.parse(ticket.expiresAt) <= Date.now()) throw new StorageUploadError(403)
        await put(body, ticket, part.url, options.signal, (bytes) =>
          options.onProgress(loaded + Math.min(bytes, body.size)),
        )
      } catch (error) {
        options.signal.throwIfAborted()
        // A signed URL may expire while queued or in flight. Refresh once until
        // another part succeeds; a permanent denial must not loop PUTs forever.
        if (
          error instanceof StorageUploadError &&
          error.status === 403 &&
          !renewedWithoutProgress
        ) {
          renewedWithoutProgress = true
          ticket = await resume()
          options.onProgress(loaded)
          continue batches
        }
        throw error
      }
      options.signal.throwIfAborted()
      sent.add(part.number)
      loaded += body.size
      options.onProgress(loaded)
      renewedWithoutProgress = false
    }
    if (ticket.parts.length === 0 || sent.size === count) return
    // The server signs missing parts in batches, not necessarily all at once.
    ticket = await resume()
  }
}

function put(
  body: Blob,
  ticket: UploadTicket,
  url: string,
  signal: AbortSignal,
  onProgress: (loaded: number) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    signal.throwIfAborted()
    const xhr = new XMLHttpRequest()
    function abort() {
      xhr.abort()
    }
    function finish(error?: Error) {
      signal.removeEventListener('abort', abort)
      if (error) reject(error)
      else resolve()
    }
    xhr.open(ticket.method, url)
    xhr.withCredentials = false
    for (const [name, value] of Object.entries(ticket.headers)) xhr.setRequestHeader(name, value)
    xhr.upload.addEventListener('progress', (event) => onProgress(event.loaded))
    xhr.addEventListener('load', () =>
      finish(
        xhr.status >= 200 && xhr.status < 300 ? undefined : new StorageUploadError(xhr.status),
      ),
    )
    xhr.addEventListener('error', () =>
      finish(new Error('Envoi interrompu : vérifiez votre connexion et l’accès au stockage.')),
    )
    xhr.addEventListener('abort', () => finish(new DOMException('Envoi interrompu', 'AbortError')))
    signal.addEventListener('abort', abort, { once: true })
    xhr.send(body)
  })
}
