import type { ReactNode } from 'react'
import { Icon } from '../components/Icon'

interface PlaceholderPageProps {
  title: string
  phase: string
  /** Where to go in the meantime, when the capability exists somewhere else. */
  alternative?: ReactNode
}

/**
 * Honest placeholder for navigation targets whose backing feature has not been
 * built yet. Shows which phase delivers it instead of an empty table that
 * implies "nothing found".
 */
export function PlaceholderPage({ title, phase, alternative }: PlaceholderPageProps) {
  return (
    <div className="card">
      <div className="empty-state">
        <Icon name="clock" size={40} strokeWidth={1.25} className="icon" />
        <div className="title">{title} is not implemented yet</div>
        <div>This area arrives in {phase}.</div>
        {alternative && <div style={{ marginTop: 10 }}>{alternative}</div>}
      </div>
    </div>
  )
}
