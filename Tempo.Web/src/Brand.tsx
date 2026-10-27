// Inline brand mark (public/brand/mark.svg, inlined so the late screen can recolor it:
// white tile + red glyph on the red screen, one component, no second asset).

export function BrandMark({ size = 20, late = false }: { size?: number; late?: boolean }) {
  const tile = late ? '#FFFFFF' : '#1F5EFF'
  const glyph = late ? '#B42318' : '#FFFFFF'
  return (
    <svg width={size} height={size} viewBox="0 0 200 200" aria-hidden="true">
      <rect width="200" height="200" rx="44" fill={tile} />
      <g transform="translate(100 100) scale(0.19)">
        <circle
          cx="0"
          cy="20"
          r="290"
          fill="none"
          stroke={glyph}
          strokeWidth="76"
          strokeDasharray="655 512 655"
          strokeLinecap="round"
          transform="rotate(-90 0 20)"
        />
        <g transform="translate(0 20) rotate(32)">
          <rect x="-38" y="-440" width="76" height="440" rx="38" fill={glyph} />
          <polygon points="-120,-390 0,-540 120,-390" fill={glyph} />
        </g>
        <circle cx="0" cy="20" r="60" fill={glyph} />
      </g>
    </svg>
  )
}

/** The app header's identity: mark + wordmark text, same size and position as before. */
export function BrandHeader({ late = false }: { late?: boolean }) {
  return (
    <div className="flex items-center gap-2">
      <BrandMark size={20} late={late} />
      <span className="text-[15px] font-bold tracking-[0.08em]">TEMPO</span>
    </div>
  )
}
