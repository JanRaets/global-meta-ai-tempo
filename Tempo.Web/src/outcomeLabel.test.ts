import { describe, expect, it } from 'vitest'
import { outcomeLabel } from './api'

// Outcome amendment (28 Oct 2026): past rows show what happened, never a plan.
describe('outcomeLabel', () => {
  it('renders an on-time trip', () => {
    expect(outcomeLabel({ kind: 'onTime', leftAt: '08:11', arrivedAt: '08:18', lateByMin: null })).toBe(
      'Left 08:11 · arrived 08:18 · on time',
    )
  })

  it('renders a late trip with the slipped minutes', () => {
    expect(outcomeLabel({ kind: 'late', leftAt: '08:31', arrivedAt: '08:36', lateByMin: 6 })).toBe(
      'Left 08:31 · arrived 08:36 · 6 min late',
    )
  })

  it('renders no data plainly', () => {
    expect(outcomeLabel({ kind: 'noData', leftAt: null, arrivedAt: null, lateByMin: null })).toBe('No trip recorded')
  })

  it('copes with a missing departure', () => {
    expect(outcomeLabel({ kind: 'onTime', leftAt: null, arrivedAt: '08:18', lateByMin: null })).toBe(
      'Arrived 08:18 · on time',
    )
  })
})
