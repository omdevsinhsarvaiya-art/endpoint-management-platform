import type { TrackedTask } from './useTaskTracker'
import { Icon } from './Icon'

/**
 * Renders tracked tasks as banners: spinner while the work is genuinely still
 * out on the machine, green only when Windows reported success, red with the
 * agent's reason when it did not.
 */
export function TaskProgress({
  tasks,
  onDismiss,
}: {
  tasks: TrackedTask[]
  onDismiss: (taskId: string) => void
}) {
  if (tasks.length === 0) return null

  return (
    <>
      {tasks.map((t) => {
        if (!t.terminal) {
          return (
            <div key={t.taskId} className="info-banner" role="status">
              <div className="loading">
                <span>
                  <strong>{t.label}</strong> — {t.stage}
                </span>
              </div>
            </div>
          )
        }

        return (
          <div
            key={t.taskId}
            className={t.succeeded ? 'notice-banner' : 'error-banner'}
            role={t.succeeded ? 'status' : 'alert'}
          >
            <Icon name={t.succeeded ? 'check' : 'alert'} size={15} />
            <span style={{ flex: 1 }}>
              <strong>{t.label}</strong> — {t.stage}
              {t.message ? `: ${t.message}` : ''}
            </span>
            <button
              type="button"
              className="btn-ghost btn-sm"
              style={{ color: 'inherit' }}
              onClick={() => onDismiss(t.taskId)}
            >
              Dismiss
            </button>
          </div>
        )
      })}
    </>
  )
}
