# Bounded package smoke

This consumer installs the five exact-version packages and executes public Runtime startup with the actual DeepSeek adapter. Its default entrypoint runs deterministic behavioral checks with controlled transport, including two fresh processes, a completed synthetic function-tool round, restricted capture and `ContinueRun` restoration. It never looks up ambient provider credentials or constructs a network transport in offline mode.

From the repository root, first pack an unused internal version, then run the local command or common verifier with that exact version and selected feed:

```sh
node scripts/packages/pack.ts --version 0.1.0-internal.1 --output artifacts/packages
node tests/PackageConsumers/LiveSmoke/run.ts --feed artifacts/packages --version 0.1.0-internal.1
node scripts/packages/verify-consumers.ts --feed artifacts/packages --version 0.1.0-internal.1 --scenario smoke-offline
```

`all`, the directory name `LiveSmoke` and `smoke-offline` always execute the zero-argument offline entrypoint. Ordinary CI and release validation use only this mode. The common verifier copies only this scenario's sources into a fresh workspace, validates package versions/source/cache and evaluated build inputs, then builds and executes the consumer. No repository projects or test helpers are referenced.

## Explicit live execution

A separately authorized paid invocation must select `smoke-live` and pass `--live`. Supply an explicit regular local file containing only the key, with owner read permission and no group/other or executable permissions. Files inside a Git worktree must be ignored; a secure external Gitless file is also accepted. Unknown Git failures, broken repository metadata and discovery overrides are rejected, and Git tracing is disabled for metadata probes. Keep it outside all scenario source directories; a root `.local` file is suitable. Use an explicit absolute path. Do not put a credential value in command arguments, environment variables, prompts or source. The runner inspects metadata/ignore status without reading the key; the parent consumer reads it only after its own opt-in/profile/CI checks. Missing, invalid, copied-source or unsupported inputs fail before network construction.

```sh
node tests/PackageConsumers/LiveSmoke/run.ts --feed artifacts/packages --version 0.1.0-internal.1 --scenario smoke-live --live --key-file "$SMOKE_KEY_FILE" --profile deepseek-flash-thinking-high
```

Here `SMOKE_KEY_FILE` is a user-selected absolute filename, never the credential itself. No key filename is assumed. The only profile is the official DeepSeek endpoint, `deepseek-flash`, nonstreaming, thinking enabled and reasoning effort high. Mixing live with another scenario, attaching live options to `all`/offline, changing profile, or running live under CI is rejected. The installed entrypoint independently requires the exact forwarded `smoke-live --live --key-file … --profile …` arguments. The harness does not force tool selection through a new provider option: an unexpected Final or tool request is an explicit failure, with no prompt adjustment or retry.

## Limits and authority

One parent Host supervises all child processes under a five-minute deadline and fixed ceilings: six physical transport entries, three tool-interface entries and `max_tokens=4096` per dispatch. Failed/unknown send entries remain charged. Runtime retries are absent; HTTP uses one-shot content, HTTP/1.1 exact and disabled redirects, proxies, cookies and activity propagation. The live success summary exposes only actual nullable input/output observations, current versus historical counts, elapsed milliseconds and post-disposal cleanup status. Current totals add only the two new rounds; historical observations are not charged again and unknown remains null. The parent injects the credential only into its own authenticated transport; child adapters receive synthetic transport credentials and inherit no ambient environment. This composition does not independently qualify the adapter's default owned production transport.

Input/wire bodies and response bodies are capped at 65,536 bytes, input records at 32, continuation at 8192 bytes, and retained Runtime state at 196,608 bytes/six attempts/32 records. Per-round accounting reserves 16,384 input and 4096 output units per attempt with finite allowances; unknown usage stops and is never represented as zero. These are smoke policy, byte ceilings and Host accounting estimates, not runtime defaults or billing guarantees. Any unexpected reply, profile/capacity failure or exhausted allowance stops the invocation. No limit is expanded automatically.

The producer admits one ToolCalls work unit, completes its synthetic tool and stops before another model call. The restorer uses a new PID, Runtime/provider/tool objects and source-bound grant. The parent retains the actual capture fingerprint, source checkpoint and tool-target authority in memory, separately from the serialized envelope. Tampering, stale grants and changed targets cannot authorize import or new effects. Actual controlled requests compare complete replayed messages, raw call arguments, tool result associations and required reasoning continuation; historical effects do not run again.

Only opaque restricted continuation is persisted in an owner-only temporary directory/file. The parent consumes child stdout as private bounded IPC, discards child stderr and emits only closed failure categories and counts. Secret, restricted-replay, input and exception canaries exercise success and failure paths. Normal success/failure exits remove temporary context even when the package verifier retains its build workspace. Abrupt parent/host termination, secure erasure, durable storage and remote cancellation are not qualified.

The common runner and local command preserve a failed entrypoint's `LiveSmoke failed: <Failure>.` line only when the category is in the explicit whitelist, stderr contains exactly that line (with at most one LF/CRLF terminator), stdout is empty and the process exited normally with a nonzero status. Unknown categories, added text, multiple lines, other output or incomplete execution produce a generic failure without forwarding either stream. The category is diagnostic data and grants no execution or recovery authority. `node --test tests/PackageConsumers/LiveSmoke/diagnostics.test.ts` checks the actual child/verifier/wrapper boundaries and malicious-output neighbors without keys or network.

The offline entrypoint reports a nonzero executed case count. Cases include aggregate and independently reduced parent caps, failed send/tool charging, no retries on 429/503, deadline termination of a non-cooperative fake, input/response/replay/output overflow, unknown usage, malformed replies/IPC, child exceptions, source/target/provider mismatch and replay denial, ambient-key poison, double opt-in, CI/profile/missing-key gates, exact restoration and cleanup. Controlled passing results establish package composition and safety checks; actual live interoperability requires its own authorized run and separate evidence.
