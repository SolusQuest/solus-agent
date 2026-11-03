# L1 published Action live qualification

Hosted qualification: **PASS — actual published Action → hosted Supabase**, accepted by the coordinator from the complete run proof below. Real local Docker Supabase qualification: **PASS**, recorded separately. [Issue #124](https://github.com/SolusQuest/solus-agent/issues/124) remains open pending final evidence review and merge; the earlier failed hosted attempt remains failed. The [release record](logging-l1-release-record.md) separately owns completed private publication, package access and actual repository-token acquisition proofs from #123.

The selected caller is `SolusQuest/solus-agent` (repository ID `1406091678`), using the existing registered `logging-release.yml` workflow (`403167763`) on `codex/l1-124-live-qualification`, with the isolated `qualify-live` job. Its token has `contents: read` and `packages: read`. Existing preparation, publication and diagnostic jobs remain unchanged and are excluded by mode. No downstream repository write or repeated publication is part of this qualification.

## Immutable released inputs

The source is `c96a1b5cf9746a7fa569a339c583e07e3e103ee6`, version `0.1.0-internal.1`, candidate `9bff5f5737aac81adbea85179b248a52136ff7ab041acc4f85c7aa4a596c0e84`, package set `a045cbbdd10fa9df19c0d7405be426a85b3b4b116aa6f83c68f48da87e7974e7`. The independent [accepted lock](../../scripts/logging/accepted-candidate.json) retains every original whole package/DLL hash and dependency.

The job cold-acquires the original four `.1` packages plus the original `SolusAgent.Api` `.4` input from the named private GitHub NuGet feed. Whole archive hashes are checked before inspection or MSBuild. It reuses the unchanged c96 `installed.ts` to copy and compile the independent Scribe and Apr PackageReference consumers with separate caches, local-only package feeds, exact DLL verification and no source links. Acquisition credentials do not enter their inspection/build/producer children. No library or CLI candidate is rebuilt or substituted.

Every delivery invokes `SolusQuest/solus-agent/actions/logging@9d2f3777401d71fdea84ce350dd5bd6240acb7a8`, downloading the original Linux x64 CLI blob from `ghcr.io/solusquest/solus-logging-cli`. The 36,409,125-byte archive SHA256 is `656957224f9e81ad16ba882c103eeaef51989deb77a2541ae0cab93d5f653339`; raw OCI manifest SHA256 is `9d23869078138bd159ebfb3c3686da55215cf49a00b267352d11c371ce201913`, config SHA256 `ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356`. These immutable release identities are checked by the actual acquisition and execution evidence below.

## Hosted setup and credential lifecycle

Initial scoped preparation found no configured target or ordinary credentials. The user later selected an existing isolated hosted test project and supplied an existing local administrator credential for the disclosed setup. The exact accepted migration was applied once (version `20261103105050`, SQL SHA256 `33ba45c0f8c37d448d7ed846200f2a75f7071a76758454be07a396d894411011`); root inspected its full catalog, grants, RLS and matching function body, zero rows and empty security advisories. Two dedicated confirmed ordinary Auth users were created through genuine Auth administration and obtained real password-login sessions; each ordinary self-identity request succeeded. No project Auth settings or API keys were created or changed. The administrator credential stayed local and never entered Actions.

After explicit user authorization, four ordinary repository bindings were configured temporarily for each separately admitted attempt: `TELEMETRY_L1_SUPABASE_ORIGIN`, `TELEMETRY_L1_PUBLISHABLE_KEY`, `TELEMETRY_L1_WRITER_A_JWT`, `TELEMETRY_L1_WRITER_B_JWT`. All four task-owned names were removed after both the failed first and successful second attempt, with timestamp ownership checks, one deletion each and final empty-name readback. Existing A/B accounts obtained fresh genuine ordinary sessions for the second attempt; no administrator credential entered the job. The selected test schema, two users, sessions, three synthetic rows and private evidence remain retained under the test operator and coordinator's custody. Subsequent necessary same-scope validations are preauthorized by the user, while each concrete operation still requires fresh source/effect admission, current credentials and finite bounds. This is not a runtime retry loop.

The publishable key identifies the application; A/B are distinct ordinary authenticated JWT principals, not service-role/admin credentials. The helper checks issuer/subject hashes, ordinary role and remaining lifetime without claiming local JWT signature verification. The target must authenticate them and enforce RLS. The helper does not query deployed definitions; root/operator's separately accepted deployment evidence is authoritative. No migration is performed automatically during a job. Credential values never belong in chat, dispatch inputs, commits or proof artifacts.

The fixed scenario retains at most three new labeled synthetic rows under `Yuee98` with coordinator evidence custody. No delete/update or automatic row/user/session cleanup is included; ordinary ingestion credentials cannot be presumed to clean up. A later administrator cleanup needs its own precise scope. Temporary GitHub Secret removal is separate from row/user retention.

## Fixed operation and effect budget

The admission names one operation nonce, two fresh event UUIDs S/P, exact source/workflow SHA, actor, next actual workflow run number, target/issuer/A/B/schema-evidence/cleanup-owner hashes and a maximum one-hour admission window. It is trusted operator attestation backed by independently inspected facts, not provider-signed proof or a self-issued authorization. The exact source and effect packet require root acceptance before dispatch. An actual GitHub run ID is recorded only after receipt.

The first step rejects a wrong actor, repository, ref, source/workflow SHA, run number or attempt before credential-use steps. The full helper guard rejects malformed, missing or expanded admission. Attempt 2 and a second distinct run number fail closed. A concurrent workflow counter advance requires stopping; it does not permit selecting a replacement counter or source. Unknown dispatch outcome requires reconciliation of the original send, never resend/rerun.

The synthetic Scribe process returns 0; the Apr process returns **7** while producing usable telemetry. Apr runs in a separate `continue-on-error` step, preserving `outcome: failure`. Both process outputs, original once markers and telemetry are verified independently before credentials are admitted. Synthetic source labels and timestamps are fixture data, not actual downstream product execution or restoration.

| Action | Principal / shape | Expected storage result |
| --- | --- | --- |
| 1 | A / Scribe S | `stored` |
| 2 | A / Apr P | `stored` despite failed business outcome |
| 3 | A / identical S | `duplicate` |
| 4 | A / identical P | `duplicate` |
| 5 | A / same S, only productVersion changed | `failed` / `identity_conflict` |
| 6 | A / same P, only productVersion changed | `failed` / `identity_conflict` |
| 7 | B / original S | `stored` under B's own writer key |

Maximum target effects are **7 Action deliveries / 7 Supabase POSTs and 10 readback GETs, at most 3 new rows**: `(A,S)`, `(A,P)`, `(B,S)`. Each Action uses maximum-attempts 1, timeout 10,000 ms, retry delay 0 and strict-delivery false; the subsequent helper still requires the exact delivery status and diagnostic. A successful Action step alone is not acceptance. Expected collision failures are distinguished from unexpected failures.

The first A/B reads select only writer/event IDs for both planned UUIDs and must be empty. Any existing, forbidden, missing, malformed or unknown result stops before writes. Full content is selected only after the operation's expected stored results. Fixed later reads verify A's two complete rows after initial storage, duplicate and collision; B cannot read A's exact rows, A cannot read B's row, and B can read its own S. Queries use only admitted IDs, `limit=3`, fixed fields, no redirects/retries, 10-second deadline and 64 KiB response cap. RLS-filtered views do not prove a global table count.

The oracle compares every envelope scalar and complete execution/outcome/usage/extensions structures, preserving nullable values, coverage, decimal Int64 strings, nested product/caller data, Unicode and array order. JSONB key order may differ; missing differs from null. Timestamp comparison reconstructs microseconds plus residual tick, and repeated reads require unchanged `received_at` and complete A rows. The immutable selected fixture has its own timestamp precision; source comparator tests do not establish a hosted seven-digit timestamp boundary result.

Job duration is bounded to 20 minutes, installation to 8 minutes and network steps to 2 minutes. Initial writer lifetime must cover 25 minutes; subsequent reads require at least 65 seconds remaining. There is no token refresh, automatic retry, event replacement, rollback or cleanup. Every uncertain request or unexpected result stops subsequent effects, retains the original IDs and requires separately accepted reconciliation.

Registry acquisition is separate from the 17 target requests. Five archives allow at most ten fixed NuGet/backing GETs; each backing hop drops credentials. Seven immediately preceding, finite namespace PULL exchanges feed the seven Action steps through a **mask-first same-job `GITHUB_OUTPUT` channel**. Tokens are not job outputs, artifacts, cache entries or operator credential files; the runtime file-backed channel is not a zeroization guarantee. The frozen Action allows three HTTPS redirects (four artifact GETs) per invocation and strips credentials across origin changes. Thus controlled registry GETs are bounded to 45, excluding separately time-bounded checkout/SDK setup traffic. Seven successful canonical CLI bodies total 254,863,875 bytes; that expected total is not a universal transfer cap. The immutable Action's compressed reader cap remains 128 MiB per invocation.

## Validation and evidence boundary

Run `node --test scripts/logging/live-qualification.test.ts` for credential-free admission, transport isolation, full-row oracle, fail-stop state, real child exit-7 handling and workflow isolation/freeze tests. Local synthetic transport and child fixtures prove source behavior only. The tests freeze all five prior jobs and accepted helpers/locks; no publisher or packer is imported by the new helper. Logging CI selects this test file alongside the existing tests on the actual new source head.

The first GitHub-hosted attempt failed after two authenticated metadata-only preflight GETs. The second attempt completed the full hosted qualification recorded below. The metadata-only proof binds the actual repository/run/attempt/source, original archive acquisitions, installed package identities, producer exits/once counts, seven exact Action outcomes and ten complete read comparisons. Original private inputs, raw headers/bodies/logs, endpoints, JWTs, writer subject IDs and binary archives stay outside commits and public proof artifacts.

The successful #123 outer repository-token acquisition proofs remain distinct from their inner local verifier's `registryAccessVerified: false` and `execution: null` fields. Those flags are not rewritten by #124: actual hosted acquisition and execution are a separate outer profile. Neither the local nor hosted synthetic qualification establishes full product adoption, model requests/billing or a durable production pipeline.

## Observed hosted qualification

[Run 41659740435](https://github.com/SolusQuest/solus-agent/actions/runs/41659740435), workflow number **15**, attempt **1**, completed **SUCCESS** on 2026-11-03 at `15:03:56Z`. It executed workflow `403167763` from exact reviewed head `e2477ffb1503b6b13e45eafda719da02d7edd6c1`. The `qualify-live` job `125999482539` had all **50 steps successful**; the five unrelated jobs were skipped. The coordinator personally accepted the full proof, native step/log evidence and independently verified private archive hashes. No run was rerun and no additional storage request was made to refresh this record.

The metadata artifact `solus-logging-live-proof` (`13240739686`) contained `solus-logging-live-proof.json`, **6,444 bytes**, SHA256 `9bb60b3d3af8fdc0ab1f16ef63158072158046ed39ec7fe9dd0029cd72b83914`. This hashes the decoded JSON, not the compressed artifact container. GitHub artifact retention is three days; the operator retains the complete proof and private raw evidence under coordinator custody. The proof binds admission wire SHA256 `b6b2b70076598e4dcaa160521135a9eed182f4445306dfa2c3d3f150c3710556` and separately inspected schema evidence SHA256 `2833cd50e571425e4b3747ead7b1c7d1e13ea05189f1d5040111ae22253b9bd8`.

Cold acquisition verified all five original package archives from the private feed, using ten bounded archive GETs and fresh caches. The four `.1` libraries and `.4` Api input matched every accepted version and whole archive hash. The unchanged c96 installer verified exact installed bytes with no source links. Scribe and APR each ran once, preserving business exits **0** and **7**; APR's failed business outcome survived `continue-on-error` and did not prevent usable telemetry. All seven deliveries used the original pinned Action and CLI bytes through real HTTPS and genuine ordinary Auth principals, not a fixture response or privileged administrator INSERT.

| Actual Action | Observed result |
| --- | --- |
| A / Scribe S | `stored` |
| A / Apr P | `stored`, original business exit 7 retained |
| A / identical S | `duplicate` |
| A / identical P | `duplicate` |
| A / same S, changed productVersion | `failed` / `identity_conflict` |
| A / same P, changed productVersion | `failed` / `identity_conflict` |
| B / original S | `stored` under B's own writer key |

All ten reviewed-source readback comparisons reported `completeMatch: true`:

| Read | Ordinary principal / selected writer | Rows and assertion |
| --- | --- | --- |
| 1 | A / A | 0, metadata-only preflight |
| 2 | B / B | 0, metadata-only preflight |
| 3 | A / A | 2, full initial records captured |
| 4 | B / A | 0, other writer hidden |
| 5 | A / A | 2, duplicate did not change content or received_at |
| 6 | A / A | 2, collisions did not overwrite content or received_at |
| 7 | B / B | 1, complete original S with own writer identity |
| 8 | B / A | 0, isolation after B's storage |
| 9 | A / B | 0, reverse isolation |
| 10 | A / A | 2, complete final records unchanged |

The actual ordinary-user oracle compared all 13 columns and complete nested JSON, including nullable/coverage facts, decimal Int64 strings, execution/outcome/usage/extensions, timestamp identity and immutable received_at. Raw row bodies are intentionally absent from the metadata artifact; `completeMatch` is produced only after the reviewed oracle succeeds. The hosted fixture does not establish the additional seven-digit timestamp boundary exercised by the separate local derivative.

The helper retains `providerSchemaDefinitionVerifiedByHelper: false`; the coordinator's independent catalog/function/grant comparison supplies schema acceptance. `independentServerHTTPTraceClaim: false` remains explicit: seven storage POSTs and seven pull exchanges derive from actual immutable Action/control flow and verified outputs, while ten readbacks are recorded helper observations. The observed own views are A=2 and B=1, cross-writer=0; this is not a privileged global row count. Execution-scoped `noNewPersistentCredential` and `noPublicationOrMigration` do not erase the separately authorized schema/Auth bootstrap and temporary Actions bindings.

The successful run used **7 POST + 10 GET = 17** target business requests, three observed new composite keys and no automatic retry. Including failed run 14, cumulative hosted business traffic is **7 POST + 12 GET = 19**, still three new keys. Auth setup, registry acquisition and toolchain traffic are accounted separately; this is not a universal HTTP-call cap. The earlier local Docker window remains separately closed. Exactly the three synthetic rows are retained with the two test users/sessions/schema; no DELETE/UPDATE or user/session cleanup was performed. All four temporary task-owned Actions Secrets were removed after terminal acceptance, without changing unrelated settings.


## Observed real local Docker qualification

On 2026-11-03 the explicitly authorized local test used a fresh, isolated Docker network with **no published ports**. Actual Supabase Postgres supplied its original Auth schema, roles and `auth.uid()`; the exact `001_telemetry.sql` was applied only to the new task database. Real GoTrue email/password grants issued ES256 JWTs for two distinct ordinary users. The official Envoy key-translation configuration routed a correctly generated `sb_publishable_` key and those user JWTs to PostgREST. No fake identity function, replacement RPC, administrator insert or storage-response fixture supplied the proof.

The Docker host was Linux arm64; Node `v24.21.0`, SDK `10.0.109` and the actual CLI ran inside a Linux amd64 container under emulation. The unavailable SDK Docker tag was replaced by the **same** official SDK archive, SHA-512 `5648e8edbdd8127452828964ca8be631ba19b75aa4020bc3352f3ab861b8b5982d823fd6a3bb3b741ec4e3fdc6c0de9ec8a52e8dcedacccf9e8754250579dc2d`, verified against Microsoft's release metadata. No package/CLI was rebuilt or substituted.

| Image | Exact pulled digest |
| --- | --- |
| `supabase/postgres:17.6.1.136` | `sha256:f371b5f3f2ac0a05703f33d6e6134515fb2498cab708fb948a0aeb7481467c00` |
| `supabase/gotrue:v2.197.0` | `sha256:1736a63078f5922b198c4cbe50f80ab9a2d3b54fe8b7b6cfb2e9dc5dbbc12c6b` |
| `postgrest/postgrest:v14.18` | `sha256:c7cd7e265a85b05248abf53f4473fcec7e327c282d0bd0cf60f1551996d09fcf` |
| `envoyproxy/envoy:v1.39.1` | `sha256:57e14a549d7bd43c8d3f6d03e8cfa653e037d4b38e133acd9b54f38c524401b4` |
| `caddy:2.10.2` | `sha256:c3d7ee5d2b11f9dc54f947f68a734c84e9c9666c92c88a7f30b9cba5da182adb` |
| `node:24.21.0-bookworm-slim` | `sha256:d6aa754f16b3197301076f047b5def2f02ea1dbbc2ca920407d46d7ec7f87b20` |
| `mcr.microsoft.com/dotnet/runtime-deps:10.0-noble` | `sha256:62028293921f5bd6db3d9f9119cd29cd926dfd3c2336da23b09f425e40c3c832` |

Official gateway files came from Supabase commit `831d6302c7361c0a5115a91e953bdf084eb85330` and were hash-verified unchanged. Studio, Storage, Realtime and Analytics were omitted. Only the native Auth/PostgREST role passwords were configured; omitted components' role dependencies were not fabricated. A private task CA signed the gateway certificate. Node and the original .NET child trusted that CA explicitly; certificate-chain and hostname checks remained enabled. No host/browser trust or security setting changed. Bootstrap signing/DB keys were excluded from the ordinary runner's mounts; ingest and readback used ordinary credentials only.

All five archived original NuGet archives were whole-hash checked before inspection/MSBuild. Unchanged c96 `installed.ts` cold-installed the independent products using exact package/DLL checks. Actual Scribe and Apr child processes ran once each, with exit **0** and **7** respectively and usable original telemetry. A small local bridge invoked the unchanged published `runAction` function and captured its bounded returned result; the original Action downloaded the exact archived CLI over real HTTPS, checked its hash/version, then invoked the real CLI/adapter through Envoy/PostgREST to the database. This is local Action-entrypoint execution, not a GitHub Actions step or job result, and it is not new private-registry acquisition proof.

| Real case | Observed result |
| --- | --- |
| Original A/Scribe and A/Apr | Both `stored`; failed Apr business exit remained 7 |
| Repeat both original records | Both `duplicate`; complete rows and `received_at` unchanged |
| Same IDs, changed productVersion | Both `failed / identity_conflict`; no overwrite |
| B stores original Scribe ID | B's own row stored; both users' exact cross-writer queries returned zero |
| Additional synthetic timestamp derivative | Stored/read back `.1234567Z` as microseconds plus residual tick 7 |
| Invalid JWT signature, wrong publishable key | Action `authentication_failed`, one attempt each |
| Anonymous GET/RPC, missing key and invalid-key/JWT GETs | HTTP 401; no administrator substitute |
| Real GoTrue short-lived JWT | Valid before expiry; rejected after actual `exp+35.010s`, Action `authentication_failed` and GET 401 |
| Only task REST backend stopped | Action `attempts_exhausted`, one attempt; after restart all full rows and timestamps unchanged |

The successful final entry run had 12 actual Action calls, 35 target REST requests, five Auth bootstrap requests and two explicit HTTP health checks. Final ordinary views contained three A rows and one B row. Full readback compared all declared columns and complete JSON, including null/coverage facts, unsafe-integer decimal strings, nested caller/product extensions, Unicode and array order. The extra timestamp record was a **local synthetic derivative**, not a second product invocation. Denial/outage POSTs reused already stored A/S so an unexpected acceptance could only duplicate, not create a fifth row.

Earlier attempts remain failures: native LibreSSL lacked a hostname-check option; full roles wiring referenced an omitted component; a signing JWK lacked official `key_ops`; and an expiry assertion ran inside PostgREST's documented 30-second skew allowance. That last failed attempt actually created a fifth synthetic row and was not relabeled passed. Its entire owned database was removed. Corrected expiry testing waits beyond the documented allowance without changing tokens, server policy or clocks. [PostgREST v14 authentication](https://docs.postgrest.org/en/v14/references/auth.html#time-based-claims-validation) defines that tolerance.

Across all retained attempts, cumulative target business traffic was **98/100**: 35 Action POSTs, 60 direct REST GETs and three direct RPC denial POSTs. Auxiliary traffic was recorded separately: 15 Auth bootstrap requests, 36 HTTP health checks, 35 local archive GETs and 48 PostgreSQL readiness CLI invocations; automatic service health and initial dependency setup are not claimed inside a universal 100-network-request cap. Thirteen synthetic telemetry rows were created across disposed databases, including the failed fifth row; none remain. Final owned cleanup completed at `04:42:40Z`, 42m30.8s after the recorded cycle start, with 548.32 seconds of summed recorded stack execution. No fresh stack reset the aggregate business/time budget. No further Docker business test was run after this window closed.

Both corrected core and durable-entry runs passed. The final private proof SHA-256 is `905870569d8dc8b91aacdb39bf49a26a17c0e13c2b58312896c8c8e7230138ef`. The ignored operator packet retains per-attempt raw logs, full synthetic readbacks, source fingerprints, configuration/image digests and exact ownership/cleanup receipts. Every recorded task container, network, volume and custom runner image was removed; unrelated resources and official image caches were retained. Final source-only changes clarify report labels, complete-cleanup reporting and required explicit budgets; request/auth/row/Action logic remains the executed logic, with offline guard validation and no post-window rerun.

### Reproduce deliberately, locally

The opt-in [local orchestrator](../../scripts/logging/local-supabase.py) and [driver](../../scripts/logging/local-supabase-driver.mjs) have no import-time effects. Supply a clean accepted c96 checkout, the six flat original archives, the official SDK archive above and a private cache/output directory. Pull the seven exact `IMAGE_PINS` digests first (Node/runtime-deps select `linux/amd64`); the remaining services use the host platform. The orchestrator fetches only the four hash-pinned official gateway files, refuses remote Docker endpoints and checks every source/archive/SDK identity before creating resources. Setup/interruption failures require inspection of the task ownership record before any label-checked recovery; a partial proof is never acceptance.

```text
python3 scripts/logging/local-supabase.py --help
python3 scripts/logging/local-supabase.py --source <clean-c96> --inputs <original-flat-archives> --sdk <sdk109-linux-x64.tar.gz> --cache <private-cache> --output <new-private-output> --deadline-epoch <original-batch-deadline> --target-budget-remaining <measured-remaining> --health-budget-remaining <measured-remaining>
```

The three budget flags are mandatory; use the original batch deadline and measured remaining counts across attempts. Reusing an output directory is refused. Private files use mode 0600 and the network is internal with no public listener. Offline admission tests run with `python3 scripts/logging/local-supabase.test.py` and in Logging CI; they do not start Docker or contact a service. A successful local run establishes real self-hosted component interoperability for these exact synthetic cases. Cloud deployment settings, hosted-runner networking/trust, GitHub job failure handling and registry access are a separate evidence profile, supplied by the hosted record above.

### First hosted attempt and token protocol correction

The first actual hosted attempt, run `41626611541` / workflow run number 14 / attempt 1 at source `10743bc`, **failed** before the first published Action invocation. Original archive acquisition, Scribe exit 0, APR exit 7 with `continue-on-error`, independent record checks and two ordinary-user metadata-only preflight GETs succeeded. The first namespace-pull token response failed the helper's `pull-response` check; all seven Action POST steps, remaining eight reads and final proof/artifact upload were skipped. There is no final hosted qualification proof, full readback or retained hosted acquisition report from this failed run. The two preflight views were empty for the selected synthetic event keys; this is not a global database count or rollback claim. Four temporary task-owned Actions Secrets were subsequently removed with ownership and absence readback; the two test users and schema were retained.

Offline counterexamples independently exposed an overly restrictive parser: it required explicit lifetime of at least 120 seconds, mandatory `issued_at`, and 65 seconds remaining. The [Distribution token specification](https://distribution.github.io/distribution/spec/auth/token/) allows an omitted lifetime (default 60 seconds) and omitted issue time (exchange completion). This compatibility defect is verified; the exact rejected field in run 14 remains unknown because its opaque response was not retained.

The focused correction accepts an opaque bounded `token` or `access_token`, rejects inconsistent aliases and malformed supplied metadata, defaults omitted lifetime/issue time, accepts explicit integer lifetimes 60–3600 seconds and requires strictly more than five seconds remaining at handoff. Supplied issue timestamps must be valid and within bounded clock skew/staleness. Closed refusal reasons identify parser branches without exposing response strings, token hashes, claims, headers or timestamps. This margin does not guarantee a download completes before expiration; existing registry authentication, immutable archive hashes and controlled failure remain binding. Endpoint/scope, read-only principal, body/time limits, no redirects/retries, mask-before-same-job-output and the ordinary Supabase JWT 1500/65-second checks remain unchanged. The source correction was verified offline before the separately admitted successful run 15. That success demonstrates current compatibility, not the exact rejected field in run 14. Future operations still require concrete bounded admission under the user's standing same-scope authorization, with no blind or automatic retry.
