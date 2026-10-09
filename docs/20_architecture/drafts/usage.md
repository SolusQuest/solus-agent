# Run limits and usage draft

`SolusAgent.Api.Usage` exchanges requested limits and immutable measurements through `AgentRequest.UsageLimits`, `AgentProgress.Usage` and `AgentOutcome.Usage`. The separately compiled [ScriptedUsageAgent](../../../tests/SolusAgent.ApiOnlyConsumer/ScriptedUsageAgent.cs) and [UsageConsumer](../../../tests/SolusAgent.ApiOnlyConsumer/UsageConsumer.cs) exercise this public path with Api as their sole production dependency. This M1 draft expresses honest observations; it supplies no production admission, settlement, billing or provider transport engine.

## Requested and supported guarantees

A configured value alone does not prove enforcement. Hosts require the relevant advertised capability, or the implementation must reject it before work and progress. `UsageReporting` promises truthful nullable observations and retention across later failures, not complete provider measurements. The original synthetic execution agent does not advertise the new guarantees and rejects them when required.

| `AgentUsageLimits` dimension | Capability | Meaning when supported |
| --- | --- | --- |
| `MaximumLogicalCalls` | `DispatchLimits` | Admit a new logical call before dispatch. |
| `MaximumPhysicalDispatches` | `DispatchLimits` | Admit every physical attempt, including retries, before dispatch. |
| `MaximumToolInvocations` | `ToolInvocationLimit` | Reserve an entire tool batch before effects, charge interface entries including failed/unknown effects, and release only unstarted reservations. |
| `InputTokenThreshold`, `OutputTokenThreshold` | `UsageThresholds` | Check cumulative observed core counts after responses to stop subsequent work; actual consumption may overshoot. |

Configured values must be positive; null means unconfigured. Unsupported optional guarantees remain advisory. Reporting, count admission and token thresholds are independent capabilities. Duration enforcement is unsupported by the synthetic usage agent. Actual deadlines, provider admission and product rate/campaign policy remain later or downstream work.

## Measurements and provider facts

`UsageObservation` keeps nullable nonnegative input/output counts and copied optional provider counters. `Complete` means both core counts are known, including valid zero; `Partial` means some measurement is known while a core count is missing; `Unavailable` means no core or detail count is known. Core completeness never asserts that optional provider details exist.

`ProviderTokenCounter` distinguishes cache-read, cache-write, independently reported uncached input and reasoning counts. Its producer declares `IncludedInInput`, `IncludedInOutput` or `Independent` according to actual provider semantics. An uncached-input fact cannot be declared as an output subset, even when its value is zero or unknown; its axis is inherent in the kind. Cache and reasoning kinds do not assert a fixed axis: their relationship remains producer-declared according to provider semantics. A known included detail cannot exceed its known parent. Missing parents or peers are never reconstructed, overlapping details are never summed, and independent counters are not constrained by a differently scoped total. Negative counts, duplicate kinds and undefined classifications reject explicitly with safe fixed diagnostics. No provider wire content or reasoning text is retained.

The [value samples and tests](../../../tests/SolusAgent.ContractTests/Usage/UsageValueTests.cs) compile known zero, partial, unavailable, independent and overlapping facts. They include `UncachedInput=3` with input total and cache-read absent, preserving both missing peers instead of deriving them.

## Attempts, inventory and accounting

`UsageAttemptObservation` carries the Host execution identity, a stable logical-call identity, a distinct physical-attempt identity and a positive per-call ordinal. `AgentRunUsage` copies the attempts and rejects cross-run association, duplicate physical identities or duplicate logical/ordinal pairs. Complete inventory requires every ordinal from one through the last attempt in each logical call. Partial inventory may omit attempts and must not establish whole-run totals. Unavailable inventory is empty and unknown; complete empty inventory explicitly proves no attempt up to that snapshot. Inventory coverage and individual measurement completeness are independent.

`DispatchExposure` distinguishes known no-dispatch, known dispatch and unknown occurrence. Any positive actual core or detail consumption proves dispatch and requires `Dispatched`. Unknown exposure can carry unavailable or zero-only measurements and an independent conservative charge. Known no-dispatch rejects positive conservative unobserved charge. A zero observation does not itself prove remote work stopped.

`UsageAccounting` separately reports optional in-flight reservation, settlement classification, conservative unobserved charge and estimated cost. Null means unreported, not zero. A conservative charge is a producer/product claim for unobserved exposure, excluding known consumption; it never becomes measured usage. Settlement may be unknown, unsettled or settled independently of measurement completeness, but settled accounting cannot retain an in-flight reservation. Estimated cost is a nonnegative decimal plus a bounded three-uppercase-letter currency label; neither format nor amount promises currency support, accurate rates or an invoice. This contract does not calculate charges or estimates.

## Publication and stopping order

The production [runtime tool adapter](runtime-tools.md) projects independent `ToolInvocationUsage` through `AgentRunUsage.ToolInvocations`. Its nonnegative `Invoked`, `ReservedUnstarted` and `ReleasedUnstarted` counts describe invocation-interface entry and provisional/released admission, not proven business effects or per-tool token usage. Null means the producer lacks tool knowledge; known zeros do not depend on provider inventory coverage. The runtime reports immutable snapshots after batch cleanup in terminal outcomes and at its existing successful-turn progress points. The synthetic usage agent does not advertise or enforce this additional capability.

The finite synthetic script captures a distinct `Dispatched/Unavailable` attempt at its actual dispatch point before awaiting measurement. It updates the retained immutable snapshot before every synchronous progress callback. Known measurements are published before response validation; rejection, validation exception, cancellation or a throwing observer cannot erase the captured snapshot from the final outcome. Ordinary progress remains optional, so retention does not depend on an observer being present.

Rejected responses may use the next scripted attempt for the same logical call, with a new physical identity and incremented ordinal. Successfully validated work is counted and reported; unnecessary retry alternatives are skipped. Final successful task completion takes precedence over a post-response threshold or cancellation arriving during final successful work. A synchronous observer failure still returns the fixed failure code and retained completed count, including on the final report.

With work remaining, caller cancellation stops further work before other resource checks. Supported work/count limits prevent the next operation. Token thresholds compare checked cumulative known core counts across all dispatched attempts, including rejected retry responses. Reached or exceeded known thresholds stop future work as `ResourceLimit`; a required missing comparison or arithmetic overflow stops as `Partial`, retaining individual observations without filling zero or saturating totals. Only configured dimensions require comparison. Terminal response validation failure remains `Failed`; an admissible retry must pass the next boundary checks. Cancellation after dispatch retains dispatch exposure and measurement uncertainty, without asserting remote stop or settlement.

[Usage execution tests](../../../tests/SolusAgent.ContractTests/Usage/UsageExecutionTests.cs) call the actual Api-only producer and consumer, including deterministic cancellation during pending measurement, retry correlation, observer failures, final-versus-remaining threshold neighbors, unknown comparisons and cumulative overflow. [Project validation](../../00_project/validation.md) owns the focused/full commands. The [security boundary](../security-boundary.md) applies: ordinary usage contains only closed metadata, numeric facts and the bounded currency label, with no input, candidate, context, credentials, raw provider response or exception payload. M3 owns production enforcement and accounting; products retain business acceptance and side effects.
