# First internal package release

The first private internal version was published and independently installed using the actual APR and ContractScribe repository tokens on the selected Linux target. This record captures operational evidence for [#95](https://github.com/SolusQuest/solus-agent/issues/95); milestone closure and downstream product delivery are separate decisions.

## Published version and source

All five packages were published as `0.1.0-internal.1` by the single [manual release run 38860964881](https://github.com/SolusQuest/solus-agent/actions/runs/38860964881), selected from `main` at `076bcda2fbe1da2677ac5c6fd03d75bc0fef66ad`. The lightweight [`v0.1.0-internal.1` tag](https://github.com/SolusQuest/solus-agent/tree/v0.1.0-internal.1) was independently read back at that commit. The run used the existing release workflow, one source checkout without persisted credentials, managed .NET 10 and Node 24 on Ubuntu 24.04 x64, source restore/build/test, one pack, and clean offline validation against the same five archives. It pushed those files and created the tag only after all uploads succeeded. The original run succeeded on its first attempt; it was not dispatched again.

| Package | Version | Authenticated visibility readback |
| --- | --- | --- |
| SolusAgent.Api | 0.1.0-internal.1 | Private |
| SolusAgent.Tools.Api | 0.1.0-internal.1 | Private |
| SolusAgent.Runtime.Api | 0.1.0-internal.1 | Private |
| SolusAgent.Runtime | 0.1.0-internal.1 | Private |
| SolusAgent.Providers.DeepSeek | 0.1.0-internal.1 | Private |

Release preparation passed APR's 10 cases with 23 independent child processes, Foundation's public startup/tool/provider composition, LiveSmoke's 128 offline behavioral cases with controlled transport and fresh-process restoration, and Scribe's 16 cases with 23 distinct child processes. These were the archives subsequently uploaded, not an authenticated registry-install claim.

## Actual access qualification

Before dispatch, the maintainer verified private package creation was enabled and organization default source-repository access inheritance was disabled. The repository's `SOLUS_INTERNAL_PACKAGE_ACCESS=private-no-inheritance` assertion was set only after that verification; the release additionally performed its authenticated registry/version/tag preflight.

Nevertheless, authenticated post-publication inspection found inherited repository access enabled on every new package. The maintainer explicitly disabled inheritance and read back the intended ACL for all five packages. Private visibility was observed throughout those inspections. The initial inherited-access interval is part of the evidence and is not described as never having existed; the organization setting and persistent assertion did not independently prove the actual new-package ACL.

Each package now has exactly these Actions grants: `solus-agent` Admin, `agentic-pr-review` Read, and `contract-scribe` Read. No other repository or Codespaces grants were added; no people/team grants were present in the readback. This was limited to the named five packages and consuming repositories. GitHub's documented possible fork access when private packages grant Actions access to public repositories remains an accepted boundary; current zero fork counts and contributor approval settings are not exclusion guarantees.

## Authenticated independent consumption

The private feed is `https://nuget.pkg.github.com/SolusQuest/index.json`. Existing credentials are supplied through the supported environment channel with the actual actor identity, never written into source. A safe version-pinned verification command is:

```sh
node scripts/packages/verify-consumers.ts --feed https://nuget.pkg.github.com/SolusQuest/index.json --version 0.1.0-internal.1 --scenario all --require foundation,apr,scribe,smoke-offline
```

The shared runner creates clean consumer workspaces and package caches, verifies exact dependency versions and registry provenance, and executes only safe offline defaults for `all`. No provider key or live selection is used.

| Actual token repository | Original run and qualification source | Registry result |
| --- | --- | --- |
| SolusQuest/agentic-pr-review | [38868684025](https://github.com/SolusQuest/agentic-pr-review/actions/runs/38868684025), merged workflow commit `873296314bd01ee89706255b1a9a4fd09b1a7579` | Original run and `install-proof` succeeded: APR 10 cases/23 processes, Foundation startup/tool/provider composition, 128 LiveSmoke offline cases, and Scribe 16 cases/23 processes; all four exact-version clean consumers passed |
| SolusQuest/contract-scribe | [38867603892](https://github.com/SolusQuest/contract-scribe/actions/runs/38867603892), reviewed workflow branch commit `7e87e9af8b0a1b3c1f6a367f4144191bf44f2f8d` | `solus_agent_install_proof` succeeded: 3 clean projects, 16 Scribe cases and 23 distinct child processes at exact version .1 |

The APR proof ran once from `main` after [PR #389](https://github.com/SolusQuest/agentic-pr-review/pull/389) merged. The merged workflow matched the reviewed content, used immutable action commits and only contents/read and packages/read permissions, and checked manual main invocation on x64 before checkout. Its own repository token installed all four required consumers from the registry at `.1`, using the exact public SolusAgent qualification fixtures. The original run and every step succeeded on attempt 1; the selected run head was independently read back at the merged workflow commit above.

The Scribe proof ran once with its own repository `GITHUB_TOKEN`, explicit actor, and only contents/read and packages/read permissions. Its manual invocation checked the exact repository/branch, x64, and equality of the expected head with both the actual run SHA and workflow SHA before checkout. All three action implementations were pinned to verified complete commit SHAs. It checked out the public SolusAgent consumer fixtures at `076bcda2fbe1da2677ac5c6fd03d75bc0fef66ad` without persisted credentials and installed from the authenticated registry. The original target job and every step succeeded on attempt 1.

The maintainer explicitly selected this controlled branch-only Scribe proof as the M5 installation boundary. [Scribe PR #239](https://github.com/SolusQuest/contract-scribe/pull/239) remains draft and unmerged, including its separately authorized one-line Xml 9.0.21 security patch. Linux source/fast and payload checks passed at security-correction commit `26d9d412c02a06562dbb4b9ffc0d275bb616327a`, whose production/build inputs are unchanged in the executed branch proof, but the surrounding product CI retains an existing failure rebuilding historical payload source `607643f10d9422289e378b243ce39660d2d9a881` with vulnerable 9.0.18 dependencies. The successful isolated installation job does not make that overall CI green, complete a product migration, or merge the product correction. Historical payload mapping, auditing, protection rules and immutable candidate evidence remain unchanged; the product owner retains that work.

## Evidence limits

The selected registry-consumption target is managed Linux x64 on Ubuntu 24.04 with .NET 10 and Node 24. Local macOS development observations do not broaden that support boundary. The original [bounded live qualification](m5-live-qualification.md) applies at its documented production source; the release source has no production `src/` difference from the passing live-tested revision. No fifth live invocation or additional provider call was made for publication or registry installation.

Internal packages and saved context have no promised upgrade compatibility. Billing accuracy, arbitrary-kill durability, production Host storage, Native AOT, broader platforms, public licensing/release and either product migration remain outside this evidence. There is no custom release manifest, digest approval protocol, promotion stage or claim that publication alone completes M5.
