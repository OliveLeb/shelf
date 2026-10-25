export interface UploadTicket {
  id: string
  method: string
  headers: Record<string, string>
  partSize: number
  parts: { number: number; url: string }[]
  expiresAt: string
}

// Separate from the cookie-authenticated API transport: bytes go straight to S3.
export function uploadFile(
  file: File,
  ticket: UploadTicket,
  options: { signal: AbortSignal; onProgress: (loaded: number) => void },
): Promise<void> {
  if (ticket.parts.length !== 1 || ticket.partSize < file.size)
    return Promise.reject(
      new Error('Cet envoi nécessite le transfert multipart, qui sera disponible prochainement.'),
    )
  return new Promise((resolve, reject) => {
    options.signal.throwIfAborted()
    const xhr = new XMLHttpRequest()
    function abort() {
      xhr.abort()
    }
    function finish(error?: Error) {
      options.signal.removeEventListener('abort', abort)
      if (error) reject(error)
      else resolve()
    }
    xhr.open(ticket.method, ticket.parts[0]!.url)
    xhr.withCredentials = false
    for (const [name, value] of Object.entries(ticket.headers)) xhr.setRequestHeader(name, value)
    xhr.upload.addEventListener('progress', (event) => options.onProgress(event.loaded))
    xhr.addEventListener('load', () =>
      finish(
        xhr.status >= 200 && xhr.status < 300
          ? undefined
          : new Error(`Le stockage a refusé l’envoi (HTTP ${xhr.status}).`),
      ),
    )
    xhr.addEventListener('error', () =>
      finish(new Error('Envoi interrompu : vérifiez votre connexion et l’accès au stockage.')),
    )
    xhr.addEventListener('abort', () => finish(new DOMException('Envoi interrompu', 'AbortError')))
    options.signal.addEventListener('abort', abort, { once: true })
    xhr.send(file)
  })
}
