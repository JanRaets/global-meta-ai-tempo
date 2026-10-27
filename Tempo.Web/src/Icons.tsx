// Stroke icons from the design canvas (TEMPO mobile screens), plus car/walk in the same style.

type IconProps = { size?: number; strokeWidth?: number; className?: string; knobFill?: string }

const base = (size: number, strokeWidth: number) => ({
  width: size,
  height: size,
  viewBox: '0 0 24 24',
  fill: 'none',
  stroke: 'currentColor',
  strokeWidth,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
})

export function ModeIcon({ mode, size = 18, strokeWidth = 2, className }: IconProps & { mode: string }) {
  const props = { ...base(size, strokeWidth), className, 'aria-hidden': true }
  switch (mode) {
    case 'bicycle':
      return (
        <svg {...props}>
          <circle cx="5.5" cy="17.5" r="3.5" />
          <circle cx="18.5" cy="17.5" r="3.5" />
          <path d="M15 6h-2l-3 7H5.5M12 6l3.5 7.5M8 6h3" />
        </svg>
      )
    case 'train':
      return (
        <svg {...props}>
          <rect x="4" y="4" width="16" height="14" rx="3" />
          <path d="M4 11h16M8 18l-1.5 3M16 18l1.5 3" />
          <circle cx="8" cy="14.5" r="1" />
          <circle cx="16" cy="14.5" r="1" />
        </svg>
      )
    case 'car':
      return (
        <svg {...props}>
          <path d="M5 11l1.5-4.5A2 2 0 0 1 8.4 5h7.2a2 2 0 0 1 1.9 1.5L19 11" />
          <rect x="3" y="11" width="18" height="7" rx="2" />
          <circle cx="7.5" cy="18" r="2" />
          <circle cx="16.5" cy="18" r="2" />
        </svg>
      )
    default: // walking
      return (
        <svg {...props}>
          <circle cx="13" cy="4.5" r="2" />
          <path d="M10 20l2-5.5M13 9l-2.5 6L14 17l1 4M13 9l3 3 3 1M13 9l-4 1-2 3" />
        </svg>
      )
  }
}

export function ClockIcon({ size = 24, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 7v5l3 2" />
    </svg>
  )
}

export function CalendarIcon({ size = 24, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <rect x="3" y="5" width="18" height="16" rx="2" />
      <path d="M3 10h18M8 3v4M16 3v4" />
    </svg>
  )
}

export function HabitIcon({ size = 24, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <path d="M4 12a8 8 0 1 0 16 0 8 8 0 0 0-16 0" />
      <path d="M8 12l3 3 5-6" />
    </svg>
  )
}

export function SettingsIcon({ size = 24, strokeWidth = 2, className, knobFill = '#FFFFFF' }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <path d="M4 7h16M4 12h16M4 17h16" />
      <circle cx="9" cy="7" r="2" fill={knobFill} />
      <circle cx="15" cy="12" r="2" fill={knobFill} />
      <circle cx="10" cy="17" r="2" fill={knobFill} />
    </svg>
  )
}

export function PauseIcon({ size = 22, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <path d="M9 6v12M15 6v12" />
    </svg>
  )
}

export function PlayIcon({ size = 22, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <path d="M8 5l11 7-11 7z" />
    </svg>
  )
}

export function MicIcon({ size = 22, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <rect x="9" y="3" width="6" height="11" rx="3" />
      <path d="M5 11a7 7 0 0 0 14 0M12 18v3" />
    </svg>
  )
}

export function CameraIcon({ size = 22, strokeWidth = 2, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <rect x="3" y="7" width="18" height="13" rx="2" />
      <circle cx="12" cy="13" r="3.5" />
      <path d="M8.5 7L10 4.5h4L15.5 7" />
    </svg>
  )
}

export function ArrowIcon({ size = 22, strokeWidth = 2.4, className }: IconProps) {
  return (
    <svg {...base(size, strokeWidth)} className={className} aria-hidden="true">
      <path d="M5 12h14M13 6l6 6-6 6" />
    </svg>
  )
}
