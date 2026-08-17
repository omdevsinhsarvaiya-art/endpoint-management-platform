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

export function getServiceInfo(): Promise<ServiceInfo> {
  return request<ServiceInfo>('/')
}

export function getReadiness(): Promise<HealthReport> {
  // /health/ready returns 503 when unhealthy, which `request` would treat as an
  // error; a health page wants the body either way, so fetch it directly.
  return fetch('/api/health/ready', { headers: { Accept: 'application/json' } }).then(
    async (response) => (await response.json()) as HealthReport,
  )
}
