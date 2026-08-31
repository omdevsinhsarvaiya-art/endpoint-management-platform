import { describe, expect, it, vi, afterEach } from 'vitest'
import {
  getBitLockerEscrowAttempts,
  getBitLockerEscrows,
  getDeviceBitLockerReadiness,
  getDeviceBitLockerVolumes,
  resetEscrowAttempts,
} from '../../api/client'

/**
 * Every request must reach the API with exactly one `/api` prefix.
 *
 * This exists because of a real production failure. The shared `request()` helper
 * prepends `/api` itself, so its callers pass a bare path -- but a handful of
 * functions use `fetch` directly and must include the prefix themselves. Two new
 * escrow functions were written in the `fetch` style while calling `request()`,
 * producing `/api/api/...`. The proxy stripped one segment, the API saw a path it
 * does not route, and returned 404.
 *
 * The symptom was disproportionate to the typo: the BitLocker panel loads four
 * endpoints in one `Promise.all`, so a single 404 blanked the whole page with
 * "BitLocker information could not be loaded" while three endpoints returned 200.
 *
 * These tests assert the property directly -- one prefix, at the front -- rather
 * than the spelling of any individual route.
 */

const DEVICE = '01a01bc4-aa58-7bf2-bcfd-9616efcdd614'
const ATTEMPT = '0192f3a1-1111-7000-8000-aaaabbbbcccc'

/** Captures the URL a client call requests, and answers with an empty success. */
function captureUrl(): { urls: string[] } {
  const urls: string[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL) => {
      urls.push(typeof input === 'string' ? input : input.toString())

      return new Response(JSON.stringify({}), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      })
    }),
  )

  return { urls }
}

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('API paths carry exactly one /api prefix', () => {
  /**
   * The regression itself. Before the fix this produced `/api/api/admin/v1/...`.
   */
  it.each([
    ['getBitLockerEscrowAttempts', () => getBitLockerEscrowAttempts(DEVICE)],
    ['resetEscrowAttempts', () => resetEscrowAttempts(ATTEMPT)],
    ['getBitLockerEscrows', () => getBitLockerEscrows(DEVICE)],
    ['getDeviceBitLockerVolumes', () => getDeviceBitLockerVolumes(DEVICE)],
    ['getDeviceBitLockerReadiness', () => getDeviceBitLockerReadiness(DEVICE)],
  ])('%s requests a singly-prefixed path', async (_name, call) => {
    const { urls } = captureUrl()

    await call().catch(() => {
      // The shape of the response is irrelevant here; only the URL is under test.
    })

    const url = urls[0]

    expect(url).toBeDefined()
    expect(url.startsWith('/api/')).toBe(true)
    expect(url).not.toContain('/api/api/')

    // Counted rather than pattern-matched, so a prefix appearing anywhere later
    // in the path is caught too.
    expect(url.split('/api/').length - 1).toBe(1)
  })

  /**
   * The four calls the BitLocker panel makes together. One bad path blanks the
   * page, so they are asserted as a set rather than individually.
   */
  it('every endpoint the BitLocker panel loads is addressable', async () => {
    const { urls } = captureUrl()

    await Promise.all([
      getDeviceBitLockerReadiness(DEVICE).catch(() => null),
      getDeviceBitLockerVolumes(DEVICE).catch(() => null),
      getBitLockerEscrows(DEVICE).catch(() => null),
      getBitLockerEscrowAttempts(DEVICE).catch(() => null),
    ])

    expect(urls).toHaveLength(4)

    for (const url of urls) {
      expect(url).not.toContain('/api/api/')
      expect(url).toMatch(/^\/api\/admin\/v1\//)
    }

    // And each one names a distinct resource, so a copy-paste error that pointed
    // two calls at the same route would fail here.
    expect(new Set(urls).size).toBe(4)
  })

  it('reset-attempts targets the attempt resource, not the escrow resource', async () => {
    const { urls } = captureUrl()

    await resetEscrowAttempts(ATTEMPT).catch(() => null)

    // Keyed on attempt id deliberately: a protector that exhausted its retries has
    // no escrow row, so an escrow-addressed route could not reach it.
    expect(urls[0]).toBe(`/api/admin/v1/bitlocker-escrow-attempts/${ATTEMPT}/reset`)
  })

  it('the attempts listing is device-scoped', async () => {
    const { urls } = captureUrl()

    await getBitLockerEscrowAttempts(DEVICE).catch(() => null)

    expect(urls[0]).toBe(`/api/admin/v1/devices/${DEVICE}/bitlocker-escrow-attempts`)
  })
})
