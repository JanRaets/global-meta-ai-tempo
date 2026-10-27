// Client-side photo prep for POST /api/capture (capture-appointment spec): downscale
// to <=1600 px on the long side, re-encode as JPEG aiming for <500 KB — all with a
// canvas, no library (same no-dependency spirit as the WAV recorder).

export const MAX_LONG_SIDE = 1600
export const TARGET_BYTES = 500_000

/** Scaled dimensions fitting within maxSide on the long side, aspect kept. Never upscales. */
export function fitWithin(width: number, height: number, maxSide: number): { width: number; height: number } {
  const longSide = Math.max(width, height)
  if (longSide <= maxSide) return { width, height }
  const scale = maxSide / longSide
  return { width: Math.max(1, Math.round(width * scale)), height: Math.max(1, Math.round(height * scale)) }
}

/** Resizes the photo and returns a JPEG blob, stepping quality down until it fits the target. */
export async function resizeImage(file: Blob): Promise<Blob> {
  const bitmap = await createImageBitmap(file)
  try {
    const { width, height } = fitWithin(bitmap.width, bitmap.height, MAX_LONG_SIDE)
    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = height
    const ctx = canvas.getContext('2d')
    if (!ctx) throw new Error('canvas unavailable')
    ctx.drawImage(bitmap, 0, 0, width, height)
    for (const quality of [0.85, 0.7, 0.55, 0.4]) {
      const blob = await new Promise<Blob | null>((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality))
      if (blob && (blob.size <= TARGET_BYTES || quality === 0.4)) return blob
    }
    throw new Error('could not encode the photo')
  } finally {
    bitmap.close()
  }
}
