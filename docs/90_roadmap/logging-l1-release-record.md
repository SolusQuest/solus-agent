# L1 private telemetry publication and consumer access

The four telemetry/logging libraries and original Linux x64 CLI at `0.1.0-internal.1` are published privately and were acquired and installed using the actual ContractScribe and APR repository tokens. This record captures [#123](https://github.com/SolusQuest/solus-agent/issues/123), following the [candidate qualification](../10_workflow/logging-package-consumption.md). It does not qualify hosted Supabase, complete either product migration, or close [L1 parent #116](https://github.com/SolusQuest/solus-agent/issues/116). The [L1 plan](logging-l1-plan.md) retains those distinct outcomes.

## Immutable source and selection

The released candidate source is [`c96a1b5cf9746a7fa569a339c583e07e3e103ee6`](https://github.com/SolusQuest/solus-agent/commit/c96a1b5cf9746a7fa569a339c583e07e3e103ee6), tree `ac0d319d738a257f37d2be3ad887018aa9b01535`. Its accepted merge at `67165130e5d4f404f5acfe1014cafeffd464859a` has the same complete tree; the source identity was not relabeled to that merge. The candidate SHA-256 is `9bff5f5737aac81adbea85179b248a52136ff7ab041acc4f85c7aa4a596c0e84`; the four-original-archive package-set SHA-256 is `a045cbbdd10fa9df19c0d7405be426a85b3b4b116aa6f83c68f48da87e7974e7`.

The standalone Action pin recorded by that candidate is `SolusQuest/solus-agent/actions/logging@9d2f3777401d71fdea84ce350dd5bd6240acb7a8`. Action source, candidate source, operator workflow source, and consuming workflow source are separate identities. The [Action guide](../10_workflow/logging-action.md) describes its inputs and delivery policy; this record supplies the released payload pins rather than the guide's earlier preparation placeholders.

The bounded manual workflow at operator source [`fe35047e29056660a5ed1982f07f042ac93286e4`](https://github.com/SolusQuest/solus-agent/commit/fe35047e29056660a5ed1982f07f042ac93286e4) completed CLI publication. Earlier library operations used the separately reviewed operator revisions listed below. These helpers are delivery machinery, not candidate package sources. Explicitly authorized rematerialization checked the unchanged clean `c96a1b5` source with genuine original Api input, exact SDK `10.0.109`, Node `24.21.0`, and the locked runtime/tool inputs. Every outgoing original archive, raw OCI document, package-set and candidate digest had to equal the independently retained accepted bytes before mutation. No new version or semantically equivalent replacement was admitted. The original preparation manifest remains historical preparation evidence; its `prepared-not-published` status was not rewritten to manufacture publication proof.

## Private NuGet packages

The authenticated feed is `https://nuget.pkg.github.com/SolusQuest/index.json`. Each new archive is version `0.1.0-internal.1` and retains the exact dependency graph in the candidate guide.

| Original filename | Bytes | Original archive SHA-256 | Package / version identity |
| --- | ---: | --- | --- |
| `Solus.Telemetry.Contracts.0.1.0-internal.1.nupkg` | 65898 | `b220edcec027c743c6d42916bb2321874c0043fd6fb97ec1dc0263d2a497ab5d` | `16460732` / `1481941083` |
| `SolusAgent.Telemetry.0.1.0-internal.1.nupkg` | 24102 | `5045f2551ff41921d958fef1271e1acf9c8373158859566edb7d70fa9c5896fa` | `16466888` / `1482864367` |
| `Solus.Logging.0.1.0-internal.1.nupkg` | 26232 | `50348a99a102de2bdd1257ee0c1ea36895cfc1b74172eeb193f6553812fb06e0` | `16467361` / `1482940818` |
| `Solus.Logging.Supabase.0.1.0-internal.1.nupkg` | 20577 | `efd5524a93c6bc36e69cf3dc38028dc8e3ec75445b31d04ab1166b1945281537` | `16467548` / `1482980731` |

| Member | Original publication operation, attempt 1 | Actual result and subsequent proof |
| --- | --- | --- |
| Contracts | [41291519134](https://github.com/SolusQuest/solus-agent/actions/runs/41291519134), operator `ab42d39022778b0aed0be40dc5f0be8d5172914d` | Package/version was created, but the operation failed its inventory-dependent readback. It was never rerun or repushed. Independent operator cold download and [source-token read-only reconciliation 41302417937](https://github.com/SolusQuest/solus-agent/actions/runs/41302417937) proved the complete original archive, DLL, dependencies and source. That reconciliation remained a controlled failure because the complete organization inventory contradicted direct package/version and feed views. Both actual consumer proofs below subsequently acquired the original archive. |
| Mapper | [41314780789](https://github.com/SolusQuest/solus-agent/actions/runs/41314780789), operator `ce2d07c4ff3f02c7b83156b923b25815754ef3b0` | Succeeded with strict direct private/source/version metadata and cold whole original archive readback. |
| Logging | [41316849269](https://github.com/SolusQuest/solus-agent/actions/runs/41316849269), operator `ce2d07c4ff3f02c7b83156b923b25815754ef3b0` | Succeeded with strict direct private/source/version metadata and cold whole original archive readback. |
| Supabase library | [41317817015](https://github.com/SolusQuest/solus-agent/actions/runs/41317817015), operator `ce2d07c4ff3f02c7b83156b923b25815754ef3b0` | Succeeded with strict direct private/source/version metadata and cold whole original archive readback; no live Supabase operation occurred. |

`SolusAgent.Api` `0.1.0-internal.4` is the existing read-only dependency, not a fifth new package. Its source is `cb5f5ef8fc39a24f888065052078d40722e7680b`, original archive SHA-256 `a3a6db2e72b41627a72c6a37512220295d6e72116b75c5b04bff8155e4186137`, and DLL SHA-256 `0ca3324b506eb5382dc6c0fb52b2959de946aebcbf2f7ae54cc20b1f5afa931c`. Neither it nor the explicitly marked source-baseline fixture was uploaded by this release. Genuine-byte installation and the credential-free Linux fixture-to-Action/CLI/HTTPS/disposable-SQL proof remain separate profiles; neither alone establishes actual registry access.

## Original CLI and Action inputs

The private OCI reference is `ghcr.io/solusquest/solus-logging-cli:0.1.0-internal.1`; immutable manifest reference `ghcr.io/solusquest/solus-logging-cli@sha256:9d23869078138bd159ebfb3c3686da55215cf49a00b267352d11c371ce201913`. GitHub package identity is `16488925`, version identity `1486460917`, linked to source repository `1406091678` (`SolusQuest/solus-agent`).

| Original object | Bytes | SHA-256 |
| --- | ---: | --- |
| `solus-logging-cli-0.1.0-internal.1-linux-x64.tar.gz` | 36409125 | `656957224f9e81ad16ba882c103eeaef51989deb77a2541ae0cab93d5f653339` |
| Raw OCI manifest | 710 | `9d23869078138bd159ebfb3c3686da55215cf49a00b267352d11c371ce201913` |
| Raw OCI config (`{}` plus LF) | 3 | `ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356` |

[Original CLI publication 41418879195](https://github.com/SolusQuest/solus-agent/actions/runs/41418879195), workflow number 13, attempt 1 at operator `fe35047e29056660a5ed1982f07f042ac93286e4`, succeeded. It checked the complete original candidate and all four published library archives before OCI mutation. It retained the existing config, uploaded the original CLI blob, and put the original manifest once. Final cold GETs verified the original manifest through both tag and digest, the entire config and entire CLI archive, followed by actual GitHub Private metadata readback. No transformation, repacking, annotation insertion, Git release, release tag, deletion or overwrite was used.

The reviewed sequencing permits a private immutable manifest to exist before final cold body verification. That partial-state risk was explicitly accepted before this operation; an uncertain or failed result would not be success and would not authorize retry, rollback, replacement bytes or broader grants. Earlier failed CLI operations remain failures: [41320103950](https://github.com/SolusQuest/solus-agent/actions/runs/41320103950) created an empty upload session before rejecting an upload location, and [41406992322](https://github.com/SolusQuest/solus-agent/actions/runs/41406992322) received config PUT 201 before failing readback. Later bounded diagnostics and eventual successful publication do not reconstruct those historical responses, prove a provider root cause, or establish rollback. Those runs were not rerun.

For an authorized Action invocation, use the Action pin above with these exact inputs:

| Input | Value |
| --- | --- |
| `cli-version` | `0.1.0-internal.1` |
| `cli-artifact-url` | `https://ghcr.io/v2/solusquest/solus-logging-cli/blobs/sha256:656957224f9e81ad16ba882c103eeaef51989deb77a2541ae0cab93d5f653339` |
| `cli-artifact-sha256` | `656957224f9e81ad16ba882c103eeaef51989deb77a2541ae0cab93d5f653339` |
| `artifact-read-token` | An externally supplied finite bearer with only `repository:solusquest/solus-logging-cli:pull`, obtained through the existing approved credential mechanism; never commit its value. |

The package-read grant and `permissions: {contents: read, packages: read}` allow the actual consumer workflow to obtain its bounded pull bearer. A raw `GITHUB_TOKEN` is not presented as an interchangeable GHCR blob bearer. Action telemetry, Supabase endpoint and ingest credentials still require their separate authorized context. The two registry proofs below acquired and executed the released CLI's `--version` handshake; they did not invoke the logging Action against storage.

## Persistent package access

All four new NuGet identities and the CLI identity were independently inspected as Private. Source-access inheritance was initially enabled on the actual new identities and was explicitly disabled; this record does not claim it never existed. After each scoped change, native settings and server-reloaded readback showed inheritance off and exactly these Actions grants:

| Repository | Repository ID | Actions role |
| --- | --- | --- |
| `SolusQuest/solus-agent` | `1406091678` | Admin |
| `SolusQuest/contract-scribe` | `1297409952` | Read |
| `SolusQuest/agentic-pr-review` | `1263978368` | Read |

There were no extra Actions repositories, Codespaces repositories, or people/team members in those persisted readbacks. Existing matching source links and roles were preserved. These are persistent delivery settings and were retained after qualification; temporary local awake holds ended without changing macOS security or browser automation permissions. The existing Api dependency's access was inspected and exercised without changing its ACL.

For CLI creation, documented new-container Private default, exact authenticated absence and exclusive-operation checks, no visibility-changing code, and mandatory actual Private readback supplied the accepted headless creation evidence. A fresh organization creation-checkbox/default-inheritance screenshot was unavailable then and was not claimed. This did not waive later per-package settings or actual consumer proof. The accepted GitHub boundary remains: granting private-package Actions access to public repositories can allow access from their forks; Private and inheritance off are not fork-exclusion guarantees. Source visibility is not binary redistribution authorization. No broader grant or public redistribution is established here.

## Actual consuming-repository proofs

Each named repository now has a reviewed manual-only proof workflow registered on `main`, merged through [CS PR #271](https://github.com/SolusQuest/contract-scribe/pull/271) and [APR PR #474](https://github.com/SolusQuest/agentic-pr-review/pull/474). The exact-head guard checks repository name/ID, manual `main` invocation, x64, and equality of the expected head with both run SHA and workflow SHA before checkout. The workflow uses only contents/read and packages/read (plus implicit metadata/read), its own repository `GITHUB_TOKEN`, fresh package caches and the finite CLI pull scope. No consumer credential was created or copied from another repository.

| Actual consumer | Original run, attempt 1 | Exact run/workflow SHA | Metadata artifact / JSON SHA-256 |
| --- | --- | --- | --- |
| `SolusQuest/contract-scribe` (`1297409952`) | [41458356150](https://github.com/SolusQuest/contract-scribe/actions/runs/41458356150), job `125320746699` | `3ea2dfddfc7a60196e24c43e5eab52a378abdb4e` | `13147058941` / `16522fd666ff33436fcadd63b43958ab07b9109678bd70cb75dd3553f82c100a` |
| `SolusQuest/agentic-pr-review` (`1263978368`) | [41458444500](https://github.com/SolusQuest/agentic-pr-review/actions/runs/41458444500), job `125321052100` | `f5ced01436a6800816afcce76fc7310be8b136cf` | `13148371312` / `16b583f45af5514c96664e85fd2d6c8b69c405844f06387cdc077646183921e2` |

Both original runs and every step succeeded, each as workflow number 1 and attempt 1, with one dispatch and no rerun. The metadata-only artifacts used three-day retention; the hashes above identify the downloaded JSON reports, not their upload ZIP containers. Before each send, authenticated default refs, selected proof/support Git blobs and modes, active registration, zero prior proof runs and original private package identities were checked. APR had advanced from the original proof merge `914edae04284203332f4d9145bae5eb205e24b5d` through reviewed `b381b642bba0985fa3ad7077080256bc8ada1eb1` to `f5ced01436a6800816afcce76fc7310be8b136cf`; the later one-commit/two-file capacity-diagnostic change was separately inspected, all proof/support/instruction bytes were unchanged from `b381b642`, and the actual `f5ced014` binding was explicitly accepted rather than silently substituted.

Both workflows checked out clean `c96a1b5cf9746a7fa569a339c583e07e3e103ee6` and verified historical Api source availability. Using their own actual repository token, each cold-downloaded all four original `.1` NuGet archives plus genuine Api `.4` from the private feed and checked their full archive SHA-256 values. Each fetched the original CLI blob, verified its full SHA-256, extracted it safely and executed the identity handshake: protocol `1`, version `0.1.0-internal.1`, runtime identifier `linux-x64`.

The same unchanged installed verifier executed all three independent scenarios (`Apr`, `Scribe`, `InstalledSink`) with fresh caches, exact package/DLL bytes and no source links. Its inner report deliberately remains `registryAccessVerified: false`, `stage: installed-packages-only`, `execution: null`, and `publicationPerformed: false`. Actual registry access is proven by the separate outer repository-bound acquisition report and successful hosted steps, not by rewriting the local verifier's evidence profile. `execution: null` omits the separate Action/HTTPS/SQL execution profile; it does not erase the installed consumer executions recorded in `installed`.

Immutable action implementations used by both consuming workflows were:

| Implementation | Exact commit |
| --- | --- |
| `actions/checkout` | `9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0` |
| `actions/setup-dotnet` | `26b0ec14cb23fa6904739307f278c14f94c95bf1` |
| `actions/setup-node` | `249970729cb0ef3589644e2896645e5dc5ba9c38` |
| `actions/upload-artifact` | `cf430e030ddbb5b0abf93d22962f4752f3646cd9` |

## Evidence limits

This establishes original private artifact publication, persistent named access, and actual CS/APR repository-token cold acquisition/installed consumption on Ubuntu 24.04 x64 with managed .NET 10 and Node 24.21.0. CLI payload includes its runtime. Whole-archive equality, source metadata and actual consumer credentials are distinct from prior source-fixture controlled execution; local administrator or source-workflow token access alone was not used as consumer proof.

No product Action or application was migrated, no product release was made, and no model, billing, hosted storage, live Supabase/RLS/RPC, production durability or additional Action-to-storage qualification is claimed. No full downstream adoption or L1 completion follows from this record. Bounded hosted storage qualification remains the separate [#124](https://github.com/SolusQuest/solus-agent/issues/124) gate. Existing immutable versions, failed-operation history and the accepted initial-inheritance/fork boundary are retained; the record authorizes no further operation.
