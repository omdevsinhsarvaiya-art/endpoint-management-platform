import { getDevice, requestInventoryRefresh } from '../api/client'

/**
 * Why this exists: a task reaching Succeeded means Windows did the thing — it
 * does not mean the dashboard's data shows it. Service state, process lists and
 * local accounts all come from inventory, and nothing server-side requests a
 * fresh inventory when a task completes. Without this chase, "Stop Spooler →
 * Succeeded" sits next to a table still saying Running until someone clicks
 * Refresh, which reads as a contradiction.
 *
 * The chase is the same sequence a careful administrator performs by hand:
 * request an inventory refresh, wait for the device to upload one, then re-read.
 */
export interface InventorySyncDeps {
  requestRefresh: (deviceId: string) => Promise<void>
  /** True while the device has not yet uploaded an inventory newer than the request. */
  isRefreshPending: (deviceId: string) => Promise<boolean>
  sleep: (ms: number) => Promise<void>
}

const defaultDeps: InventorySyncDeps = {
  requestRefresh: (deviceId) => requestInventoryRefresh(deviceId),
  isRefreshPending: async (deviceId) => (await getDevice(deviceId)).inventoryRefreshPending,
  sleep: (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
}

export interface InventorySyncOptions {
  /** Give up after this long; the device may be slow, offline, or rebooting. */
  timeoutMs?: number
  intervalMs?: number
  deps?: InventorySyncDeps
}

/**
 * Requests a fresh inventory and waits until the device has uploaded one.
 *
 * Returns true when fresh data is known to have arrived, false on timeout.
 * A timeout is not an error — the caller should still reload, because partial
 * freshness beats staleness — but it must not be reported as certainty that the
 * view is current.
 *
 * The pending flag, not a timestamp comparison, is the completion signal: the
 * server sets it when the refresh is requested and clears it only once an
 * inventory *newer than the request* lands, so a poll can never be satisfied by
 * an upload that was already in flight with pre-change data.
 */
export async function waitForFreshInventory(
  deviceId: string,
  { timeoutMs = 180_000, intervalMs = 10_000, deps = defaultDeps }: InventorySyncOptions = {},
): Promise<boolean> {
  await deps.requestRefresh(deviceId)

  let waited = 0
  while (waited < timeoutMs) {
    await deps.sleep(intervalMs)
    waited += intervalMs

    try {
      if (!(await deps.isRefreshPending(deviceId))) {
        return true
      }
    } catch {
      // One failed poll is a blip, not a verdict; keep waiting.
    }
  }

  return false
}
