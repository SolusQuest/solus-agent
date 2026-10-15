# APR package consumer

This synthetic consumer proves restored execution from the five installed packages at one exact internal version. Business compiles against `SolusAgent.Api` alone, CustomTool against `SolusAgent.Tools.Api` alone, and Startup selects the public Runtime factory and actual DeepSeek adapter. The projects contain their own sources and use neither repository test assemblies nor friend access. P1's verifier copies them outside the checkout, evaluates the build inputs and references, and restores the selected packages with fresh caches.

After generating packages at the selected version, run from the repository root:

```sh
node scripts/packages/verify-consumers.ts --feed artifacts/packages --version 0.1.0-internal.1 --scenario apr
```

Replace the version and feed together when selecting another internal build. The existing `package-foundation` CI job executes Foundation and Apr on managed `net10.0`, Ubuntu 24.04 x64, without credentials or live provider calls. A local run on another platform establishes only that environment.

The entrypoint requires ten completed cases and 23 distinct child processes. Every child reconstructs its runtime, DeepSeek provider, controlled HTTP handler, tools, capability, business Host, exposure hooks and recovery authority. The producer exits before restoration starts. Child inputs contain only fixture location and case/stage names; restoration obtains its transcript exclusively from the captured restricted envelope. The full expected HTTP JSON is constructed independently from fixed synthetic facts, including exact reasoning, correction, tool arguments and associated results.

| Case | Required observation |
| --- | --- |
| Completed tool and candidates | Accept A, reject B, execute a tool once, then stop at the three-work-unit limit. A fresh process repairs B with no repeated tool entry or earlier submission, preserving A and its effect. A third process continues the completed checkpoint with zero work. |
| Accepted progress then 503 | The second dispatch fails after A was accepted. Renewed authorization retries the same logical operation with the next attempt ordinal and predecessor identity. A usage-less response retains null measurements and an unresolved reservation, while the independent Host campaign charge records each physical attempt once. |
| Malformed context | Reject before Host claim, import, dispatch, progress, capture, tools or candidate callbacks. |
| Truncated context | Reject at the same deterministic decode boundary. |
| Altered valid JSON | Added insignificant whitespace passes grammar but fails the independently retained fingerprint before import or work. |
| Changed actual target | An identical descriptor/capability ID cannot authorize a different injected effect target. |
| Duplicate grant | Reuse the consumed grant against the original source and shared claim store; reject without new work. |
| Competing grant | A new grant cannot claim the already consumed source round. |
| Current allowance | Admit the source but stop before dispatch when the new allowance cannot reserve an attempt, retaining earlier acceptance/effects without another historical charge. |
| Unknown candidate feedback | Preserve the earlier accepted A and unresolved later acknowledgement; restoration performs no callback, dispatch or effect. |

Business acknowledgement records and explicit applied-effect records remain separate from safe Runtime receipts and completion. Host campaign observations are keyed by physical attempt identity; repeated observation of historical usage is idempotent. Runtime current-round measurements, unresolved estimates, historical observations and fresh allowances are asserted separately. Every actual provider attempt has correlated permission and same-attempt settlement hooks. Invalid-state cases assert the owning rejection code and zero new effects, including absence of fresh fallback. The parent checks child exits, timeouts, actual PIDs, nonzero case counts and ordinary stdout/stderr confinement. Child assertions also check safe results, progress, configuration and observations; synthetic transport credentials must be absent from restricted context.

The optional verifier `--keep-workspace` flag retains the copied consumer's ignored `.local/apr` observations and final case/environment summary for local inspection. Restricted envelopes stay separate from safe observations. None of these fixture artifacts is intended for publication.

This is bounded synthetic consumption evidence, not an actual APR migration, arbitrary process-kill checkpoint guarantee, production storage qualification, distributed effect guarantee or provider billing claim. It uses no real credentials, live service, GitHub effect or Native AOT. No cross-version API or context upgrade compatibility is promised before formal release.
