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

/**
 * Fired when any API call returns 401 - the session has expired or been
 * revoked. The auth provider listens and returns the user to the login page.
 */
export const sessionExpiredEvent = new EventTarget()

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      // Anti-CSRF: the API refuses cookie-authenticated mutations without this.
      'X-Requested-With': 'XMLHttpRequest',
      ...init?.headers,
    },
  })

  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }

  if (!response.ok) {
    // Surface the correlation id so a user can quote it to an operator.
    const correlationId = response.headers.get('X-Correlation-Id')
    throw new ApiError(
      response.status,
      `Request to ${path} failed with HTTP ${response.status}`,
      correlationId,
    )
  }

  // A successful mutation may answer 204 with no body. Parsing that as JSON
  // throws on an empty string, turning a success into a spurious failure.
  if (response.status === 204 || response.headers.get('Content-Length') === '0') {
    return undefined as T
  }

  return (await response.json()) as T
}

export interface DeviceListItem {
  id: string
  /** The Windows computer name, as reported by the agent. */
  hostname: string
  /** The administrator's console label, or null when none is set. */
  displayName: string | null
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  isOnline: boolean
  enrolledAt: string
  /** Newest published agent version for this device's platform, or null. */
  latestAgentVersion: string | null
  /** True when a strictly newer published agent exists for this device. */
  agentUpdateAvailable: boolean
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

export function getDevices(
  page: number,
  pageSize: number,
  search: string,
  status?: 'Active' | 'Retired',
): Promise<DevicePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim().length > 0) {
    params.set('search', search.trim())
  }
  if (status) {
    params.set('status', status)
  }
  return request<DevicePage>(`/admin/v1/devices?${params}`)
}

export function getDeviceCounts(): Promise<DeviceCounts> {
  return request<DeviceCounts>('/admin/v1/devices/counts')
}

export interface DeviceDisk {
  name: string
  fileSystem: string | null
  sizeBytes: number
  freeBytes: number
}

export interface DeviceHardware {
  serialNumber: string | null
  manufacturer: string | null
  model: string | null
  cpuName: string | null
  cpuPhysicalCores: number | null
  cpuLogicalProcessors: number | null
  totalMemoryBytes: number | null
  disks: DeviceDisk[] | null
  collectedAt: string
}

export interface DeviceNetworkInterface {
  name: string
  macAddress: string | null
  ipAddresses: string[] | null
  isUp: boolean
}

export interface DeviceLocalUser {
  sid: string
  name: string
  fullName: string | null
  description: string | null
  enabled: boolean
  passwordRequired: boolean
  passwordExpires: boolean
  lastLogon: string | null
  isLocalAdministrator: boolean
}

export interface DeviceLocalGroupMember {
  name: string
  sid: string | null
  memberType: string
}

export interface DeviceLocalGroup {
  sid: string
  name: string
  description: string | null
  memberCount: number
  isAdministrators: boolean
  members: DeviceLocalGroupMember[] | null
}

export interface DeviceDetail {
  id: string
  /** The Windows computer name, as reported by the agent. */
  hostname: string
  /** The administrator's console label, or null when none is set. */
  displayName: string | null
  /**
   * Whether the agent has checked in recently enough to be considered
   * reachable. Same definition as the device list — the server computes both,
   * so the UI never invents its own staleness threshold.
   */
  isOnline: boolean
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  enrolledAt: string
  machineIdentifier: string
  loggedOnUser: string | null
  inventoryCollectedAt: string | null
  inventoryRefreshPending: boolean
  hardware: DeviceHardware | null
  networkInterfaces: DeviceNetworkInterface[]
  localUsers: DeviceLocalUser[]
  localGroups: DeviceLocalGroup[]
  software: DeviceSoftwareItem[]
  securityPosture: SecurityPosture | null
  services: DeviceServiceRow[]
  processes: DeviceProcessRow[]
  windowsUpdate: DeviceUpdateDetail | null
}

export interface DeviceUpdateHistoryRow {
  title: string
  date: string | null
  operation: string
  result: string
}
export interface DeviceUpdateDetail {
  rebootRequired: boolean
  failedUpdateCount: number
  collectedAt: string
  history: DeviceUpdateHistoryRow[]
}

export interface DeviceServiceRow {
  name: string
  displayName: string
  status: string
  startMode: string
}
export interface DeviceProcessRow {
  processId: number
  name: string
  workingSetBytes: number
  executablePath: string | null
  collectedAt: string
}

export interface DeviceTaskItem {
  id: string
  type: string
  status: string
  createdByDisplay: string
  createdAt: string
  deliveredAt: string | null
  completedAt: string | null
  resultMessage: string | null
}

export async function controlService(
  deviceId: string,
  serviceName: string,
  action: 'Start' | 'Stop' | 'Restart',
): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/control-service`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ serviceName, action }),
    },
  )
}

export function getDeviceTasks(deviceId: string): Promise<DeviceTaskItem[]> {
  return request<DeviceTaskItem[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/tasks`)
}

export async function queueDeviceAction(
  deviceId: string,
  action: 'restart' | 'shutdown' | 'lock' | 'signout',
): Promise<{ taskId: string }> {
  // The taskId is the point: creating the task is not the outcome, and the id
  // is what lets the UI follow the task to what actually happened on Windows.
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/${action}`,
    { method: 'POST' },
  )
}

/**
 * Queues termination of one process, guarded by the image name the
 * administrator saw. If the PID has been reused by a different executable by
 * the time the agent acts, the agent refuses rather than killing a stranger.
 */
export async function terminateProcess(
  deviceId: string,
  processId: number,
  expectedImageName: string,
): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/terminate-process`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ processId, expectedImageName }),
    },
  )
}

/**
 * Cancels a task that is still Queued. The server refuses once the task has
 * been delivered -- the agent may already be acting on it, and a cancellation
 * that stops nothing would be a lie. Authorization is per task type: you may
 * cancel exactly what you are permitted to queue.
 */
export async function cancelDeviceTask(deviceId: string, taskId: string): Promise<void> {
  await request<void>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/tasks/${encodeURIComponent(taskId)}/cancel`,
    { method: 'POST' },
  )
}

export interface FleetTaskItem {
  id: string
  deviceId: string
  deviceHostname: string
  deviceDisplayName: string | null
  type: string
  status: string
  createdByDisplay: string
  createdAt: string
  deliveredAt: string | null
  completedAt: string | null
  resultMessage: string | null
}

export interface FleetTaskPage {
  items: FleetTaskItem[]
  totalCount: number
  page: number
  pageSize: number
}

export function getRecentTasks(
  page: number,
  pageSize: number,
  status?: string,
): Promise<FleetTaskPage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (status) params.set('status', status)
  return request<FleetTaskPage>(`/admin/v1/tasks?${params}`)
}

export interface SoftwareTitle {
  name: string
  version: string | null
  publisher: string | null
  installCount: number
}
export interface SoftwareTitlePage {
  items: SoftwareTitle[]
  totalCount: number
  page: number
  pageSize: number
}
export interface DeviceSoftwareItem {
  name: string
  version: string | null
  publisher: string | null
  installDate: string | null
  architecture: string | null
}

export function getSoftwareTitles(page: number, pageSize: number, search: string, publisher: string): Promise<SoftwareTitlePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim()) params.set('search', search.trim())
  if (publisher) params.set('publisher', publisher)
  return request<SoftwareTitlePage>(`/admin/v1/software?${params}`)
}
export function getSoftwarePublishers(): Promise<string[]> {
  return request<string[]>('/admin/v1/software/publishers')
}

export interface SecurityPosture {
  defenderAntivirusEnabled: boolean | null
  defenderRealtimeProtectionEnabled: boolean | null
  defenderSignatureAgeDays: number | null
  firewallDomainEnabled: boolean | null
  firewallPrivateEnabled: boolean | null
  firewallPublicEnabled: boolean | null
  secureBootEnabled: boolean | null
  tpmPresent: boolean | null
  tpmEnabled: boolean | null
  tpmSpecVersion: string | null
  bitLockerSystemDriveStatus: string | null
  localAdministratorCount: number | null
  collectedAt: string
  complianceScore: number | null
}
export interface DeviceSecuritySummary {
  deviceId: string
  hostname: string
  complianceScore: number | null
  defenderEnabled: boolean | null
  firewallEnabled: boolean | null
  secureBootEnabled: boolean | null
  tpmEnabled: boolean | null
  bitLockerSystemDriveStatus: string | null
  localAdministratorCount: number | null
  collectedAt: string
}
export interface SecurityOverview {
  summary: {
    devicesReporting: number
    averageScore: number | null
    healthy: number
    needsAttention: number
    critical: number
  }
  devices: DeviceSecuritySummary[]
}
export function getSecurityOverview(): Promise<SecurityOverview> {
  return request<SecurityOverview>('/admin/v1/security/overview')
}

export interface PolicyRow {
  id: string
  type: string
  name: string
  description: string
  isEnabled: boolean
  currentVersionNumber: number
  compliant: number
  nonCompliant: number
  unknown: number
}
export interface PolicyComplianceRow {
  deviceId: string
  hostname: string
  state: string
  policyVersionNumber: number
  evaluatedAt: string
  deviations: string[] | null
}
export function getPolicies(): Promise<PolicyRow[]> {
  return request<PolicyRow[]>('/admin/v1/policies')
}
export function getPolicyCompliance(policyId: string): Promise<PolicyComplianceRow[]> {
  return request<PolicyComplianceRow[]>(`/admin/v1/policies/${encodeURIComponent(policyId)}/compliance`)
}
export async function createScreenLockPolicy(name: string, description: string, maxTimeoutSeconds: number): Promise<void> {
  const r = await fetch('/api/admin/v1/policies', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ type: 'ScreenLockTimeout', name, description, maxTimeoutSeconds }),
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Create policy failed', r.headers.get('X-Correlation-Id'))
}
export async function assignPolicy(policyId: string, deviceId: string): Promise<void> {
  const r = await fetch(`/api/admin/v1/policies/${encodeURIComponent(policyId)}/assign`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ deviceId }),
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Assign policy failed', r.headers.get('X-Correlation-Id'))
}

export interface GroupRow { id: string; name: string; description: string; type: string; memberCount: number }
export interface GroupMember { id: string; hostname: string; status: string }
export function getGroups(): Promise<GroupRow[]> { return request<GroupRow[]>('/admin/v1/groups') }
export function getGroupMembers(groupId: string): Promise<GroupMember[]> {
  return request<GroupMember[]>(`/admin/v1/groups/${encodeURIComponent(groupId)}/members`)
}
export async function createGroup(name: string, description: string): Promise<void> {
  const r = await fetch('/api/admin/v1/groups', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify({ name, description }) })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Create group failed', r.headers.get('X-Correlation-Id'))
}
export async function addGroupMember(groupId: string, deviceId: string): Promise<void> {
  const r = await fetch(`/api/admin/v1/groups/${encodeURIComponent(groupId)}/members`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' }, body: JSON.stringify({ deviceId }) })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Add member failed', r.headers.get('X-Correlation-Id'))
}

export function getDevice(deviceId: string): Promise<DeviceDetail> {
  return request<DeviceDetail>(`/admin/v1/devices/${encodeURIComponent(deviceId)}`)
}

export async function requestInventoryRefresh(deviceId: string): Promise<void> {
  const response = await fetch(`/api/admin/v1/devices/${encodeURIComponent(deviceId)}/refresh-inventory`, {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }
  if (!response.ok) {
    throw new ApiError(response.status, 'Inventory refresh request failed', response.headers.get('X-Correlation-Id'))
  }
}

// ---------------------------------------------------------------- auth

export interface CurrentUser {
  userId: string
  email: string
  displayName: string
  permissions: string[]
}

export async function login(email: string, password: string): Promise<CurrentUser> {
  const response = await fetch('/api/admin/v1/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ email, password }),
  })

  if (!response.ok) {
    throw new ApiError(response.status, 'Sign-in failed', response.headers.get('X-Correlation-Id'))
  }

  // The HttpOnly session cookie was set by this response; the sessionToken field
  // in the body exists for non-browser clients and is deliberately not stored.
  const body = (await response.json()) as {
    userId: string
    email: string
    displayName: string
    permissions: string[]
  }

  return {
    userId: body.userId,
    email: body.email,
    displayName: body.displayName,
    permissions: body.permissions,
  }
}

export async function logout(): Promise<void> {
  await fetch('/api/admin/v1/auth/logout', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
}

export function getCurrentUser(): Promise<CurrentUser> {
  return request<CurrentUser>('/admin/v1/auth/me')
}

export function getReadiness(): Promise<HealthReport> {
  // /health/ready returns 503 when unhealthy, which `request` would treat as an
  // error; a health page wants the body either way, so fetch it directly.
  return fetch('/api/health/ready', { headers: { Accept: 'application/json' } }).then(
    async (response) => (await response.json()) as HealthReport,
  )
}

export interface DeviceUpdateSummaryRow {
  deviceId: string
  hostname: string
  rebootRequired: boolean
  failedUpdateCount: number
  collectedAt: string
}
export interface UpdateOverview {
  summary: {
    devicesReporting: number
    rebootPending: number
    withFailedUpdates: number
  }
  devices: DeviceUpdateSummaryRow[]
}
export function getUpdateOverview(): Promise<UpdateOverview> {
  return request<UpdateOverview>('/admin/v1/updates/overview')
}

export interface PackageRow {
  id: string
  name: string
  version: string
  publisher: string | null
  type: string
  sha256: string
  fileName: string
  sizeBytes: number
  msiProductCode: string
  requiredSignerSubject: string | null
  isWithdrawn: boolean
  createdByDisplay: string
  createdAt: string
}

export function getPackages(): Promise<PackageRow[]> {
  return request<PackageRow[]>('/admin/v1/packages/')
}

/** SHA-256 of a file as lowercase hex, computed in the browser. */
export async function sha256Hex(file: File): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', await file.arrayBuffer())
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('')
}

export interface NewPackageMeta {
  name: string
  version: string
  publisher?: string
  msiProductCode: string
  requiredSignerSubject?: string
}

export async function uploadPackage(file: File, meta: NewPackageMeta): Promise<void> {
  const sha256 = await sha256Hex(file)
  const form = new FormData()
  form.append('file', file, file.name)
  form.append('name', meta.name)
  form.append('version', meta.version)
  if (meta.publisher) form.append('publisher', meta.publisher)
  form.append('sha256', sha256)
  form.append('msiProductCode', meta.msiProductCode)
  if (meta.requiredSignerSubject) form.append('requiredSignerSubject', meta.requiredSignerSubject)

  const r = await fetch('/api/admin/v1/packages/', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    body: form,
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Package upload failed', r.headers.get('X-Correlation-Id'))
}

export async function withdrawPackage(packageId: string): Promise<void> {
  await request(`/admin/v1/packages/${encodeURIComponent(packageId)}/withdraw`, { method: 'POST' })
}

export async function deployPackageToDevice(packageId: string, deviceId: string): Promise<void> {
  await request(`/admin/v1/packages/${encodeURIComponent(packageId)}/deploy`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceId }),
  })
}

export async function offboardDevice(deviceId: string): Promise<void> {
  await request(`/admin/v1/devices/${encodeURIComponent(deviceId)}/offboard`, { method: 'POST' })
}

export async function reactivateDevice(deviceId: string): Promise<void> {
  await request(`/admin/v1/devices/${encodeURIComponent(deviceId)}/reactivate`, { method: 'POST' })
}

export interface FleetReport {
  devices: { total: number; online: number; offline: number; retired: number }
  security: { devicesReporting: number; averageScore: number | null; needsAttention: number; critical: number }
  updates: { devicesReporting: number; rebootPending: number; withFailedUpdates: number }
  policies: { enabledPolicies: number; nonCompliantResults: number }
  tasks: { queued: number; delivered: number; succeeded: number; failed: number; expired: number; cancelled: number }
  activePackages: number
}

export function getFleetReport(): Promise<FleetReport> {
  return request<FleetReport>('/admin/v1/reports/summary')
}

// ---- Windows local account management (Device -> Users / Groups) ----

export interface LocalUserRow {
  sid: string
  name: string
  fullName: string | null
  description: string | null
  enabled: boolean
  passwordRequired: boolean
  passwordExpires: boolean
  lastLogon: string | null
  isLocalAdministrator: boolean
  collectedAt: string
}

export interface LocalGroupRow {
  sid: string
  name: string
  description: string | null
  memberCount: number
  isAdministrators: boolean
  members: { name: string; sid: string | null; memberType: string }[] | null
  collectedAt: string
}

export function getLocalUsers(deviceId: string): Promise<LocalUserRow[]> {
  return request<LocalUserRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users`)
}

export function getLocalGroups(deviceId: string): Promise<LocalGroupRow[]> {
  return request<LocalGroupRow[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-groups`)
}

/** Every mutation returns the queued task id so the UI can follow it to completion. */
export interface QueuedTask { taskId: string; status: string }

async function localAccountAction<T = QueuedTask>(
  path: string,
  method: 'POST' | 'DELETE',
  body?: unknown,
): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: {
      Accept: 'application/json',
      'X-Requested-With': 'XMLHttpRequest',
      ...(body ? { 'Content-Type': 'application/json' } : {}),
    },
    ...(body ? { body: JSON.stringify(body) } : {}),
  })

  if (response.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))

  if (!response.ok) {
    // A safety rule (last administrator, protected account) answers 409 with a
    // human-readable reason; surface it rather than a generic failure.
    let detail: string | null = null
    try {
      const problem = await response.json()
      detail = problem?.title ?? problem?.detail ?? null
    } catch {
      detail = null
    }
    throw new ApiError(response.status, detail ?? `Request failed with HTTP ${response.status}`,
      response.headers.get('X-Correlation-Id'))
  }

  return (await response.json()) as T
}

export function changeAccountType(
  deviceId: string, sid: string, accountType: 'Administrator' | 'StandardUser',
): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/change-account-type`,
    'POST', { accountType })
}

export function setLocalUserEnabled(deviceId: string, sid: string, enabled: boolean): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/${enabled ? 'enable' : 'disable'}`,
    'POST')
}

export interface UserConfigurationProfile {
  key: string
  displayName: string
  description: string
  accountType: 'StandardUser' | 'Administrator'
  enabled: boolean
  mustChangePasswordAtNextLogon: boolean
  additionalGroups: string[]
  grantsAdministrator: boolean
}

export interface ProfileCatalog {
  profiles: UserConfigurationProfile[]
  /**
   * Groups that may be assigned on THIS device: the policy allow-list intersected
   * with the groups the device reported. Not every Windows edition has every group
   * in the policy, so this is what the operator may actually pick.
   */
  permittedAdditionalGroups: string[]
  /** The unfiltered policy allow-list, so the UI can explain what is missing and why. */
  policyAdditionalGroups: string[]
  /** False when the device has never reported its groups; the full allow-list is offered then. */
  deviceGroupsKnown: boolean
  /** Whether this operator may create an administrator. The server re-checks on submit. */
  canGrantAdministrator: boolean
}

export function getUserProfiles(deviceId: string): Promise<ProfileCatalog> {
  return request<ProfileCatalog>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-user-profiles`)
}

export interface CreateLocalUserBody {
  username: string
  fullName?: string
  description?: string
  password: string
  enabled: boolean
  mustChangePasswordAtNextLogon: boolean
  accountType: 'StandardUser' | 'Administrator'
  additionalGroups: string[]
  profileKey?: string
}

export function createLocalUser(deviceId: string, body: CreateLocalUserBody): Promise<QueuedTask> {
  return localAccountAction(`/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users`, 'POST', body)
}

export function deleteLocalUser(deviceId: string, sid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}`, 'DELETE')
}

export function resetLocalUserPassword(deviceId: string, sid: string, password: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/reset-password`,
    'POST', { password })
}

export function forceLocalUserPasswordChange(deviceId: string, sid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-users/${encodeURIComponent(sid)}/force-password-change`,
    'POST')
}

export function addLocalGroupMember(deviceId: string, groupSid: string, memberSid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-groups/${encodeURIComponent(groupSid)}/members`,
    'POST', { memberSid })
}

export function removeLocalGroupMember(deviceId: string, groupSid: string, memberSid: string): Promise<QueuedTask> {
  return localAccountAction(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/local-groups/${encodeURIComponent(groupSid)}/members/${encodeURIComponent(memberSid)}`,
    'DELETE')
}

// ---------------------------------------------------------------------------
// Pending agent enrollment
//
// A Windows PC that has installed the MSI asks to be managed and then waits.
// Nothing is issued until an administrator approves it here, so this is the
// authorization gate for the whole enrollment flow.
//
// Everything below is identity and state. The proof secret, the sealed
// enrollment token and the device credential are all server-side only and are
// never sent to the browser -- there is deliberately no field for them here.
// ---------------------------------------------------------------------------

export interface PendingEnrollment {
  /** SHA-256 the agent published. Identifies the request; it is not a credential. */
  requestId: string
  hostname: string
  /** SMBIOS UUID. Lets an administrator tell a re-enrolling machine from a new one. */
  machineIdentifier: string
  operatingSystem: string | null
  agentVersion: string
  requestedAt: string
  expiresAt: string
  status: 'Pending' | 'Approved' | 'Rejected'
  /** Display name of the deciding administrator, once decided. */
  approvedBy: string | null
}

export interface EnrollmentDecision {
  requestId: string
  status: string
  hostname: string
  message: string
}

export function getPendingEnrollments(): Promise<PendingEnrollment[]> {
  return request<PendingEnrollment[]>('/admin/v1/enrollments/pending')
}

export function approveEnrollment(requestId: string): Promise<EnrollmentDecision> {
  return request<EnrollmentDecision>(
    `/admin/v1/enrollments/${encodeURIComponent(requestId)}/approve`,
    { method: 'POST' },
  )
}

export function rejectEnrollment(requestId: string): Promise<EnrollmentDecision> {
  return request<EnrollmentDecision>(
    `/admin/v1/enrollments/${encodeURIComponent(requestId)}/reject`,
    { method: 'POST' },
  )
}

/**
 * What a device should be called on screen: the administrator's label if one is
 * set, otherwise the machine's real hostname. Mirrors the server's fallback so
 * the two can never disagree.
 */
export function deviceName(device: { hostname: string; displayName: string | null }): string {
  return device.displayName ?? device.hostname
}

/**
 * Sets or clears the console display name for a device.
 *
 * Pass null or a blank string to clear it, which restores the hostname. This
 * changes a label in this console only -- it does not rename Windows, and
 * nothing about it reaches the endpoint.
 */
export async function setDeviceDisplayName(
  deviceId: string,
  displayName: string | null,
): Promise<void> {
  await request<void>(`/admin/v1/devices/${deviceId}/display-name`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ displayName }),
  })
}

// ---- Agent releases (Milestone 10) -----------------------------------------

export interface AgentReleaseRow {
  id: string
  version: string
  platform: string
  architecture: string
  fileName: string
  sha256: string
  signerSubject: string | null
  releaseNotes: string | null
  contentSizeBytes: number
  status: 'Draft' | 'Published' | 'Revoked'
  createdByDisplay: string
  createdAt: string
  publishedAt: string | null
  revokedAt: string | null
}

export interface LatestAgentRelease {
  available: boolean
  releaseId?: string
  version?: string
  architecture?: string
  fileName?: string
  sha256?: string
  signerSubject?: string | null
  releaseNotes?: string | null
  sizeBytes?: number
  publishedAt?: string | null
}

export function getAgentReleases(): Promise<AgentReleaseRow[]> {
  return request<AgentReleaseRow[]>('/admin/v1/agent-releases/')
}

export function getLatestAgentRelease(): Promise<LatestAgentRelease> {
  return request<LatestAgentRelease>('/admin/v1/agent-releases/latest')
}

/**
 * Uploads a new agent MSI as a Draft release. The SHA-256 is computed here in
 * the browser and re-computed by the server as it stores the bytes -- a
 * mismatch anywhere discards the upload.
 */
export async function createAgentRelease(
  file: File,
  meta: { version: string; releaseNotes?: string; signerSubject?: string },
): Promise<void> {
  const sha256 = await sha256Hex(file)
  const form = new FormData()
  form.append('file', file, file.name)
  form.append('version', meta.version)
  form.append('sha256', sha256)
  if (meta.releaseNotes) form.append('releaseNotes', meta.releaseNotes)
  if (meta.signerSubject) form.append('signerSubject', meta.signerSubject)

  const r = await fetch('/api/admin/v1/agent-releases/', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
    body: form,
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Release upload failed', r.headers.get('X-Correlation-Id'))
}

export async function publishAgentRelease(releaseId: string): Promise<void> {
  await request<void>(`/admin/v1/agent-releases/${encodeURIComponent(releaseId)}/publish`, { method: 'POST' })
}

export async function revokeAgentRelease(releaseId: string): Promise<void> {
  await request<void>(`/admin/v1/agent-releases/${encodeURIComponent(releaseId)}/revoke`, { method: 'POST' })
}

/**
 * Starts a native browser download of a release's MSI.
 *
 * Deliberately NOT fetch+blob. A 29 MB blob fetch shows no progress and no
 * busy state, so the button gets clicked repeatedly -- and six in-flight
 * fetches consume the browser's entire HTTP/1.1 connection pool for the
 * origin, queueing every poll and navigation behind them until the transfers
 * finish. The page freezes for a minute while the server serves each request
 * perfectly; that reads as "the dashboard is down". A plain navigation hands
 * the transfer to the browser's download manager instead: real progress UI,
 * no page-held connection, no memory buffering. Authentication rides the
 * HttpOnly __Host- session cookie, and the server answers with
 * Content-Disposition: attachment, so the SPA never navigates away.
 */
export function downloadAgentRelease(releaseId: string, _fileName: string): void {
  const anchor = document.createElement('a')
  anchor.href = `/api/admin/v1/agent-releases/${encodeURIComponent(releaseId)}/download`
  // The server names the file via Content-Disposition; the attribute only
  // marks the click as a download so the SPA route never changes.
  anchor.download = ''
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
}

/** Queues a device's agent self-update to a published release. */
export async function updateDeviceAgent(
  deviceId: string,
  releaseId: string,
): Promise<{ taskId: string }> {
  return request<{ taskId: string }>(
    `/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/update-agent`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ releaseId }),
    },
  )
}
