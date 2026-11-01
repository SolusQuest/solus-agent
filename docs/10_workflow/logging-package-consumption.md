# Telemetry candidate packages and clean consumption

This track prepares an unpublished internal release independently of the [legacy five Agent packages](package-consumption.md). It does not upload packages, change visibility or qualify access to a hosted registry or Supabase deployment. The [L1 plan](../90_roadmap/logging-l1-plan.md) separates this preparation from authorized publication and live qualification.

## Exact selection

| Member | Selected version | Exact package dependencies |
| --- | --- | --- |
| `Solus.Telemetry.Contracts` | `0.1.0-internal.1` | none |
| `SolusAgent.Telemetry` | `0.1.0-internal.1` | Contracts `.1`, `SolusAgent.Api` `0.1.0-internal.4` |
| `Solus.Logging` | `0.1.0-internal.1` | Contracts `.1` |
| `Solus.Logging.Supabase` | `0.1.0-internal.1` | Logging `.1`, Contracts `.1` |
| Managed Linux x64 CLI payload | `0.1.0-internal.1` | the identical installed Contracts/Logging/Supabase DLL bytes |
| Existing private `SolusAgent.Api` | `0.1.0-internal.4` | none; **not a new publication member** |

The Api baseline originates at `cb5f5ef8fc39a24f888065052078d40722e7680b`. Its immutable original `.nupkg` SHA-256 is `a3a6db2e72b41627a72c6a37512220295d6e72116b75c5b04bff8155e4186137`; its `lib/net10.0/SolusAgent.Api.dll` SHA-256 is `0ca3324b506eb5382dc6c0fb52b2959de946aebcbf2f7ae54cc20b1f5afa931c`. `scripts/logging/api-baseline.json` locks these identities. The current public Api source/build settings must match the pinned source inventory before either validation profile can run. No missing core member or new core publication is assumed. The mapper compiles and executes against the genuine baseline through the separate actual-byte gate below.

`pack.ts` copies sources and build settings into an isolated temporary tree and changes only its copied projects to exact `[version]` PackageReferences. Production ProjectReferences and the legacy pipeline stay unchanged. It packs Contracts first, then mapper, Logging and Supabase. Every nuspec is inspected for the exact dependency graph. Its CLI consumes the same candidate packages through isolated caches, and its archive is rejected unless the three included library DLL hashes equal their package DLL hashes. Api and mapper are absent from the CLI.

SDK 10 supplies random OPC core-property part names and relationship IDs and variable ZIP timestamps. Preparation writes a standard stored ZIP with fixed timestamps, deterministic core-property part name/relationship ID and sorted entries. It preserves nuspec, DLL, XML documentation and content-type bytes; actual NuGet cold restoration exercises the resulting packages. The old published Api archive is never normalized or rebuilt. Deterministic compilation disables PDBs/source suffixes and maps temporary source paths. Only the isolated preparation tree disables SDK patch roll-forward; the staged CLI resolves its SDK from that tree. Preparation requires exact SDK `10.0.109` and Node `24.21.0` and records their versions, zlib, runtime pack and all payload file hashes; the repository-wide SDK policy and legacy pipeline remain unchanged. The gzip archive uses portable OS metadata (`255`) so otherwise identical Linux/macOS builds have identical archive bytes. Package-set equality is the SHA-256 of **all four original prepared archives**, with a separate CLI archive hash; DLL-only equality cannot substitute for archive equality.

## Preparation and two evidence profiles

Use SDK `10.0.109` from `global.json` and Node `24.21.0`, `unzip`, `openssl`, and the disposable-storage dependencies. Ordinary validation requires no model, hosted storage or registry credential:

```sh
npm ci --prefix scripts/logging
node scripts/logging/pack.ts --version 0.1.0-internal.1 --output artifacts/logging-packages
node scripts/logging/verify-consumers.ts --feed artifacts/logging-packages --manifest artifacts/logging-release.json --scenario all
node --test scripts/logging/consumption.test.ts
```

Output parents must be unused. The packer refuses overwriting any candidate directory/manifest; preserve earlier candidates rather than replacing bytes with the same release identity. It writes:

| Path | Role |
| --- | --- |
| `artifacts/logging-packages/*.nupkg` | exactly four new library candidates |
| `artifacts/logging-candidate-cli/` | CLI archive/checksum/report and proposed OCI manifest/config |
| `artifacts/logging-release.json` | source commit/tree/inventory/dirty flag, versions, dependencies, archive/DLL/destination hashes, Action pin, candidate digest |
| `artifacts/logging-validation-api/` | isolated **never-publish** baseline input; not part of the four-library feed or upload set |

The default credential-free profile builds an explicitly marked **source-baseline-fixture** from the pinned Api source. It needs the same NuGet ID/version `.4` so that the new mapper's real exact dependency metadata can resolve. Its description and assembly informational version identify the fixture, and its archive/DLL hashes must differ from the locked genuine package. Its manifest role, source inventory and separate feed remain explicit. This proves source compatibility and Linux installation/uploader mechanics; it does **not** prove genuine private `.4` byte installation, private-registry download or consumer access. Never publish the fixture, commit private binary inputs, or treat it as the old immutable archive. The publication-input guard rejects it as genuine baseline provenance.

A maintainer possessing the original authorized Api archive runs the independent actual-byte gate against the **same new candidate feed and manifest**:

```sh
node scripts/logging/verify-consumers.ts \
  --feed artifacts/logging-packages --manifest artifacts/logging-release.json --scenario all \
  --api-package /operator-owned/SolusAgent.Api.0.1.0-internal.4.nupkg \
  --packages-only --report artifacts/logging-genuine-api-proof.json
```

The tool verifies both locked hashes, copies the untouched archive into a fresh private validation feed, and restores/builds/executes every independent consumer with fresh caches. An existing global cache is never evidence. `--packages-only` explicitly omits Linux uploader execution; it permits a native macOS installed-byte gate without claiming ELF execution. A full run without that flag requires Ubuntu 24.04 x64 and fails on other hosts. An authorized Ubuntu operator can also supply `--api-package` without `--packages-only` for both gates in one run. No flag relaxes selected versions or original-byte checks.

For a genuine-baseline **preparation** manifest, use `pack.ts --api-package <original-archive>` with a different unused output parent. Compare all four raw archive hashes and the CLI hash against the source-fixture candidate from the same clean commit. They must match. `publicationFiles` is a local preflight that only returns the selected four new archives and CLI after integrity checks, clean-source checks and genuine baseline provenance; it performs no network action. The existing Api input and fixture feed can never become new upload members.

Dirty-source manifests are provisional development evidence. Publication review requires a clean exact source revision, original package/payload digests, matching Linux and genuine-byte evidence, and an explicit authorization for the particular destination and operation. The default `prepare` mode of the manual `Logging release preparation` workflow accepts an exact accepted-main SHA and exact internal version, uses read-only repository permission, prepares/qualifies the source-fixture profile and prints its metadata. It sends no registry credential and uploads no package, binary, release, tag or ACL. A green preparation job alone does not satisfy the genuine-byte or access gate.

## Independent installed consumers

`tests/TelemetryConsumers` contains three separately copied managed, non-packable projects outside the repository: Scribe and APR product shapes use installed Contracts/mapper/Api, while InstalledSink checks the public callable Logging/Supabase surface without issuing a send. Each has an explicit project inventory; missing/extra projects, friend assembly names, outside Compile/ProjectReference/imports, direct assembly links and non-exact PackageReferences fail. Evaluated MSBuild items are checked before and after restore. Resolved assets, `.nupkg.metadata` source, original archive and executed DLL hashes are checked in each fresh cache. No sibling source or production assembly is linked.

The generated NuGet config clears inherited sources/fallbacks. Both `Solus.*` and `SolusAgent.*` map to the four-candidate feed, with the more specific exact `SolusAgent.Api` mapping pointing only to the separate baseline feed. The payload builder additionally allows only official .NET runtime-pack prefixes at nuget.org. Independent negative restores prove missing/wrong versions and an Api package placed in the wrong feed cannot be rescued by ambient caches or fallback sources. The legacy consumer verifier remains separate.

The two products execute once for each observed business invocation, determine business outputs independently, and produce a bounded neutral record using public installed APIs. Scribe includes multiple current runs, exact `9007199254740993` and `9223372036854775807`, explicit partial zero/null, overlapping cache detail and independent product/caller JSON. APR observes public `ContextExecutionResult` DTOs for a new run from supplied context, keeps historical 100 out of current input 7, adds a partial large run and unavailable usage, and exits 7 for its independent failed business result. This synthetic DTO composition does not claim a production Runtime restoration or downstream migration. Invalid/duplicate/non-object/oversized caller JSON makes telemetry unavailable while retaining the business result.

On Ubuntu the verifier writes/readbacks a safe multiline product output, invokes the actual pinned Action entrypoint, acquires the candidate over controlled authenticated HTTPS, verifies/extracts it, and starts the actual CLI against controlled HTTPS RPC backed by the actual migration in disposable UTF-8 PostgreSQL. The writer has RLS and no owner/superuser bypass. SQL JSONB readback compares complete usage, execution, outcome and extensions, including precision/null/history boundaries. First/duplicate/collision and default-warning/strict authentication failure are tested, with explicit unavailable skip, no producer replay and unchanged original business output/exit. Synthetic download/storage credentials stay separate and masked; the existing wrapper suite owns detailed cancellation, timeout, hard-kill, platform and artifact refusal tests. No hosted storage is contacted.

## Proposed destinations, access and immutable release

The intended NuGet feed is **private** `https://nuget.pkg.github.com/SolusQuest/index.json`. Preserve the four exact filenames/hashes from the reviewed manifest. Publishing authority must include these package identities; a future operator verifies whether each version is absent or already has exactly the reviewed bytes. An existing unequal version is a hard stop: never replace, delete-and-republish, relabel the old Api archive, or use `--skip-duplicate` as byte-equality evidence. Credentials must come from an already authorized operator mechanism, not this preparation workflow.

The proposed CLI destination is a private OCI repository `ghcr.io/solusquest/solus-logging-cli:<exact-version>`. The manifest records the raw archive URL `https://ghcr.io/v2/solusquest/solus-logging-cli/blobs/sha256:<archive-sha256>`. Future authorized publication must retain the **original archive bytes** as that blob and an OCI version manifest referencing it; an unreferenced/orphan blob is insufficient. `oci-manifest.json` and `oci-config.json` describe the exact proposed manifest/config digests and layer size. Version-tag existence/immutability, accepted media types, original blob retrieval, visibility and read authorization remain unverified until the release gate. A registry PAT or `GITHUB_TOKEN` is not automatically the raw-blob bearer token: the operator separately obtains a finite repository-scoped registry bearer token through the registry's authentication flow. Default preparation invokes no client for that credential operation; the separately accepted exact operator mode below obtains only its finite fixed-namespace bearer.

Consumers need explicit read access to **every** selected private NuGet identity, including the already-published Api, and the CLI repository. The publisher's rights do not establish downstream repository-token rights. Package ownership, workflow repository linkage, organization policy and redistribution terms must be reviewed; there is no assumption of public redistribution rights or public artifact hosting. Future access validation uses approved consumer identities, exact source/version/hash pins and cold caches, and separately records NuGet fetch and direct original CLI-blob fetch. Credential values never belong in manifests, logs, docs or URLs.

After authorized publication and access qualification, a consumer uses the reviewed Action source pin (current implementation `SolusQuest/solus-agent/actions/logging@9d2f3777401d71fdea84ce350dd5bd6240acb7a8`), exact CLI version, reviewed raw blob URL/SHA and separately supplied masked artifact-read token. See [the pinned-step example and delivery policies](logging-action.md#consumer-workflow) for `telemetry-json`, Supabase inputs, `if: always()` and explicit strict opt-in. Business outcomes and telemetry delivery remain separate. These destinations are proposed preparation material, not currently available published CLI/package claims.

NuGet behavior references: [source mapping and specificity](https://learn.microsoft.com/en-us/nuget/consume-packages/package-source-mapping), [cache isolation](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [installed package metadata](https://learn.microsoft.com/en-us/nuget/concepts/troubleshooting-installed-packages). Destination references: [GHCR authentication/access](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry), [OCI distribution blobs/manifests](https://github.com/opencontainers/distribution-spec/blob/main/spec.md).


## Exact original-candidate operator path

The same already-registered `logging-release.yml` has mutually exclusive `validate-exact` and `publish-one` modes for the accepted original `c96a1b5cf9746a7fa569a339c583e07e3e103ee6` candidate only. This is a bounded issue123 operation; normal preparation remains read-only. The root/maintainer must review the exact helper/workflow commit before dispatching the task branch, establish the exclusive publisher window and current private-creation/admin prerequisites, and use that actual reviewed SHA as `expected-operation-head`. Branch/run/workflow/helper identities must agree and attempt must be one. No invented SHA or rerun of an uncertain operation is permitted.

`scripts/logging/accepted-candidate.json` locks the independently archived original complete candidate metadata, every outgoing archive size/hash and raw OCI documents. `publish-exact.ts` cold-restores genuine Api `.4` through the existing workflow token using an environment credential, rematerializes a clean separate exact c96 checkout with the original packer, and requires complete original identity/byte equality plus the unchanged c96 `publicationFiles` guard before any immutable write. A mismatch stops; changing the lock/version/source to match new output is not recovery. Api and fixture bytes are never upload members. Build children receive only the original clean build environment, and no private binary Actions artifact is uploaded.

First run `validate-exact` to demonstrate the hosted toolchain reproduces every original byte without publication. After that exact operation is accepted, `publish-one` selects exactly one fixed member: Contracts, mapper, Logging, Supabase, or CLI. It uses only the existing source `GITHUB_TOKEN` with job-level packages/write; no new PAT/key, legacy five-package publisher, Git tag or GitHub Release. Required dependency members must be published and byte-verified first. Existing exact versions/tags are retained only after authenticated complete-byte readback; unknown/conflicting state stops. No overwrite, delete, skip-duplicate or automatic upload retry is used.

OCI publication obtains a finite fixed-namespace bearer from the existing token, validates same-origin upload locations while preserving opaque query parameters, uploads the original config/archive, and publishes the exact original manifest only after blob readback and a fresh tag collision check. It then verifies tag, digest, config, archive and actual private visibility. Media rejection or an incompatible upload endpoint is an execution gate; it never authorizes repacking or manifest transformation.

After each new identity, stop for actual private visibility, inheritance-off, source Admin and the two named consumer repositories' Actions Read readback before another member operation. Organization defaults and publisher permissions cannot certify these settings. New-package initial inheritance observations must be recorded accurately. Each successful operation prints bounded candidate/operation/run provenance and explicitly leaves access/consumer verification false. Only after publication and actual settings are verified may the two separately reviewed consumer workflows run once at their actual merged/read-back default SHAs. Their cold registry acquisition and CLI handshake remain separate from storage delivery and the original local verifier's `registryAccessVerified: false`.

The operator uses the existing GitHub workflow dispatch interface with exact `mode`, `member`, `source-commit=c96a1b5cf9746a7fa569a339c583e07e3e103ee6`, `version=0.1.0-internal.1`, and the real reviewed `expected-operation-head`. Capture the actual run ID/attempt and reconcile it before any recovery. Do not dispatch before exact-head acceptance or infer issue123 completion from a validated/published member. The owning release record is written only from actual publication, settings and consumer evidence.
