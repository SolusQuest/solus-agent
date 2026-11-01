# Bounded telemetry CLI and Action

The implemented upload step combines `Solus.Logging.Cli` with the standalone [JavaScript Action](../../actions/logging/action.yml). It runs one admitted neutral record through the existing [bounded Supabase delivery](logging-supabase.md). The supported consumer host is Ubuntu 24.04 x64, managed .NET 10. The self-contained payload includes its runtime; consumers install no SDK and compile no source. This is source/build preparation and controlled execution evidence. No CLI payload location or release is published by this work.

## Consumer workflow

The Action source and the downloaded executable have separate identities. Pin the Action to an immutable reviewed commit and select the exact CLI version, authorized HTTPS URL and independently trusted compressed SHA-256 together. The following source pin contains this implementation; payload variables must be populated from a separately prepared and authorized release record. They are placeholders for operator configuration, not evidence of an existing release. Do not obtain the trusted digest solely from the same untrusted download response.

```yaml
- name: Product invocation
  id: product
  uses: owner/product-action@REVIEWED_PRODUCT_COMMIT
  # The product retains its existing business outputs and result policy.
- name: Upload available telemetry
  id: telemetry
  if: always()
  timeout-minutes: 4
  uses: SolusQuest/solus-agent/actions/logging@c35f54aa57300152c1ae68d269377e7a4e3f40a9
  with:
    telemetry-json: ${{ steps.product.outputs.telemetry-json }}
    adapter: supabase
    supabase-url: ${{ vars.TELEMETRY_SUPABASE_URL }}
    supabase-publishable-key: ${{ secrets.TELEMETRY_PUBLISHABLE_KEY }}
    supabase-access-token: ${{ secrets.TELEMETRY_INGEST_JWT }}
    strict-delivery: 'false'
    cli-version: ${{ vars.TELEMETRY_CLI_EXACT_VERSION }}
    cli-artifact-url: ${{ vars.TELEMETRY_CLI_AUTHORIZED_URL }}
    cli-artifact-sha256: ${{ vars.TELEMETRY_CLI_TRUSTED_SHA256 }}
    # Only if the selected artifact path requires a separate read credential:
    artifact-read-token: ${{ secrets.TELEMETRY_ARTIFACT_READ_TOKEN }}
```

`delivery-status` is exactly `stored`, `duplicate`, `skipped` or `failed`. `diagnostic-code` is a fixed bounded code. Empty/JSON-whitespace input produces a visible `skipped` / `input_unavailable` notice with no artifact acquisition or storage request. Missing telemetry is unavailable, never fabricated zero usage. A present malformed record fails. Default delivery failure emits a warning and exits successfully; `strict-delivery: 'true'` fails this logging step. It does not rerun the product or replace its business result. A prior failed job remains failed under normal GitHub workflow rules. The wrapper cannot recover outputs that an earlier killed process never produced.

GitHub renders Action inputs before the JavaScript starts. Supply credentials through the `secrets` context or values already masked by the producer; sensitive signed artifact URLs also require that treatment. The wrapper masks credential inputs before its own use, which cannot retroactively mask runner pre-start logs. Keep confidential content out of telemetry and follow the neutral extension admission policy. The wrapper emits no record, child output, raw server body, URL or exception. Mask control commands are the only intentional credential-bearing protocol output. Optional artifact-read authority is never forwarded to the CLI/storage; storage credentials never accompany artifact downloads. No ambient `GITHUB_TOKEN`, proxy, cookie, database password or credential refresh is discovered.

## Input and finite limits

Inline `telemetry-json` is bounded to 64 KiB UTF-8. Linux has a smaller per-environment-string limit than the 256 KiB neutral record limit; a very large inline GitHub input can fail before the wrapper starts and therefore cannot produce outputs. For a larger admitted record use `telemetry-json-file` instead. It is mutually exclusive with nonempty inline input and accepts one same-job regular file, at most 256 KiB, under the actual `GITHUB_WORKSPACE` or `RUNNER_TEMP`. Symlinks, symlink ancestors and out-of-scope paths fail. The opened descriptor is checked against the original inode and its resolved `/proc/self/fd` path before bounded reading. The wrapper reads once and passes the original bytes to CLI stdin. It does not move files between jobs, stream records or watch changing files. Producers must finish writing before invoking the uploader.

```yaml
# Producer has already written this same-job file; no cross-job artifact transfer.
- if: always()
  timeout-minutes: 4
  uses: SolusQuest/solus-agent/actions/logging@c35f54aa57300152c1ae68d269377e7a4e3f40a9
  with:
    telemetry-json-file: ${{ runner.temp }}/product-telemetry.json
    supabase-url: ${{ vars.TELEMETRY_SUPABASE_URL }}
    supabase-publishable-key: ${{ secrets.TELEMETRY_PUBLISHABLE_KEY }}
    supabase-access-token: ${{ secrets.TELEMETRY_INGEST_JWT }}
    cli-version: ${{ vars.TELEMETRY_CLI_EXACT_VERSION }}
    cli-artifact-url: ${{ vars.TELEMETRY_CLI_AUTHORIZED_URL }}
    cli-artifact-sha256: ${{ vars.TELEMETRY_CLI_TRUSTED_SHA256 }}
```

The wrapper validates host, scalar options, bounded input, JSON syntax, adapter and explicit storage/artifact configuration before acquisition. Full neutral semantic admission uses the authoritative .NET validator in the acquired CLI before any storage access. Bootstrap downloads contain no telemetry or storage credential. An invalid artifact, platform or configuration cannot reach storage. Only `adapter: supabase` exists; no adapter discovery or placeholder implementation is supplied.

| Input | Default | Supported limit |
| --- | --- | --- |
| `maximum-attempts` | `3` | 1–10 |
| `timeout-ms` | `10000` | 1–120000; CLI input, admission and delivery combined |
| `retry-delay-ms` | `200` | 0–10000; fixed, no server extension |
| `strict-delivery` | `false` | exactly `true` or `false`; malformed policy fails closed |
| Artifact acquisition | — | 30 seconds total, at most 3 HTTPS redirects, 128 MiB compressed |
| Archive expansion | — | 256 MiB total, 512 flat files, 64 MiB per file |
| CLI identity handshake | — | 5 seconds, no input/storage credential |
| Child stdout/stderr | — | 1024 bytes each; consumed privately, never echoed |

The wrapper has an overall deadline of 45 seconds plus `timeout-ms`, including download, verification and process phases. Delivery process supervision allows the CLI deadline plus 500 ms, then requests SIGTERM, allows one second and sends SIGKILL; a final bounded reap wait is one second. SIGINT/SIGTERM propagate cancellation. Each child has only explicit storage credentials when delivering, controlled runtime settings and host locale/public CA trust paths (`SSL_CERT_FILE`/`SSL_CERT_DIR`); Action inputs, artifact tokens and ambient runtime injection variables are excluded.

Ordinary deadline/cancellation returns a bounded failure where output remains possible. A runner hard kill or job timeout can suppress even final outputs, masks or cleanup; no mechanism guarantees delivery or rolls back a committed request. Retry only the same immutable producer event after an uncertain result; a duplicate means the existing equal record, and an identity collision fails without overwrite. No failure requests product replay. The parent workflow should reserve enough finite step/job time for the desired cleanup window.

## CLI protocol and candidate payload

The CLI reads exactly one strict UTF-8 neutral JSON record from stdin, including whitespace in the 256 KiB bound. Credentials are supplied only as `SOLUS_LOGGING_PUBLISHABLE_KEY` and `SOLUS_LOGGING_ACCESS_JWT` environment values. Endpoint is a project HTTPS origin. Example after acquiring/verifying the authorized payload:

```sh
# Set the two environment variables from the operator's existing secret mechanism.
./Solus.Logging.Cli --adapter supabase --endpoint https://PROJECT.supabase.co \
  --maximum-attempts 3 --timeout-ms 10000 --retry-delay-ms 200 < telemetry.json
```

Unknown/duplicate options, unsupported adapters and out-of-range policies fail. Empty input or JSON whitespace (space, tab, CR, LF) skips without credentials; a BOM or other non-JSON whitespace fails. Nonempty malformed input fails before sink construction. Handled results write exactly one JSON line with `deliveryStatus`, `diagnosticCode`, and integer `attempts` (0–10); exit 0 means stored/duplicate/skipped, exit 1 means failed. The Action owns warn/strict conversion. `--version` alone prints protocol `1`, exact informational version and actual runtime identifier, and performs no delivery.

Diagnostics are `none`, `input_unavailable`, `invalid_config`, `invalid_record`, `record_too_large`, `unsupported_schema`, `unsupported_platform`, `artifact_unavailable`, `invalid_artifact`, `process_failure`, `authentication_failed`, `schema_rejected`, `identity_conflict`, `rejected`, `protocol_failure`, `attempts_exhausted`, `deadline_exceeded`, `cancelled`, or `sink_failure`. Bootstrap/process failures may not know the CLI attempt count. Failure does not imply that storage has no row.

Maintainers prepare an unpublished candidate from source:

```sh
dotnet test tests/Solus.Logging.Tests/Solus.Logging.Tests.csproj -c Release --filter "FullyQualifiedName~Cli"
node scripts/logging/build-payload.ts --output artifacts/logging-cli
node --test tests/logging-action/*.test.ts
```

The builder uses self-contained `dotnet publish -r linux-x64`, with no AOT, trimming or single-file mode. Default candidate version is `0.1.0-internal.1`; `--version` selects an exact candidate version. It writes the versioned `.tar.gz`, `.sha256`, and `payload.json` report. It performs no remote publication. Builder version, manifest version and executable informational version use the identical exact spelling, with no SDK source suffix.

The owned archive format is gzip plus a narrow canonical flat USTAR subset. The SHA-256 of the entire compressed archive must match before extraction. Headers/checksums, unique ASCII flat names, regular files, size/padding/terminator, exact version/RID, manifest file hashes, ELF64 x86-64 apphost and included runtime are verified before writes. Links, directories, PAX, traversal, duplicates and trailing data fail closed. Extraction uses a new private directory with exclusive file creation; the actual `--version` handshake precedes storage credentials. Redirects cannot downgrade HTTPS; the optional read token is permanently stripped when crossing an origin. General-purpose tar layouts are deliberately unsupported.

## Validation evidence and limits

The CLI tests execute real admission/delivery code and generate an admitted exact 256 KiB fixture. Archive/protocol tests run against the built payload. On Ubuntu 24.04 x64, the Node suite runs the actual Action entrypoint, downloads and verifies that payload, starts its actual CLI, and serves controlled HTTPS RPC backed by disposable PostgreSQL with the actual migration and non-owner/non-BYPASSRLS writer. It proves nested JSONB and exact numeric retention, first/duplicate/collision, lost committed response, bounded faults, masks, file/inline limits, no invalid-input storage access, response stalls, cancellation and a SIGSTOP child requiring hard kill. Standard synthetic CA trust is scoped to child processes; no TLS verification bypass exists.

The failed producer subprocess exits 7 once and retains its result after upload. Logging CI additionally invokes the real local Action with `if: always()` after a `continue-on-error` failed step and checks its original `outcome: failure` plus empty-input outputs. That CI probe establishes step outcome preservation; its successful job conclusion alone does not prove a failed job conclusion. A local macOS run reports Linux process tests explicitly unexecuted; exact-head Ubuntu proof is required before readiness.

These wrapper tests do not qualify hosted JWT signatures, an operator's Supabase/PostgREST deployment or published payload access. The separate [telemetry candidate track](logging-package-consumption.md) now composes independent installed-package products through this real wrapper/CLI to controlled storage and records exact package/payload selection. Immutable publication, consumer access and bounded live qualification remain separate L1 work and authorization.

Relevant platform sources: [GitHub Action metadata](https://docs.github.com/en/actions/reference/workflows-and-actions/metadata-syntax), [workflow commands and masking](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-commands), [runner input rendering](https://github.com/actions/runner/blob/main/src/Runner.Worker/Handlers/Handler.cs), [Linux argument/environment limit](https://github.com/torvalds/linux/blob/master/include/uapi/linux/binfmts.h), and [.NET deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/).
