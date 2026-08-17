/**
 * Minimal typed client for the Admin API.
 *
 * All requests go through the Vite dev proxy (`/api` -> Admin API), so the
 * browser sees one origin. Authentication headers are added here in Phase 3;
 * nothing in this module ever persists credentials to localStorage - the
 * session token will live in memory / an HttpOnly cookie, not in script-readable
 * storage.
 */

export interface ServiceInfo {
  service: string
  version: string
  environment: string
}

export interface HealthCheckEntry {
  name: string
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  durationMs: number
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  totalDurationMs: number
  checks: HealthCheckEntry[]
}

export class ApiError extends Error {
  readonly status: number
  readonly correlationId: string | null

  constructor(status: number, message: string, correlationId: string | null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.correlationId = correlationId
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      ...init?.headers,
    },
  })

  if (!response.ok) {
    // Surface the correlation id so a user can quote it to an operator.
    const correlationId = response.headers.get('X-Correlation-Id')
    throw new ApiError(
      response.status,
      `Request to ${path} failed with HTTP ${response.status}`,
      correlationId,
    )
  }

  return (await response.json()) as T
}

export interface DeviceListItem {
  id: string
  hostname: string
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  isOnline: boolean
  enrolledAt: string
}

export interface DevicePage {
  items: DeviceListItem[]
  totalCount: number
  page: number
  pageSize: number
}

export interface DeviceCounts {
  total: number
  online: number
  offline: number
  retired: number
}

export function getServiceInfo(): Promise<ServiceInfo> {
  return request<ServiceInfo>('/')
}

export function getDevices(page: number, pageSize: number, search: string): Promise<DevicePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim().length > 0) {
    params.set('search', search.trim())
  }
  return request<DevicePage>(`/admin/v1/devices?${params}`)
}

export function getDeviceCounts(): Promise<DeviceCounts> {
  return request<DeviceCounts>('/admin/v1/devices/counts')
}

export function getReadiness(): Promise<HealthReport> {
  // /health/ready returns 503 when unhealthy, which `request` would treat as an
  // error; a health page wants the body either way, so fetch it directly.
  return fetch('/api/health/ready', { headers: { Accept: 'application/json' } }).then(
    async (response) => (await response.json()) as HealthReport,
  )
}
