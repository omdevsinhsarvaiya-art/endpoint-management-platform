/**
 * The device list's lifecycle filter.
 *
 * Three choices reach the API as two states plus an absence, which is the one
 * detail worth isolating: "All" is not a status the server understands, it is the
 * absence of a status filter. Sending it as a literal would silently return
 * nothing, and the default view is Active, so the mistake would look like an empty
 * estate rather than a bug.
 */

/** What the operator picked. */
export type DeviceListView = 'Active' | 'Retired' | 'All'

/** What the server understands. */
export type DeviceStatusFilter = 'Active' | 'Retired' | undefined

export const deviceListViews: readonly DeviceListView[] = ['Active', 'Retired', 'All']

/** The default: retired machines are history, not the day-to-day estate. */
export const defaultDeviceListView: DeviceListView = 'Active'

/**
 * Maps a chosen view onto the `status` query parameter.
 *
 * "All" maps to undefined so the parameter is omitted entirely rather than sent
 * empty, which is what the API expects for "no filter".
 */
export function statusFilterFor(view: DeviceListView): DeviceStatusFilter {
  return view === 'All' ? undefined : view
}

/** Label for the count line under the table, e.g. "3 active devices". */
export function describeDeviceCount(view: DeviceListView, total: number): string {
  const noun = total === 1 ? 'device' : 'devices'
  return view === 'All' ? `${total} ${noun}` : `${total} ${view.toLowerCase()} ${noun}`
}
