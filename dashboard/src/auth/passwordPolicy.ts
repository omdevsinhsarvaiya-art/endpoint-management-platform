/**
 * A client-side mirror of the server's password policy.
 *
 * This exists to tell someone what is wrong *while they are typing*, not to
 * decide whether the password is acceptable. The server re-validates every
 * request and is the only authority; this file is a convenience that saves a
 * round trip and a rejected form. If the two ever disagree, the server wins and
 * the person sees the server's message.
 *
 * Kept in step with `PasswordPolicy` in the domain. The rule weights length over
 * composition -- a 12-character floor, no digit/symbol/case requirements --
 * because composition rules reliably produce "Password1!" while length is the
 * property that actually costs an attacker something.
 */

/** Matches PasswordPolicy.MinimumLength. */
export const PASSWORD_MINIMUM_LENGTH = 12

/** Matches PasswordPolicy.MaximumLength; bounds hasher work rather than discouraging passphrases. */
export const PASSWORD_MAXIMUM_LENGTH = 256

/**
 * Returns a reason the password is unacceptable, or null when it looks fine.
 *
 * Returns the first problem rather than a list: the reader is mid-way through
 * retyping a password, and a wall of simultaneous complaints is harder to act on
 * than one instruction.
 */
export function validateNewPassword(password: string): string | null {
  if (password.trim().length === 0) {
    return 'A password is required.'
  }

  // Array.from, not .length: a string's length counts UTF-16 code units, so an
  // emoji or an astral-plane character would count as two and let a shorter
  // password through than the server will accept.
  const characters = Array.from(password).length

  if (characters < PASSWORD_MINIMUM_LENGTH) {
    return `The password must be at least ${PASSWORD_MINIMUM_LENGTH} characters.`
  }

  if (characters > PASSWORD_MAXIMUM_LENGTH) {
    return `The password must be at most ${PASSWORD_MAXIMUM_LENGTH} characters.`
  }

  if (new Set(Array.from(password)).size === 1) {
    return 'The password must not be a single repeated character.'
  }

  return null
}

/**
 * Everything wrong with the form, for enabling the submit button.
 *
 * The "same as current" check is a courtesy: the server refuses it too, because
 * re-setting the same password would rotate the security stamp and destroy every
 * session for no security gain.
 */
export function validateChangePasswordForm(input: {
  currentPassword: string
  newPassword: string
  confirmPassword: string
}): { newPassword: string | null; confirmPassword: string | null; canSubmit: boolean } {
  const newPassword = input.newPassword.length > 0 ? validateNewPassword(input.newPassword) : null

  const confirmPassword =
    input.confirmPassword.length > 0 && input.confirmPassword !== input.newPassword
      ? 'The passwords do not match.'
      : null

  const sameAsCurrent =
    input.newPassword.length > 0 && input.newPassword === input.currentPassword
      ? 'The new password must be different from the current one.'
      : null

  const canSubmit =
    input.currentPassword.length > 0 &&
    input.newPassword.length > 0 &&
    input.confirmPassword === input.newPassword &&
    validateNewPassword(input.newPassword) === null &&
    sameAsCurrent === null

  return { newPassword: newPassword ?? sameAsCurrent, confirmPassword, canSubmit }
}
