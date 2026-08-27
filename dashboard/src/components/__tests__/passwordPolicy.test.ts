import { describe, expect, it } from 'vitest'
import {
  PASSWORD_MAXIMUM_LENGTH,
  PASSWORD_MINIMUM_LENGTH,
  validateChangePasswordForm,
  validateNewPassword,
} from '../../auth/passwordPolicy'

/**
 * The client-side password rules.
 *
 * These exist to tell someone what is wrong while they type. The server
 * re-validates every request and remains the only authority, so the property
 * worth testing is not "does this accept good passwords" but **does it stay in
 * step with the server** -- a client rule that is looser produces a rejected
 * form the user cannot explain, and one that is stricter silently forbids
 * passwords the platform would have accepted.
 *
 * The values below mirror `PasswordPolicy` in the domain, which has its own
 * tests. If the two drift, one of the two suites should fail.
 */

describe('the password rule', () => {
  it.each([
    'correct horse battery staple',
    'aaaaaaaaaaab',
    'Tr0ub4dor&3xyz',
    'これは長いパスワードです',
  ])('accepts %j', (password) => {
    expect(validateNewPassword(password)).toBeNull()
  })

  it.each(['', '   ', '\t'])('refuses the blank value %j', (password) => {
    expect(validateNewPassword(password)).not.toBeNull()
  })

  it.each(['short', 'elevenchars', 'Passw0rd!'])(
    'refuses %j for being below the floor, complexity notwithstanding',
    (password) => {
      expect(validateNewPassword(password)).toContain(String(PASSWORD_MINIMUM_LENGTH))
    },
  )

  /**
   * A single repeated character clears the length floor while carrying almost no
   * entropy -- the obvious way to satisfy a length rule without choosing a
   * password at all.
   */
  it.each(['aaaaaaaaaaaa', '000000000000', '....................'])(
    'refuses the repeated character %j',
    (password) => {
      expect(validateNewPassword(password)).not.toBeNull()
    },
  )

  /**
   * Counted in characters, not UTF-16 code units.
   *
   * `''.length` counts code units, so a password of emoji would score double and
   * a shorter password than the server accepts would pass here -- producing a
   * form that submits and is then rejected, with no explanation the user can act
   * on.
   */
  it('counts astral-plane characters once, as the server does', () => {
    const eleven = '🙂'.repeat(11)

    expect(eleven.length).toBe(22) // what a naive check would have seen
    expect(validateNewPassword(eleven)).not.toBeNull()
    expect(validateNewPassword('🙂'.repeat(11) + '🎉')).toBeNull()
  })

  it('refuses a password past the ceiling that bounds hasher work', () => {
    expect(validateNewPassword('a'.repeat(PASSWORD_MAXIMUM_LENGTH - 1) + 'b')).toBeNull()
    expect(validateNewPassword('a'.repeat(PASSWORD_MAXIMUM_LENGTH) + 'b')).not.toBeNull()
  })
})

describe('the form', () => {
  const good = 'a-perfectly-adequate-passphrase'

  it('cannot be submitted until every field is present and valid', () => {
    expect(
      validateChangePasswordForm({ currentPassword: '', newPassword: '', confirmPassword: '' })
        .canSubmit,
    ).toBe(false)

    expect(
      validateChangePasswordForm({
        currentPassword: 'old-one',
        newPassword: good,
        confirmPassword: good,
      }).canSubmit,
    ).toBe(true)
  })

  it('reports a mismatch against the confirm field, not the new-password field', () => {
    const r = validateChangePasswordForm({
      currentPassword: 'old-one',
      newPassword: good,
      confirmPassword: 'something-else',
    })

    expect(r.confirmPassword).not.toBeNull()
    expect(r.newPassword).toBeNull()
    expect(r.canSubmit).toBe(false)
  })

  /**
   * Re-setting the same password is refused here as a courtesy; the server
   * refuses it too, because it would rotate the security stamp and destroy every
   * session for no security gain.
   */
  it('refuses a new password identical to the current one', () => {
    const r = validateChangePasswordForm({
      currentPassword: good,
      newPassword: good,
      confirmPassword: good,
    })

    expect(r.newPassword).not.toBeNull()
    expect(r.canSubmit).toBe(false)
  })

  /**
   * Nothing is reported until there is something to report.
   *
   * Marking a field invalid before the first keystroke is finished trains people
   * to ignore the message that eventually matters.
   */
  it('says nothing about fields that are still empty', () => {
    const r = validateChangePasswordForm({
      currentPassword: 'old-one',
      newPassword: '',
      confirmPassword: '',
    })

    expect(r.newPassword).toBeNull()
    expect(r.confirmPassword).toBeNull()
    expect(r.canSubmit).toBe(false)
  })

  it('stops reporting a mismatch once the confirmation catches up', () => {
    const partial = validateChangePasswordForm({
      currentPassword: 'old-one',
      newPassword: good,
      confirmPassword: good.slice(0, 5),
    })
    expect(partial.confirmPassword).not.toBeNull()

    const complete = validateChangePasswordForm({
      currentPassword: 'old-one',
      newPassword: good,
      confirmPassword: good,
    })
    expect(complete.confirmPassword).toBeNull()
    expect(complete.canSubmit).toBe(true)
  })
})

/**
 * The client floor must equal the server's.
 *
 * Pinned as a constant rather than inferred, so a change on either side has to
 * be made deliberately on both.
 */
describe('agreement with the server', () => {
  it('uses the same minimum the domain enforces', () => {
    expect(PASSWORD_MINIMUM_LENGTH).toBe(12)
  })

  it('uses the same maximum the domain enforces', () => {
    expect(PASSWORD_MAXIMUM_LENGTH).toBe(256)
  })
})
