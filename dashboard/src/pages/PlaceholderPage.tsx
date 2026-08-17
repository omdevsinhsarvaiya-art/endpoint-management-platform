interface PlaceholderPageProps {
  title: string
  phase: string
}

/**
 * Honest placeholder for navigation targets whose backing feature has not been
 * built yet. Shows which phase delivers it instead of an empty table that
 * implies "nothing found".
 */
export function PlaceholderPage({ title, phase }: PlaceholderPageProps) {
  return (
    <div className="card">
      <div className="empty-state">
        <div className="title">{title} is not implemented yet</div>
        <div>This area arrives in {phase}.</div>
      </div>
    </div>
  )
}
