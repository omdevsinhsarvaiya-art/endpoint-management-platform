import { existsSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * The minimum agent version for Remove is one decision written down in two
 * files, and this is what stops either copy moving alone.
 *
 * The console will not offer Remove below MINIMUM_REMOVE_AGENT_VERSION
 * (src/pages/softwareView.ts); the server will not queue the task below
 * MinimumAgentVersion on the RemoveApplication entry of DeviceTaskCatalog.cs.
 * Let the console's number fall below the server's and Remove is offered on a
 * device that cannot carry it out: the operator confirms a destructive action
 * and only then reads the server's NotEligible refusal, which is precisely the
 * defect the gate was added to prevent.
 *
 * Each side already pins itself to a literal in its own suite, and that catches
 * nothing here. Raise the server's minimum for a rewritten executor and only
 * the C# suite fails; its literal is updated, both suites go green, and the
 * console keeps offering Remove on agents the server now refuses. So this reads
 * both files and compares them. Its twin lives in
 * server/tests/EndpointPlatform.Domain.Tests/Tasks/RemoveApplicationTaskCatalogTests.cs,
 * so a one-sided change fails whichever suite its author runs.
 *
 * It reads source text rather than importing, because one of the two sides is
 * C#. That is also why it sits here rather than in src/components/__tests__:
 * node:fs is deliberately absent from the browser bundle's type surface
 * (tsconfig.app.json carries no node types), and tsconfig.node.json -- which
 * this file is included by -- is the project that has them.
 */

const CATALOG = 'server/Domain/Tasks/DeviceTaskCatalog.cs'
const CONSOLE = 'dashboard/src/pages/softwareView.ts'

/**
 * The repository root, walked out of wherever vitest was started and recognised
 * by holding both halves of the comparison. Not finding it is a failure, never
 * a skip: a moved file must break this test, not silently retire it.
 */
function repositoryRoot(): string {
  let directory = process.cwd()

  while (!existsSync(join(directory, CATALOG)) || !existsSync(join(directory, CONSOLE))) {
    const parent = dirname(directory)

    if (parent === directory) {
      throw new Error(
        `No directory above ${process.cwd()} holds both ${CATALOG} and ${CONSOLE}. ` +
          'If one of them moved, point this test at its new home rather than deleting the ' +
          'check: the two constants it compares are one decision and must move together.',
      )
    }

    directory = parent
  }

  return directory
}

function repositoryFile(relativePath: string): string {
  return readFileSync(join(repositoryRoot(), relativePath), 'utf8').replace(/\r\n/g, '\n')
}

/**
 * The minimum declared by the RemoveApplication entry specifically.
 *
 * The catalogue holds several of these -- StopApplication is 1.6.0,
 * InstallDriverPackage 1.3.0 -- so the pattern is anchored on the entry and
 * `[^)]` keeps it from running past the end of that constructor call into a
 * neighbour's.
 */
function catalogMinimum(): string {
  const entry = /new\(DeviceTaskType\.RemoveApplication\b[^)]*?MinimumAgentVersion:\s*"([^"]+)"/
  const match = entry.exec(repositoryFile(CATALOG))

  if (!match) {
    throw new Error(
      `No RemoveApplication entry declaring MinimumAgentVersion was found in ${CATALOG}. ` +
        'Either the gate was removed from the server -- in which case the console must stop ' +
        'offering Remove on its own -- or the entry was rewritten and this pattern needs to ' +
        'follow it. Do not delete the check: it is what keeps the two copies of this version ' +
        'in step.',
    )
  }

  return match[1]
}

function consoleMinimum(): string {
  const declaration = /^export const MINIMUM_REMOVE_AGENT_VERSION = '([^']+)'/m
  const match = declaration.exec(repositoryFile(CONSOLE))

  if (!match) {
    throw new Error(
      `MINIMUM_REMOVE_AGENT_VERSION was not found in ${CONSOLE}. It is the console's half of ` +
        'the agent-version gate; if it was renamed or moved, point this test at it rather ' +
        'than deleting the check.',
    )
  }

  return match[1]
}

describe('the minimum agent version for Remove', () => {
  it('is the same number in the console and in the server catalogue', () => {
    const fromConsole = consoleMinimum()
    const fromCatalog = catalogMinimum()

    expect(
      fromConsole,
      `The console offers Remove from ${fromConsole} but the server refuses it below ` +
        `${fromCatalog}. These are one decision in two files -- MINIMUM_REMOVE_AGENT_VERSION ` +
        `in ${CONSOLE}, and MinimumAgentVersion on the RemoveApplication entry of ${CATALOG}, ` +
        'plus the literal each suite pins -- and both must move together. Apart, the console ' +
        'offers Remove, takes the operator confirmation for a destructive action, and the ' +
        'server then refuses the task as NotEligible.',
    ).toBe(fromCatalog)
  })
})
