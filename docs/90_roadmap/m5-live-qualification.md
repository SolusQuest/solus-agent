# M5 local live qualification

## Result: failed, publication gate not qualified

The first explicitly authorized bounded live command was executed on 2026-10-16 (Asia/Shanghai), starting at 2026-10-15T17:57:58.639598Z. It exited with the closed category `UnexpectedReply`. There was no retry, substitute credential, prompt repair or budget expansion. This result does not qualify real tool exchange, fresh-process restoration or first publication.

| Input or observation | Actual evidence |
| --- | --- |
| Tested harness revision | `46081f208179671968106e1cb1b563a8e3bcdb3b` |
| Production package build revision | `a3a0eb9436c3b783ebefacba33942dfbd420717f` |
| Packages and source | All five production packages, exact `0.1.0-internal.93`, packed and inspected in a fresh local directory; clean consumer restore uses that selected local feed and fresh caches |
| Environment | macOS 27.0.1, arm64; .NET SDK 10.0.109; Node 24.21.0 |
| Configured service/profile | Official DeepSeek endpoint; `deepseek-flash`, nonstreaming, thinking enabled, reasoning effort high; parent-controlled transport |
| Invocation | One `smoke-live` command with explicit `--live` and maintainer-selected private credential source |
| Limits | At most six physical transport entries, three tool-interface entries, 4096 output tokens per dispatch, five-minute harness deadline, no retries; existing finite input, response, replay and retained-state ceilings unchanged |
| Command wall time | 4.703 seconds, including clean-consumer preparation; separate harness elapsed time was not reported on failure |
| Exit | Nonzero; `UnexpectedReply` |
| Physical dispatch/tool/effect counts | Unknown: the failed invocation emitted no numeric summary; configured limits are not observations |
| Current/historical input/output usage | Unknown, not zero; no usage estimate or billing claim |
| Tool/capture/restoration | Not qualified: no successful completed-tool/fresh-process result was reported |
| Restricted context cleanup | Normal cleanup is implemented in the harness; no post-disposal success boolean was emitted for this failure, so that observation remains unavailable |

The prerequisite correction admits explicitly selected private Gitless credential files while preserving Git ignore checks for files in worktrees, metadata restrictions and exclusion from every scenario source root. Unknown Git errors, malformed metadata, unsafe discovery overrides and tracing cannot become a Gitless fallback. Before the actual invocation, 18 Node regression tests and 47 controlled installed-package smoke cases passed. These synthetic results establish the corrected admission and safe numeric-summary behavior, not live interoperability.

The later integration of Scribe and its documentation/CI updates changes neither production code nor this tested harness. The first failure remains cause unknown. No raw provider reply, prompt, reasoning, context, credential location/value, process/session identity or execution log is retained in this record.

## One authorized diagnostic retest: failed

After the first failure, the harness was corrected to preserve closed failure observations through its existing execution boundaries and to accept schema-valid empty tool objects containing whitespace. Controlled installed-package evidence proved the textual empty-object comparison was overstrict; it did not identify the cause of the first real failure. The original argument bytes and exact restored-wire comparison remain intact. Before the one additionally authorized invocation, all four clean consumer scenarios passed: APR 10 cases/23 child processes, Foundation, LiveSmoke 58 behavioral cases and Scribe 16 cases/23 child processes. Twenty Node regression tests passed. A controlled 503 failure also traversed the actual installed child, parent, common runner, verifier and wrapper with its safe facts and cleanup observation preserved.

| Input or observation | Actual evidence |
| --- | --- |
| Tested harness revision | `560a75febb342be9f88df072293aa5014bb7cabc` |
| Production package provenance | The same five `0.1.0-internal.93` local archives built at `a3a0eb9436c3b783ebefacba33942dfbd420717f`; production source is unchanged |
| Start | 2026-10-15T18:32:54.483006Z (2026-10-16 Asia/Shanghai) |
| Environment, profile and limits | Same environment, selected profile, explicit credential source and finite limits as the first invocation; no retries or expanded budget |
| Exit | Nonzero; `UnexpectedReply` |
| Stage and independent counts | Producer; one physical dispatch, zero tool-interface entries, zero acknowledged effects |
| HTTP and adapter observations | HTTP 200; request and response model/profile checks matched; typed `ProviderError.InvalidResponse` (ordinal 6) |
| Current observed usage | Input 307 tokens; output 49 tokens; actual typed measurements, without a billing guarantee |
| Historical observed usage | Input 0; output 0; no restored round ran and these values are not added to current usage |
| Timing | Harness 958 milliseconds; full command 4.356 seconds including clean-consumer preparation |
| Restricted context cleanup | `true`, observed after disposal/removal |
| Tool/capture/restoration qualification | Failed: no tool entry/effect or successful producer capture/restorer completion was established |

The adapter rejected the response before returning an admitted model response. The recorded aggregate reply-match flag was `true` because the child initializes it before any comparison; it is not evidence of an admitted or matching reply. The final diagnostic correction reports this unobserved comparison as null on adapter errors. This changes reporting only and does not retroactively alter either tested revision or justify another paid invocation.

There were exactly two separately authorized live commands in total. Paid testing stopped after the diagnostic retest. The evidence narrows this failure to actual adapter response rejection, but does not identify which response-contract requirement failed. Further adapter/provider diagnosis belongs to its owner and needs a separately authorized task; raw data was not retained for reconstruction. The publication gate remains unqualified and blocks first publication. No registry upload, tag, release dispatch or downstream migration was performed.

Even a later passing macOS live run would qualify only the observed service interoperability. Linux deterministic package qualification, default owned provider transport, billing accuracy, durable storage, hostile concurrent filesystem replacement, abrupt-host cleanup, macOS release support and downstream product migration are separate evidence boundaries. Internal package and saved-context upgrade compatibility is not promised before formal release.
