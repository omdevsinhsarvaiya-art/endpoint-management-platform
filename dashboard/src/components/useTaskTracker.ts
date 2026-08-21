import { useCallback, useEffect, useRef, useState } from 'react'
import { getDeviceTasks } from '../api/client'

const POLL_MS = 3_000

/** The states the tracker reports, in the order a task moves through them. */
export interface TrackedTask {
  taskId: string
  /** What the administrator asked for, e.g. `Restart` or `Stop "Spooler"`. */
  label: string
  /** Human description of where the task is right now. */
  stage: string
  /** Set once the task reaches a terminal state. */
  terminal: boolean
  succeeded: boolean | null
  /** The agent's result message, when there is one. */
  message: string | null
}

/**
 * Follows queued tasks to their terminal state so the UI can report what
 * actually happened on Windows, not merely that the server accepted a request.
 *
 * The stages come straight from the server's task record: Queued means the
 * device has not picked it up, Delivered means the agent is acting on it, and
 * only Succeeded means the Windows operation reported back as done. An HTTP 200
 * from the queue endpoint appears here as "waiting", never as success —
 * conflating those two is the failure mode this hook exists to prevent.
 *
 * `offline` softens the Queued wording: a task for an offline machine is not
 * about to run, and saying "waiting for the device to check in" without
 * qualification would imply it is.
 */
export function useTaskTracker(deviceId: string, offline: boolean) {
  const [tracked, setTracked] = useState<TrackedTask[]>([])
  const offlineRef = useRef(offline)
  offlineRef.current = offline

  const track = useCallback((taskId: string, label: string) => {
    setTracked((current) => [
      ...current.filter((t) => t.taskId !== taskId),
      {
        taskId,
        label,
        stage: offlineRef.current
          ? 'Queued — device offline; runs when the agent reconnects.'
          : 'Queued — waiting for the device to check in…',
        terminal: false,
        succeeded: null,
        message: null,
      },
    ])
  }, [])

  const dismiss = useCallback((taskId: string) => {
    setTracked((current) => current.filter((t) => t.taskId !== taskId))
  }, [])

  const anyPending = tracked.some((t) => !t.terminal)

  useEffect(() => {
    if (!anyPending) return

    let cancelled = false

    const timer = setInterval(async () => {
      try {
        const tasks = await getDeviceTasks(deviceId)
        if (cancelled) return

        setTracked((current) =>
          current.map((t) => {
            if (t.terminal) return t
            const task = tasks.find((x) => x.id === t.taskId)
            if (!task) return t

            switch (task.status) {
              case 'Queued':
                return t
              case 'Delivered':
                return { ...t, stage: 'Running on Windows…' }
              case 'Succeeded':
                return {
                  ...t,
                  stage: 'Succeeded',
                  terminal: true,
                  succeeded: true,
                  message: task.resultMessage,
                }
              case 'Failed':
                return {
                  ...t,
                  stage: 'Failed',
                  terminal: true,
                  succeeded: false,
                  message: task.resultMessage,
                }
              case 'Expired':
                return {
                  ...t,
                  stage: 'Expired',
                  terminal: true,
                  succeeded: false,
                  message:
                    task.resultMessage ??
                    'The device never picked this up before the task expired.',
                }
              case 'Cancelled':
                return {
                  ...t,
                  stage: 'Cancelled',
                  terminal: true,
                  succeeded: false,
                  message: task.resultMessage,
                }
              default:
                return t
            }
          }),
        )
      } catch {
        // A failed poll is not a failed task — keep the last known state and
        // try again on the next tick.
      }
    }, POLL_MS)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [anyPending, deviceId])

  return { tracked, track, dismiss }
}
