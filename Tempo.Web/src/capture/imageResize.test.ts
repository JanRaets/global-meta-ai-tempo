import { describe, expect, it } from 'vitest'
import { fitWithin, MAX_LONG_SIDE } from './imageResize'

describe('fitWithin', () => {
  it('leaves a small photo untouched', () => {
    expect(fitWithin(800, 600, MAX_LONG_SIDE)).toEqual({ width: 800, height: 600 })
  })

  it('scales a wide photo to 1600 on the long side', () => {
    expect(fitWithin(4000, 3000, MAX_LONG_SIDE)).toEqual({ width: 1600, height: 1200 })
  })

  it('scales a tall photo on its long side', () => {
    expect(fitWithin(3000, 4000, MAX_LONG_SIDE)).toEqual({ width: 1200, height: 1600 })
  })

  it('leaves a photo exactly at the limit untouched', () => {
    expect(fitWithin(1600, 1200, MAX_LONG_SIDE)).toEqual({ width: 1600, height: 1200 })
  })
})
