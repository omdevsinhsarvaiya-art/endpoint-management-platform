# ADR-0014: Application discovery reports evidence and confidence, not just registry entries

Status: accepted

## Context

Until agent 1.8.0 the software inventory was the Windows uninstall registry and
nothing else. That is one of several places Windows records software, and it is
the one that MSIX and AppX packages -- every Store app, every packaged desktop
application such as Slack -- do not write to. A machine could have Slack
installed and running and report no Slack. Portable executables, which have no
installation record anywhere, were invisible on principle.

The obvious fix is more sources. The problem that creates is that sources
disagree: an uninstall key, a package manifest, a Start Menu shortcut, a file's
version resource and a running process each call the same application
something different, and several of them can point at the same executable.
Adding sources without a rule for what counts as one application, and for what
each source is allowed to claim, produces an inventory that invents
applications and duplicates real ones.

The agent also runs as LocalSystem, and every new source is a new read of the
machine. What it must never do is fixed by ADR-0005 and by the milestone's
constraints: no shell, no PowerShell, no process launch, no WinRT projection
(the target framework stays `net10.0-windows`), no full-disk scan, no loading of
unloaded user hives, and no action taken on the strength of what is found.

## Decision

Discovery is a pipeline of *evidence* → *identity* → *merged applications* →
*wire rows*, with three rules that hold everywhere.

**Sources report evidence; the merger decides what it adds up to.** A source
reports what it itself saw as a `SoftwareEvidence` record and asserts nothing
about installation. Sources are either *authoritative* -- an installation
record: the uninstall registry, a Windows Installer product, an MSIX/AppX
package registration -- or *supplementary*: an App Paths alias, a Start Menu
shortcut, an executable's own metadata, a running process. Supplementary
evidence sharpens identity and location; it never on its own makes an
application installed.

**Identity is not a display name.** An application's `StableKey` is the
identity that survives an update: a package family name, a Windows Installer
upgrade code, or -- where Windows offers no identifier -- a composed key of who
vouches for it, what it calls itself and where it lives. A `VersionKey` is what
changed. The merger folds installation records only where they were always one
inventory row (name, version, publisher, scope, account) *and* share a stable
key, so nothing folds that the inventory kept apart; two installed versions of
one product stay two rows. Folding an update into its predecessor by stable key
alone is deliberately not done here, because the row identity includes the
version and changing that changes what every device reports.

**Confidence gates what is a row.** `Installed` needs at least one installation
record. `Observed` is a running process with no installation record -- a
portable application -- and is a row, labelled as such on the wire and in the
console. `Referenced` (a shortcut or alias pointing at something) and
`Transient` (an installer, updater or a run from a temp folder) are kept as
applications with their evidence and are never rows.

Consequences of those rules that are themselves decisions:

- Supplementary evidence attaches to an installation only by containment (its
  executable is inside the installation's directory), or, for an installation
  with no recorded directory, by an exact name match with agreeing publisher
  and scope, unambiguously. An adopted directory must be one Force Stop could
  act on; two different candidate directories withdraw the adoption. A row's
  install location is only ever *filled* from empty by discovery, never changed.
- An observed executable reports its directory as its location only when that
  directory is its own. A profile root, Downloads, Desktop, Documents or a temp
  folder is never an install location, because the location is what Force Stop
  terminates processes under.
- Everything is collected and categorised; nothing is dropped for being a
  framework, an inbox app or a component. The category is a label the console
  filters on, and its default view hides those three. A row an older agent
  reported has no category and is shown in every view.
- A signature status is presence, not trust: `Signed` means an embedded
  signature named this subject, or Windows verified a package as this
  publisher at deployment. It does not say the certificate chains to a trusted
  root, and nothing in the platform acts on it as if it did.
- Superseded package registrations -- versions a later version replaced, which
  the AppModel repository keeps -- are filtered by the state repository's
  package index, and where that cannot be read, by a package root whose
  manifest is for another version.
- Processes under the Windows directory are the process inventory's subject,
  not software discovery's, and are not reported as software. Processes whose
  image path cannot be read are skipped, not guessed.
- Every read is bounded: shortcuts per root, bytes per shortcut, manifests per
  hive, bytes per manifest, executables described per collection, evidence
  entries per row. A source that fails costs its own evidence and nothing else.

The wire contract gains optional fields (identity kind, stable and version
keys, confidence, category, package names, upgrade code, executable path,
signer, signature status) and a bounded evidence list per row. The nine fields
the report always carried are unchanged, row for row, which the agent's tests
assert against the pre-milestone pipeline rather than assume. The Agent API
refuses a value the contract does not name rather than storing it.

## Consequences

- Slack, Store apps and Windows' own packaged applications are inventory rows,
  installed for the account they are deployed to, with the directory Force
  Stop acts on. On the reference machine the report went from 33 rows to 185:
  the same 33, 133 package rows, and 19 observed executables.
- A portable application is visible as "running only", with the executable
  that was seen, and can be told apart from an installation.
- The console can answer "why does Techsara believe this application exists"
  from stored evidence rather than inference.
- Force Stop is unchanged in mechanism and can now be offered for packaged
  applications; it is not offered for an observed executable that lives in a
  shared folder.
- A future application-blocking capability has a stable identity to name and a
  path an executable was seen at. Neither is acted on by this decision.
- The software section of an inventory report is larger (216 KB for 185 rows
  on the reference machine, against 103 KB for the same rows in the legacy
  shape), within the existing report limits.
- Adding a discovery value -- a category, a source, a confidence -- is a change
  to the contract first and the agent second, enforced by a test that every
  name the agent can produce is one the contract allows.
