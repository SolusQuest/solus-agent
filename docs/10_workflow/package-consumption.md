# Internal package consumption

SolusAgent's first distribution is internal-only experimental NuGet consumption for authorized APR and ContractScribe consumers. The five existing production assemblies are five packages sharing one `0.1.0-internal.N` version. The selected destination is private GitHub Packages; no public NuGet release, public license, contribution invitation or upgrade compatibility is selected. Internal-use authorization does not grant third parties redistribution rights. Third-party adoption and a public license require a separate maintainer decision; retain the applicable notices and license basis for any adopted dependencies or source.

## Package boundaries and exact versions

| Package | Direct production dependencies | Consumer responsibility |
| --- | --- | --- |
| `SolusAgent.Api` | None | Business orchestration through the outer agent interfaces. |
| `SolusAgent.Tools.Api` | None | Reusable custom function tools and narrow capabilities. |
| `SolusAgent.Runtime.Api` | Api, Tools.Api | Custom providers and Host runtime integration. |
| `SolusAgent.Runtime` | Api, Tools.Api, Runtime.Api | Startup selecting the self-owned runtime. |
| `SolusAgent.Providers.DeepSeek` | Runtime.Api | Optional explicitly selected HTTP provider. |

Dependency names in the table omit the common `SolusAgent.` prefix. All packages target managed `net10.0` and contain their own DLL and XML documentation. The adapter is optional; the core runtime has no dependency on it. See [project structure](../20_architecture/project-structure.md#reference-graph).

Pin each direct reference with NuGet exact equality, as the executable [Foundation startup project](../../tests/PackageConsumers/Foundation/Startup/Startup.csproj) does:

```xml
<PackageReference Include="SolusAgent.Runtime" Version="[$(SolusAgentVersion)]" />
<PackageReference Include="SolusAgent.Providers.DeepSeek" Version="[$(SolusAgentVersion)]" />
```

Pass an explicit `SolusAgentVersion` matching the selected published version to every consumer project. A bare version is a NuGet minimum, not exact equality. Package metadata uses SDK-generated dependency lower bounds; verify that **every resolved SolusAgent dependency**, including transitives, equals the selected version. Do not mix internal versions or accept a newer substitute. The clean verifier performs this check against the actual restored assets.

Published identities are immutable: never replace the contents of an existing package version. Publish a new internal version for a correction. Before formal release there is no upgrade guarantee for API, behavior, package dependencies or saved context. Review breaking-change disclosure and revalidate the consumer before changing its pin; retaining an old pin is distinct from a promise to support upgrades.

## Feed and authentication

The selected registry index is `https://nuget.pkg.github.com/SolusQuest/index.json`. Only use a version actually published there; the local validation version below is not a publication claim. An authorized consumer needs both usable authentication and package read access. GitHub documents a personal access token (classic) with `read:packages` for private NuGet installation; its owner must also have effective access. An Actions `GITHUB_TOKEN` needs `packages: read` and the consumer repository must have access to the package. Scope alone does not provision access. See [GitHub's NuGet authentication documentation](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry).

Supply an existing consumer token securely through the environment, without putting it in a feed URL, committed NuGet.Config, command arguments or logs. For a local PAT, set `GITHUB_TOKEN` to the existing token and explicitly set `GITHUB_ACTOR` to the token owner's personal GitHub account name; the verifier translates both into child-process `NuGetPackageSourceCredentials_selected`. The runner's placeholder username fallback is not a substitute for that account name. Alternatively supply `NuGetPackageSourceCredentials_selected` directly using NuGet's `Username=...;Password=...;ValidAuthenticationTypes=Basic` convention, with `Username` set to the token owner's account name. In Actions, provide the workflow's actor username with its authorized workflow token; a PAT used in Actions still needs its owner's username. Do not echo its value. The runner does not provision credentials or package access and writes no credential into its generated config.

For the local PAT path, after an authorized publication and with the existing token, its owner's account name and exact published version securely configured in the environment:

```sh
: "${GITHUB_ACTOR:?Set the personal GitHub account name that owns the PAT}"
: "${GITHUB_TOKEN:?Supply the existing authorized consumer PAT securely}"
: "${SOLUS_AGENT_VERSION:?Set the exact published internal version}"
node scripts/packages/verify-consumers.ts --feed https://nuget.pkg.github.com/SolusQuest/index.json --version "$SOLUS_AGENT_VERSION" --scenario foundation
```

Set `SOLUS_AGENT_VERSION` to one exact published `0.1.0-internal.N`. This command is an authenticated installation procedure, not evidence that publication or permissions have already been verified. Authentication failures require checking the supplied credential; authorization failures require checking effective package/repository access. A missing exact version requires checking the published identity and selected source. Do not work around any of these by silently enabling another SolusAgent feed or using a warm cache.

For a product's own restore configuration, clear inherited sources/fallback folders and map `SolusAgent.*` exclusively to this selected feed; map unrelated tooling to its declared source. Use the source name matching the credential environment variable. The clean runner generates this configuration, disables inherited build props/targets and central version management, and checks actual package source metadata. Its selected-source prefix takes precedence over the tooling wildcard. See [clean package foundation](../00_project/validation.md#clean-package-foundation) for the complete interface.

## Run the executable startup and extensions

Use Node.js 24.12 or newer, the SDK selected by `global.json` (currently 10.0.109 with patch roll-forward), and `unzip`. From a checkout, use a new ignored output directory containing only one version's five packages:

```sh
node scripts/packages/pack.ts --version 0.1.0-internal.89 --output .local/package-example-89
node scripts/packages/verify-consumers.ts --feed .local/package-example-89 --version 0.1.0-internal.89 --scenario foundation
node scripts/packages/verify-negatives.ts --feed .local/package-example-89 --version 0.1.0-internal.89
```

The pack command refuses same-version overwrite; use a fresh output directory for a repeat local experiment. These commands build and consume local artifacts without publishing. They use no model credential or live provider. The verifier copies the required projects outside the repository, restores with fresh package/HTTP/plugin caches, builds every project in Release and executes every manifest entrypoint. Missing scenarios or projects fail rather than silently reducing coverage.

The [Foundation manifest](../../tests/PackageConsumers/Foundation/scenario.json) declares four independent projects and one executable entrypoint. These maintained sources are the executable examples:

| Example | Source and observable behavior |
| --- | --- |
| Public startup | [Program.cs](../../tests/PackageConsumers/Foundation/Startup/Program.cs) calls `RuntimeAgentFactory.Create` with a provider, registered tool/capability and exposure hooks, then supplies `IAgent` to business code. |
| Api-only business | [BusinessWorkflow.cs](../../tests/PackageConsumers/Foundation/Business/BusinessWorkflow.cs) creates a finite request, invokes `IAgent.ExecuteAsync` and checks completion and usage. |
| Tools.Api-only extension | [IncrementTool.cs](../../tests/PackageConsumers/Foundation/CustomTool/IncrementTool.cs) defines normalized schemas, a narrow counter capability and one guarded mutation. |
| Runtime.Api-only extension | [TwoTurnProvider.cs](../../tests/PackageConsumers/Foundation/CustomProvider/TwoTurnProvider.cs) implements bounded provider exchange, dispatch/usage observation and exact tool-result association; its hooks acknowledge permission and closure. |

The executed startup asserts two provider turns, one tool effect, two permissions, two closures and known per-attempt usage. Separate bounded scripts complete one logical call on attempt 65 and 128 through the Api-only business consumer, retaining exact logical/physical correlation, consecutive ordinals and all usage; each attempt receives its own permission and closure. The Host explicitly selects the larger retry permission and runtime inventory. Compiled assembly checks preserve the independent business/tool/provider boundaries. It also constructs the optional DeepSeek adapter using a synthetic unused credential and a terminal no-network handler. This proves public construction, not a DeepSeek request or live interoperability.

The negative verifier executes 12 intended rejections: overwrite, missing package despite a warm cache, exact-version substitution, wrong selected source despite alternate availability, missing scenario/project, foreign project reference, linked source, direct assembly reference, external MSBuild import, friend assembly identity and private implementation access. It then successfully runs an independently named scenario through the same discovery interface.

## Fresh execution and supplied context

Foundation executes a fresh ordinary request; it does not exercise restoration. The factory's returned agent also implements `IContextAgent`, `ICandidateAgent` and `IContextCandidateAgent`. Choose the context lifecycle explicitly using [ContextExecutionIntent](../../src/SolusAgent.Api/Context/ContextExecutionRequest.cs):

- `Fresh`: supply no envelope or round grant. A Scribe-shaped Host can reconstruct current input from validated business progress without retaining a runtime transcript.
- `NewRunFromContext`: supply a compatible checkpoint to start distinct Host-authorized work. Ordinary context requires a safely closed accepted Final; candidate context requires known End.
- `ContinueRun`: supply a supported unfinished checkpoint with matching continuation semantics. Current instructions and empty new data are required by the ordinary continuation route. This does not authorize replay of unknown effects.

Supplied Runtime context requires a `ContextRoundGrant`, provider persistence support and freshly injected `IRuntimeContextAuthority`. The Host must independently verify the exact selected bytes/metadata and actual effect targets and exclusively claim the current source/round and grant across processes. A hash packaged with untrusted bytes is not trusted provenance. Inject current provider, tools, credentials, hooks and controls again; they are not serialized authority. Combined candidate/context execution requires exposure hooks even when ordinary startup permits their omission.

Check admission and capture separately from the work result. Invalid, incompatible, stale, unsupported or already-consumed context rejects explicitly without work or silent fresh fallback. A failed provider import consumes the claim without dispatch. Treat a rejection after an upgrade as a consumer decision: retain the compatible pinned runtime/context pair, or deliberately start fresh from independently validated business progress when product policy permits. Do not rewrite envelope discriminators or retry an uncertain side effect to force acceptance. No migration reader or cross-version context guarantee is supplied.

Current-round allowances and duration renew on authorized restoration; historical usage remains separate, while retained record/byte capacities remain cumulative. Completed tool results are retained without invoking them again. Partial recovery needs the explicitly bound supported recovery guarantee and Host authority; unknown mutation or unresolved candidate feedback cannot authorize blind replay. Restricted capture uses a separate call-scoped sink; delivery does not prove durable storage or guaranteed capture on process death. See the [context draft](../20_architecture/drafts/context-envelope.md#production-ordinary-context-c1), [candidate context](../20_architecture/drafts/context-envelope.md#candidate-protocol-state-r3) and [owning validation](../00_project/validation.md) for supported transitions and actual controlled process evidence.

## Support and qualification boundaries

The selected internal environment is managed .NET 10 on `ubuntu-24.04` x64, without Native AOT or trimming qualification. The ordinary [package-foundation CI job](../../.github/workflows/ci.yml) explicitly selects that host and executes the local-package checks with `0.1.0-internal.1`. Use the successful run at the reviewed/released revision as Linux evidence; a queued job or another revision is not proof. The local `.89` example and CI `.1` execution use different inputs.

Historical local results and exact-head CI evidence belong to [PR #97's validation record](https://github.com/SolusQuest/solus-agent/pull/97), scoped to the tested revisions and execution inputs recorded there. They do not qualify later packages or add the local development platform to the selected support commitment. Confirm successful CI and the owning qualification record for the actual reviewed or released revision.

Current source implements M3 run admission/accounting/retries and M4 ordinary, completed/partial-tool and candidate restoration, including controlled DeepSeek mixed process tests and APR/Scribe Host-shaped probes. These are bounded synthetic/controlled-transport results. They do not establish live service behavior, provider billing accuracy, arbitrary-kill recovery, production Host persistence or actual product migration. Missing usage remains unknown; post-response token thresholds and cancellation observation do not promise remote preemption.

The five private packages are published at `0.1.0-internal.1`. The [release record](../90_roadmap/m5-release-record.md) records all four clean offline registry consumers, actual APR and Scribe repository-token installations, private-access readbacks and their limits. The independent [bounded live record](../90_roadmap/m5-live-qualification.md) identifies the tested profile, source and observed service exchange/restoration. M5 administrative closure remains separate from those technical results; downstream product migration and the retained Scribe product CI issue remain product-owner work. Ordinary CI and release validation must not make live model calls. Actual APR Native AOT distribution, public release and full product migrations remain outside this internal release scope.

## Static feedback variant API change

Current source requires explicit `ToolArgumentError(key, code, attributes)` construction and key-based `AllowedErrorKeys`, denials and dispositions. Up to 30 custom variants plus two builtins can share wire codes with distinct fixed ordered envelopes; a tool may allow up to 31 eligible keys. The independent Foundation package consumer constructs 21 custom variants, checks exact same-code feedback and an 18-key tool allowlist using only its existing Runtime.Api package reference. This is local package/CI consumption evidence, not publication or downstream adoption of the change. Previously published package identities remain immutable.

The experimental source change intentionally alters policy-bearing context bindings while retaining outer format 2/compatibility 1 and provider format 1. Old policy-bearing checkpoints reject explicitly; policy-free continuation remains supported. Consumers must recompile and revalidate their current declarations and context selection before changing a package pin. No upgrade guarantee or fallback reader is introduced.

## Independent telemetry candidates

The [telemetry package-consumption guide](logging-package-consumption.md) owns the separate four-library candidate set, compatible private Api `.4`, installed CLI, clean product consumers and preparation-only manual workflow. It preserves this legacy five-package pipeline and its immutable versions. Its source-baseline fixture is never a publication input or proof of private registry access.
