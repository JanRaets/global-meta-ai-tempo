// WAV capture for hold-to-talk (design D9): Web Audio → mono 16-bit PCM 16 kHz → WAV,
// all client-side, no library. The ASR API accepts WAV only (design D2 API facts).

export const TARGET_SAMPLE_RATE = 16000
export const MAX_SECONDS = 15

/** Average-pooling downsample to the target rate. Exported for the encoder unit test. */
export function downsample(samples: Float32Array, fromRate: number, toRate: number): Float32Array {
  if (fromRate === toRate) return samples
  const ratio = fromRate / toRate
  const length = Math.floor(samples.length / ratio)
  const result = new Float32Array(length)
  for (let i = 0; i < length; i++) {
    const start = Math.floor(i * ratio)
    const end = Math.min(Math.floor((i + 1) * ratio), samples.length)
    let sum = 0
    for (let j = start; j < end; j++) sum += samples[j]
    result[i] = end > start ? sum / (end - start) : 0
  }
  return result
}

/** Mono 16-bit PCM WAV (RIFF) from float samples in [-1, 1]. */
export function encodeWav(samples: Float32Array, sampleRate: number): ArrayBuffer {
  const bytesPerSample = 2
  const dataLength = samples.length * bytesPerSample
  const buffer = new ArrayBuffer(44 + dataLength)
  const view = new DataView(buffer)
  const writeAscii = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i++) view.setUint8(offset + i, text.charCodeAt(i))
  }
  writeAscii(0, 'RIFF')
  view.setUint32(4, 36 + dataLength, true)
  writeAscii(8, 'WAVE')
  writeAscii(12, 'fmt ')
  view.setUint32(16, 16, true) // fmt chunk size
  view.setUint16(20, 1, true) // PCM
  view.setUint16(22, 1, true) // mono
  view.setUint32(24, sampleRate, true)
  view.setUint32(28, sampleRate * bytesPerSample, true) // byte rate
  view.setUint16(32, bytesPerSample, true) // block align
  view.setUint16(34, 16, true) // bits per sample
  writeAscii(36, 'data')
  view.setUint32(40, dataLength, true)
  let offset = 44
  for (let i = 0; i < samples.length; i++, offset += 2) {
    const clamped = Math.max(-1, Math.min(1, samples[i]))
    view.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true)
  }
  return buffer
}

/**
 * Hold-to-talk recorder. Create and start it inside the touch/pointer handler — iOS Safari
 * only allows an AudioContext born from a user gesture, and it must be resumed explicitly.
 */
export class WavRecorder {
  private context: AudioContext | null = null
  private stream: MediaStream | null = null
  private processor: ScriptProcessorNode | null = null
  private source: MediaStreamAudioSourceNode | null = null
  private chunks: Float32Array[] = []
  private sampleCount = 0
  private maxSamples = 0

  async start(): Promise<void> {
    this.stream = await navigator.mediaDevices.getUserMedia({ audio: true })
    this.context = new AudioContext() // native rate; downsampled on stop
    await this.context.resume() // iOS: resume inside the gesture
    this.maxSamples = this.context.sampleRate * MAX_SECONDS
    this.source = this.context.createMediaStreamSource(this.stream)
    // ScriptProcessorNode is deprecated but universally supported, including iOS Safari.
    this.processor = this.context.createScriptProcessor(4096, 1, 1)
    this.processor.onaudioprocess = (e) => {
      if (this.sampleCount >= this.maxSamples) return // 15 s cap
      const input = e.inputBuffer.getChannelData(0)
      this.chunks.push(new Float32Array(input))
      this.sampleCount += input.length
    }
    this.source.connect(this.processor)
    this.processor.connect(this.context.destination)
  }

  /** Stops capture and returns the WAV blob (mono 16-bit PCM at 16 kHz). */
  async stop(): Promise<Blob> {
    const nativeRate = this.context?.sampleRate ?? TARGET_SAMPLE_RATE
    this.teardown()
    const all = new Float32Array(Math.min(this.sampleCount, this.maxSamples || this.sampleCount))
    let offset = 0
    for (const chunk of this.chunks) {
      const room = all.length - offset
      if (room <= 0) break
      all.set(room >= chunk.length ? chunk : chunk.subarray(0, room), offset)
      offset += chunk.length
    }
    const resampled = downsample(all, nativeRate, TARGET_SAMPLE_RATE)
    return new Blob([encodeWav(resampled, TARGET_SAMPLE_RATE)], { type: 'audio/wav' })
  }

  cancel(): void {
    this.teardown()
  }

  private teardown(): void {
    this.processor?.disconnect()
    this.source?.disconnect()
    this.stream?.getTracks().forEach((t) => t.stop())
    void this.context?.close()
    this.processor = null
    this.source = null
    this.stream = null
    this.context = null
  }
}
