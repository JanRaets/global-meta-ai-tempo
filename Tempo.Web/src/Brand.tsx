// Inline brand mark (public/brand/mark-mono.svg): pure currentColor, so it is ink on the
// white screens and white on the red late screen with no second asset and no props.

export function BrandMark({ size = 20 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 200 200" aria-hidden="true">
      <g transform="translate(100 100) scale(0.19)">
        <circle
          cx="0"
          cy="20"
          r="290"
          fill="none"
          stroke="currentColor"
          strokeWidth="76"
          strokeDasharray="655 512 655"
          strokeLinecap="round"
          transform="rotate(-90 0 20)"
        />
        <g transform="translate(0 20) rotate(32)">
          <rect x="-38" y="-440" width="76" height="440" rx="38" fill="currentColor" />
          <polygon points="-120,-390 0,-540 120,-390" fill="currentColor" />
        </g>
        <circle cx="0" cy="20" r="60" fill="currentColor" />
      </g>
    </svg>
  )
}

/** The app header's identity: mark + wordmark text, same size and position as before. */
export function BrandHeader() {
  return (
    <div className="flex items-center gap-2">
      <BrandMark size={20} />
      <span className="text-[15px] font-bold tracking-[0.08em]">TEMPO</span>
    </div>
  )
}
