import { describe, expect, it } from 'vitest'
import { downsample, encodeWav } from './wavRecorder'

const ascii = (view: DataView, offset: number, length: number) =>
  Array.from({ length }, (_, i) => String.fromCharCode(view.getUint8(offset + i))).join('')

describe('encodeWav', () => {
  it('writes a valid mono 16-bit PCM header and the right byte length', () => {
    const samples = new Float32Array(1000)
    const buffer = encodeWav(samples, 16000)
    const view = new DataView(buffer)

    expect(buffer.byteLength).toBe(44 + 1000 * 2)
    expect(ascii(view, 0, 4)).toBe('RIFF')
    expect(view.getUint32(4, true)).toBe(36 + 2000) // RIFF size
    expect(ascii(view, 8, 4)).toBe('WAVE')
    expect(ascii(view, 12, 4)).toBe('fmt ')
    expect(view.getUint16(20, true)).toBe(1) // PCM
    expect(view.getUint16(22, true)).toBe(1) // mono
    expect(view.getUint32(24, true)).toBe(16000) // sample rate
    expect(view.getUint32(28, true)).toBe(32000) // byte rate = rate * 2
    expect(view.getUint16(32, true)).toBe(2) // block align
    expect(view.getUint16(34, true)).toBe(16) // bits per sample
    expect(ascii(view, 36, 4)).toBe('data')
    expect(view.getUint32(40, true)).toBe(2000) // data size
  })

  it('clamps and scales samples to int16', () => {
    const buffer = encodeWav(new Float32Array([0, 1, -1, 2, -2, 0.5]), 16000)
    const view = new DataView(buffer)
    expect(view.getInt16(44, true)).toBe(0)
    expect(view.getInt16(46, true)).toBe(0x7fff)
    expect(view.getInt16(48, true)).toBe(-0x8000)
    expect(view.getInt16(50, true)).toBe(0x7fff) // clamped
    expect(view.getInt16(52, true)).toBe(-0x8000) // clamped
    expect(view.getInt16(54, true)).toBe(Math.trunc(0.5 * 0x7fff)) // DataView truncates
  })
})

describe('downsample', () => {
  it('reduces 48 kHz to 16 kHz at one third the length', () => {
    const input = new Float32Array(48000).fill(0.25)
    const output = downsample(input, 48000, 16000)
    expect(output.length).toBe(16000)
    expect(output[0]).toBeCloseTo(0.25)
    expect(output[output.length - 1]).toBeCloseTo(0.25)
  })

  it('returns the input untouched at the target rate', () => {
    const input = new Float32Array([0.1, 0.2])
    expect(downsample(input, 16000, 16000)).toBe(input)
  })
})
