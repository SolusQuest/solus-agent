# L1 published Action live qualification

Status: **PREPARED — NOT EXECUTED**. [Issue #124](https://github.com/SolusQuest/solus-agent/issues/124) remains open. This record describes the bounded source preparation; it contains no hosted Supabase result, actual qualification run ID or qualification acceptance. The [release record](logging-l1-release-record.md) separately owns the completed private publication, package access and actual repository-token acquisition proofs from #123.

The selected caller is `SolusQuest/solus-agent` (repository ID `1406091678`), using the existing registered `logging-release.yml` workflow (`403167763`) on `codex/l1-124-live-qualification`, with the isolated `qualify-live` job. Its token has `contents: read` and `packages: read`. Existing preparation, publication and diagnostic jobs remain unchanged and are excluded by mode. No downstream repository write or repeated publication is part of this qualification.

## Immutable released inputs

The source is `c96a1b5cf9746a7fa569a339c583e07e3e103ee6`, version `0.1.0-internal.1`, candidate `9bff5f5737aac81adbea85179b248a52136ff7ab041acc4f85c7aa4a596c0e84`, package set `a045cbbdd10fa9df19c0d7405be426a85b3b4b116aa6f83c68f48da87e7974e7`. The independent [accepted lock](../../scripts/logging/accepted-candidate.json) retains every original whole package/DLL hash and dependency.

The job cold-acquires the original four `.1` packages plus the original `SolusAgent.Api` `.4` input from the named private GitHub NuGet feed. Whole archive hashes are checked before inspection or MSBuild. It reuses the unchanged c96 `installed.ts` to copy and compile the independent Scribe and Apr PackageReference consumers with separate caches, local-only package feeds, exact DLL verification and no source links. Acquisition credentials do not enter their inspection/build/producer children. No library or CLI candidate is rebuilt or substituted.

Every delivery invokes `SolusQuest/solus-agent/actions/logging@9d2f3777401d71fdea84ce350dd5bd6240acb7a8`, downloading the original Linux x64 CLI blob from `ghcr.io/solusquest/solus-logging-cli`. The 36,409,125-byte archive SHA256 is `656957224f9e81ad16ba882c103eeaef51989deb77a2541ae0cab93d5f653339`; raw OCI manifest SHA256 is `9d23869078138bd159ebfb3c3686da55215cf49a00b267352d11c371ce201913`, config SHA256 `ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356`. These are released identities, not new #124 execution proof.

## External prerequisites still missing

Scoped preparation found no configured Supabase target, project reference, deployed-schema evidence or existing ordinary A/B writer credential bindings. The source repository has no configured repository secrets or environments at the preparation snapshot. No private target endpoint has been contacted. Names/availability inspection did not read secret values or search unrelated credential stores.

Before execution, the operator must identify an **existing** HTTPS project origin, deployed `public.telemetry_events` and `public.ingest_telemetry_event` matching the accepted migration, and two distinct existing ordinary authenticated writer identities. Their existing values must be supplied through an explicitly accepted credential binding mechanism. This implementation names four step-scoped repository secret bindings: `TELEMETRY_L1_SUPABASE_ORIGIN`, `TELEMETRY_L1_PUBLISHABLE_KEY`, `TELEMETRY_L1_WRITER_A_JWT`, `TELEMETRY_L1_WRITER_B_JWT`. Configuring these bindings is a separate settings operation; their absence is not permission to create credentials, users, a project or a schema. No values belong in chat, dispatch inputs, commits or proof artifacts.

The publishable key identifies the application; A/B are distinct ordinary authenticated JWT principals, not service-role/admin credentials. The helper checks issuer/subject hashes, ordinary role and remaining lifetime without claiming local JWT signature verification. The target must authenticate them and enforce the deployed RLS policy. The helper does not query or verify deployed schema definitions; root/operator must inspect and accept actual deployment evidence separately. No migration is performed automatically.

A cleanup owner and retention agreement are also required. The proposed operation retains exactly three new labeled synthetic rows. No delete/update or automatic cleanup is included; ingestion credentials cannot be presumed to clean up. Any later administrator cleanup needs its own precise operation scope.

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

## Prepared validation and evidence boundary

Run `node --test scripts/logging/live-qualification.test.ts` for credential-free admission, transport isolation, full-row oracle, fail-stop state, real child exit-7 handling and workflow isolation/freeze tests. Local synthetic transport and child fixtures prove source behavior only. The tests freeze all five prior jobs and accepted helpers/locks; no publisher or packer is imported by the new helper. Logging CI selects this test file alongside the existing tests on the actual new source head.

No actual #124 run, target authentication, stored row, duplicate, collision, isolation result, cleanup or live acceptance is recorded yet. The future metadata-only proof must bind the actual repository/run/attempt/source, original archive acquisitions, installed DLL/package identities, producer exits/once counts, seven exact Action outcomes and ten complete read comparisons. Original private inputs, raw headers/bodies/logs, endpoints, JWTs, writer subject IDs and binary archives stay outside commits and proof artifacts.

The successful #123 outer repository-token acquisition proofs remain distinct from their inner local verifier's `registryAccessVerified: false` and `execution: null` fields. Neither those prior proofs nor this preparation establishes full product adoption, model requests/billing, an Action live-storage result, a durable product pipeline or hosted Supabase qualification. Final #124 acceptance requires the actual bounded operation and reviewed readback evidence.
