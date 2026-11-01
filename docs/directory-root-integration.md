# Directory-root index integration

Every configured search root opens its own `FileIndex` using `FileIndexOptions.Root`.
Retained repositories outside the configured roots add exact-root views. A retained
repository inside a configured search root is covered by that view. Prepared discovery
owners are reused by root identity; watch arming requires the exact requested root set.

`ProducerSelection.Automatic` lets MFTLib select MFT or enumeration. Eligibility failures
can select enumeration, including an unavailable broker or declined elevation. A failure
during an MFT scan remains a failure. `--no-mft` selects uncached `WalkOnly` opens.
`RootStatus.Producer` and `MftUnavailable` describe what happened.

All names are stored. MFT blocks describe whole-volume metadata; queries on a directory
view are implicitly limited by MFTLib. GitWizard queries `.git` and `HEAD`, applies
candidate admission and validates repository structure. The application cache tag is
`GWMI` version 3. MFTLib validates scan shape independently, including `IncludeFreed`.

Directory walks prune `.git` contents and apply the configured ignored, hidden and
temporary-directory policy. The same options-configuration dependency reaches session,
default discovery, enumeration and watch opens. Tests apply `SyntheticVolumeFacts.Apply`
to the actual options and supply owned roots. Auxiliary clone roots have a dependency
provider so inventory tests can supply owned clone directories.

MFTLib owns alias resolution. GitWizard reads opened indexes' root paths and projects
configured admission policy into those coordinates. It does not resolve junctions,
symbolic links, mounted folders or substituted drives independently. Root keys remain
the caller's keys even when result paths use the resolved spelling. Status lookup uses
the configured key, but a successful root observation names the resolved root, so a
partial reconciliation prunes a stale resolved path that the root rescanned. A failed
observation keeps the configured root, and saved entries under it are retained.

Directory-root volume identity comes from watch notifications and checkpoint-loss
observations. A configured path's letter is not a volume resolver. Each supported view
is armed; sibling views on one volume must all catch up before that volume reports ready.
Per-root event versions are checked before projecting a volume-level readiness version.
Overlapping views route a change through one outermost participating root, considering
both paths of a rename. Event subscriptions detach before stop/disposal.

Active-session serials come from `RootStatus.VolumeSerial`. A null serial prints
`unavailable` and cannot match a dismissed journal warning. Cached-file inventory keeps
the serial recorded in the cached file. Indexes are disposed before their broker session.

At the pinned dependency revision, eligible views with one source instance, volume,
serial, normalized cache directory and cache tag share one volume scan. GitWizard opens
all roots in a session with one source instance. A view with a different share key or
contested canonical ownership can still use private fallback.
