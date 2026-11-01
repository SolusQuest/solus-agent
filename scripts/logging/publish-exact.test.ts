import { test } from "node:test";
import assert from "node:assert/strict";
import {
    readFileSync,
    writeFileSync,
    mkdirSync,
    mkdtempSync,
    rmSync,
    symlinkSync,
    existsSync,
} from "node:fs";
import { join, resolve } from "node:path";
import { createHash } from "node:crypto";
import { tmpdir, constants as osConstants } from "node:os";
import {
    lock,
    operationContext,
    compareOriginal,
    guardOriginalOperation,
    ociTransport,
    registryBearer,
    publishOci,
    tool,
} from "./publish-exact.ts";
import {
    readNugetIdentity,
    creationAdmission,
    createNugetResource,
    requireOriginalNugetResources,
} from "./nuget-resource.ts";
import {
    classifyOriginalError,
    validateOriginalReport,
    readOriginalReport,
    runOriginalPack,
} from "./pack-exact-child.ts";
import { pack as originalPack } from "./pack.ts";
import { actualApi as originalActualApi } from "./pack-projects.ts";
import { readPackage as originalReadPackage } from "./package-archive.ts";
import { ToolFailure as OriginalToolFailure } from "./common.ts";
const hash = (v: Uint8Array) => createHash("sha256").update(v).digest("hex");
const manifest = Buffer.from(lock.ociRaw["oci-manifest.json"]);
const config = Buffer.from(lock.ociRaw["oci-config.json"]);
const digest = `sha256:${lock.manifest.payload.ociManifestSha256}`;
const context = {
    GITHUB_ACTIONS: "true",
    GITHUB_EVENT_NAME: "workflow_dispatch",
    GITHUB_REPOSITORY: "SolusQuest/solus-agent",
    GITHUB_REPOSITORY_ID: "1406091678",
    GITHUB_REF: "refs/heads/codex/l1-123-logging-publication",
    GITHUB_RUN_ATTEMPT: "1",
    GITHUB_RUN_ID: "1",
    RUNNER_OS: "Linux",
    RUNNER_ARCH: "X64",
    EXPECTED_OPERATION_HEAD: "a".repeat(40),
    GITHUB_SHA: "a".repeat(40),
    WORKFLOW_SHA: "a".repeat(40),
    SELECTED_SOURCE: lock.manifest.source.commit,
    SELECTED_VERSION: lock.manifest.version,
    EXACT_MODE: "validate-exact",
    EXACT_MEMBER: "CLI",
};
test("operator context admits only the fixed first-attempt exact source/head operation", () => {
    assert.equal(operationContext(context).mode, "validate-exact");
    for (const key of Object.keys(context))
        assert.throws(() => operationContext({ ...context, [key]: "wrong" }));
    assert.equal(
        operationContext({
            ...context,
            EXACT_MODE: "publish-one",
            EXACT_MEMBER: "Solus.Logging",
        }).member,
        "Solus.Logging",
    );
});
test("independent original lock detects source, Api, version, candidate, package-set and whole archive substitutions before immutable operations", async () => {
    for (const change of [
        (v: any) => {
            v.source.commit = "b".repeat(40);
        },
        (v: any) => {
            v.source.dirty = true;
        },
        (v: any) => {
            v.source.inventorySha256 = "0".repeat(64);
        },
        (v: any) => {
            v.version = "0.1.0-internal.2";
        },
        (v: any) => {
            v.validationBaseline.role = "source-baseline-fixture";
        },
        (v: any) => {
            v.validationBaseline.packageSha256 = "0".repeat(64);
        },
        (v: any) => {
            v.candidateSha256 = "0".repeat(64);
        },
        (v: any) => {
            v.packageSetSha256 = "0".repeat(64);
        },
        (v: any) => {
            v.packages[0].sha256 = "0".repeat(64);
        },
        (v: any) => {
            v.payload.sha256 = "0".repeat(64);
        },
        (v: any) => {
            v.payload.ociManifestSha256 = "0".repeat(64);
        },
        (v: any) => {
            v.payload.ociConfigSha256 = "0".repeat(64);
        },
    ]) {
        const value = structuredClone(lock.manifest);
        change(value);
        let operations = 0;
        let publicationGuards = 0;
        await assert.rejects(
            guardOriginalOperation(
                value,
                {},
                () => {
                    publicationGuards++;
                },
                async () => {
                    operations++;
                },
            ),
            /original-manifest/,
        );
        assert.equal(operations, 0);
        assert.equal(publicationGuards, 0);
    }
    assert.throws(() => compareOriginal(lock.manifest, {}), /byte-inventory/);
    const bytes = Object.fromEntries(
        Object.keys(lock.sizes).map((n) => [n, Buffer.alloc(0)]),
    );
    assert.throws(
        () => compareOriginal(lock.manifest, bytes),
        /original-bytes/,
    );
    let operations = 0;
    await assert.rejects(
        guardOriginalOperation(
            lock.manifest,
            bytes,
            () => {},
            async () => {
                operations++;
            },
        ),
        /original-bytes/,
    );
    assert.equal(operations, 0);
    assert.equal(hash(config), lock.manifest.payload.ociConfigSha256);
    assert.equal(hash(manifest), lock.manifest.payload.ociManifestSha256);
});
test("finite bearer request uses existing credentials without offline token and refuses unknown responses", async () => {
    let calls = 0;
    const auth = await registryBearer(
        "actor",
        "synthetic-token",
        async (url, options) => {
            calls++;
            assert.equal(
                String(url),
                "https://ghcr.io/token?service=ghcr.io&scope=repository%3Asolusquest%2Fsolus-logging-cli%3Apull%2Cpush",
            );
            assert.equal(options?.redirect, "error");
            return new Response(
                JSON.stringify({ token: "synthetic-bearer", expires_in: 180 }),
            );
        },
    );
    assert.equal(calls, 1);
    assert.equal(auth.declaredLifetimeSeconds, 180);
    for (const response of [
        new Response("", { status: 403 }),
        new Response("{}"),
        new Response('{"token":"bad token"}'),
    ]) {
        await assert.rejects(
            registryBearer("actor", "synthetic-token", async () => response),
        );
    }
});
test("actual OCI client preserves opaque upload query and bytes and never forwards upload credentials across origin", async () => {
    const calls: any[] = [];
    const transport = ociTransport(
        "synthetic",
        Date.now() + 120000,
        async (url, options) => {
            calls.push([url, options]);
            if (options?.method === "HEAD")
                return new Response(null, { status: 404 });
            if (options?.method === "POST")
                return new Response(null, {
                    status: 202,
                    headers: {
                        location:
                            "/v2/solusquest/solus-logging-cli/blobs/uploads/id?opaque=a%20b%2fc&state=1",
                    },
                });
            return new Response(null, {
                status: 201,
                headers: { "docker-content-digest": `sha256:${hash(config)}` },
            });
        },
    );
    await transport.blob(config);
    assert.equal(calls.length, 3);
    assert.equal(
        calls[2][0],
        `https://ghcr.io/v2/solusquest/solus-logging-cli/blobs/uploads/id?opaque=a%20b%2fc&state=1&digest=sha256%3A${hash(config)}`,
    );
    assert.equal(calls[2][1].body, config);
    assert.equal(calls[2][1].headers.Authorization, "Bearer synthetic");
    for (const location of [
        "https://other.invalid/upload",
        "/v2/another/blobs/uploads/id",
        "/v2/solusquest/solus-logging-cli/blobs/uploads/id?digest=bad",
    ]) {
        let writes = 0;
        const bad = ociTransport(
            "synthetic",
            Date.now() + 120000,
            async (_url, options) => {
                if (options?.method === "HEAD")
                    return new Response(null, { status: 404 });
                if (options?.method === "PUT") writes++;
                return new Response(null, {
                    status: 202,
                    headers: { location },
                });
            },
        );
        await assert.rejects(bad.blob(config), /upload-location-origin/);
        assert.equal(writes, 0);
    }
});
test("OCI existing tag must be byte-identical with exact media/digest; unknown or conflicting state refuses", async () => {
    const response = () =>
        new Response(manifest, {
            headers: {
                "content-type": "application/vnd.oci.image.manifest.v1+json",
                "docker-content-digest": digest,
            },
        });
    assert.deepEqual(
        await ociTransport("synthetic", Date.now() + 120000, async () =>
            response(),
        ).readManifest(lock.manifest.version),
        manifest,
    );
    for (const bad of [
        new Response(null, { status: 401 }),
        new Response(null, { status: 403 }),
        new Response("{}", {
            headers: {
                "content-type": "application/vnd.oci.image.manifest.v1+json",
                "docker-content-digest": digest,
            },
        }),
        new Response(manifest, {
            headers: {
                "content-type": "application/json",
                "docker-content-digest": digest,
            },
        }),
    ]) {
        await assert.rejects(
            ociTransport(
                "synthetic",
                Date.now() + 120000,
                async () => bad,
            ).readManifest(lock.manifest.version),
        );
    }
});
test("ambiguous blob start/upload is never retried; expired bearer makes no request", async () => {
    for (const failing of ["POST", "PUT"]) {
        const calls: string[] = [];
        const transport = ociTransport(
            "synthetic",
            Date.now() + 120000,
            async (_url, options) => {
                const method = options?.method ?? "GET";
                calls.push(method);
                if (method === "HEAD")
                    return new Response(null, { status: 404 });
                if (method === failing)
                    throw new Error("synthetic lost receipt");
                return new Response(null, {
                    status: 202,
                    headers: {
                        location:
                            "/v2/solusquest/solus-logging-cli/blobs/uploads/id",
                    },
                });
            },
        );
        await assert.rejects(transport.blob(config));
        assert.equal(calls.filter((v) => v === failing).length, 1);
    }
    let called = false;
    await assert.rejects(
        ociTransport("synthetic", Date.now(), async () => {
            called = true;
            return new Response();
        }).blob(config),
        /registry-bearer-expiry/,
    );
    assert.equal(called, false);
});
test("OCI protocol retains identical tag without mutation and writes raw original manifest only after both blob readbacks", async () => {
    // Tiny synthetic archive exercises protocol sequencing only, never candidate qualification.
    const bytes = {
        "oci-config.json": config,
        "oci-manifest.json": manifest,
        [lock.manifest.payload.filename]: Buffer.from(
            "synthetic protocol payload",
        ),
    };
    for (const existing of [true, false]) {
        const events: string[] = [];
        let manifestReads = 0;
        const transport = {
            async readManifest() {
                events.push("manifest-read");
                manifestReads++;
                return existing || manifestReads > 2 ? manifest : undefined;
            },
            async blob(value: Uint8Array) {
                events.push(`blob:${hash(value)}`);
                return { base: "", digest: "" };
            },
            async send(_url: string, method: string, value: Uint8Array) {
                events.push("manifest-put");
                assert.equal(method, "PUT");
                assert.equal(value, manifest);
                return new Response(null, {
                    status: 201,
                    headers: { "docker-content-digest": digest },
                });
            },
        };
        const result = await publishOci(
            bytes,
            transport as any,
            async (url) => {
                events.push("blob-read");
                return url.endsWith(hash(config))
                    ? config
                    : bytes[lock.manifest.payload.filename];
            },
        );
        assert.equal(
            result,
            existing ? "retained-identical" : "published-readback",
        );
        assert.equal(
            events.filter((v) => v === "manifest-put").length,
            existing ? 0 : 1,
        );
        if (!existing)
            assert.equal(
                events.slice(0, 6).filter((v) => v === "blob-read").length,
                2,
            );
    }
});
test("workflow job isolation and permission guards keep ordinary preparation credential-free", () => {
    const source = readFileSync(
        new URL("../../.github/workflows/logging-release.yml", import.meta.url),
        "utf8",
    );
    const prepare = source
        .split("  prepare:")[1]
        .split("  reconcile-contracts:")[0];
    assert.match(prepare, /if: inputs.mode == 'prepare'/);
    assert.doesNotMatch(prepare, /packages: write|GITHUB_TOKEN|secrets\./);
    assert.match(
        source,
        /if: inputs.mode == 'validate-exact' \|\| inputs.mode == 'publish-one'/,
    );
    assert.match(source, /test "\$GITHUB_RUN_ATTEMPT" = 1/);
    assert.match(source, /test "\$EXPECTED" = "\$WORKFLOW_SHA"/);
    assert.equal((source.match(/packages: write/g) ?? []).length, 1);
});

// Exact NuGet resource/admission production-boundary tests follow below.
test("OCI wrong blob readback prevents manifest publication; rejected media or ambiguous manifest receipt has no retry", async () => {
    const bytes = {
        "oci-config.json": config,
        "oci-manifest.json": manifest,
        [lock.manifest.payload.filename]: Buffer.from(
            "synthetic protocol payload",
        ),
    };
    let puts = 0;
    const transport = {
        readManifest: async () => undefined,
        blob: async () => ({ base: "", digest: "" }),
        send: async () => {
            puts++;
            return new Response(null, { status: 400 });
        },
    };
    await assert.rejects(
        publishOci(bytes, transport as any, async () => Buffer.from("wrong")),
        /blob-readback/,
    );
    assert.equal(puts, 0);
    for (const unknown of [false, true]) {
        puts = 0;
        const actual = {
            ...transport,
            send: async () => {
                puts++;
                if (unknown) throw new Error("lost receipt");
                return new Response(null, { status: 400 });
            },
        };
        await assert.rejects(
            publishOci(bytes, actual as any, async (url) =>
                url.endsWith(hash(config))
                    ? config
                    : bytes[lock.manifest.payload.filename],
            ),
        );
        assert.equal(puts, 1);
    }
});

test("tool failure diagnostics close every hardcoded phase without exposing child data", () => {
    const secret = "synthetic-secret-never-log-92a7";
    const phases = [
        ["operation-checkout", "git"],
        ["registry-restore", "dotnet"],
        ["original-pack", "node"],
    ] as const;
    for (const [phase, command] of phases) {
        const records: any[] = [];
        let calls = 0;
        const fake = () => {
            calls++;
            return {
                status: 23,
                signal: null,
                stdout: Buffer.from(
                    `path=/private/${secret} auth=${secret} NU1301 MSB1009`,
                ),
                stderr: Buffer.from(
                    `${secret}: CS2008 NU1301 nu1302 xNU1000 NU12345 _CS1001 CS1002_suffix`,
                ),
            };
        };
        assert.throws(
            () =>
                tool(
                    phase,
                    command,
                    [secret],
                    `/private/${secret}`,
                    { TOKEN: secret },
                    180000,
                    fake,
                    (r) => records.push(r),
                ),
            new RegExp(`tool-failed-${phase}`),
        );
        assert.equal(calls, 1);
        assert.deepEqual(records, [
            {
                event: "exact-tool-failure",
                phase,
                exitCode: 23,
                signal: null,
                timedOut: false,
                spawnFailed: false,
                timeoutMs: 180000,
                codes: ["CS2008", "MSB1009", "NU1301"],
            },
        ]);
        const serialized = JSON.stringify(records);
        assert.ok(
            !serialized.includes(secret) &&
                !serialized.includes("/private/") &&
                !serialized.includes("auth="),
        );
    }
});
test("tool diagnostics bound scanned tails and codes, and never serialize error or malformed fields", () => {
    const secret = "synthetic-secret-not-an-error-record";
    const records: any[] = [];
    const fake = () => ({
        status: secret as any,
        signal: secret,
        error: { code: secret, message: secret, stack: secret, cause: secret },
        stdout: Buffer.from(
            "NU0000 " +
                "x".repeat(70000) +
                " NU1009 NU1008 NU1007 NU1006 NU1005 NU1004 NU1003 NU1002 NU1001 NU1000",
        ),
        stderr: {
            toString: () => {
                throw new Error(secret);
            },
        } as any,
    });
    assert.throws(
        () =>
            tool(
                "registry-restore",
                "dotnet",
                [secret],
                tmpdir(),
                {},
                180000,
                fake,
                (r) => records.push(r),
            ),
        /tool-failed-registry-restore/,
    );
    assert.deepEqual(records[0].codes, [
        "NU1000",
        "NU1001",
        "NU1002",
        "NU1003",
        "NU1004",
        "NU1005",
        "NU1006",
        "NU1007",
    ]);
    assert.equal(records[0].exitCode, null);
    assert.equal(records[0].signal, null);
    assert.equal(records[0].timedOut, false);
    assert.ok(!JSON.stringify(records).includes(secret));
    assert.deepEqual(Object.keys(records[0]).sort(), [
        "codes",
        "event",
        "exitCode",
        "phase",
        "signal",
        "spawnFailed",
        "timedOut",
        "timeoutMs",
    ]);
});
test("tool success stays quiet and invalid callsite/command/timeout cannot spawn", () => {
    let calls = 0,
        observations = 0;
    const fake = () => {
        calls++;
        return {
            status: 0,
            signal: null,
            stdout: Buffer.from("  expected-sha\n"),
            stderr: Buffer.from("synthetic-secret"),
        };
    };
    assert.equal(
        tool(
            "operation-checkout",
            "git",
            ["rev-parse", "HEAD"],
            tmpdir(),
            {},
            180000,
            fake,
            () => {
                observations++;
            },
        ),
        "expected-sha",
    );
    assert.equal(calls, 1);
    assert.equal(observations, 0);
    for (const [phase, command, timeout] of [
        ["bad-secret", "git", 180000],
        ["__proto__", "git", 180000],
        ["operation-checkout", "node", 180000],
        ["original-pack", "node", 0],
        ["original-pack", "node", 900001],
    ]) {
        assert.throws(
            () =>
                tool(
                    phase as any,
                    command as string,
                    [],
                    tmpdir(),
                    {},
                    timeout as number,
                    fake,
                    () => {
                        observations++;
                    },
                ),
            /diagnostic-callsite/,
        );
    }
    assert.equal(calls, 1);
    assert.equal(observations, 0);
});
test("real Node failure and timeout use numeric termination data and redact synthetic child credentials", () => {
    const secret = "synthetic-token-in-child-output-keep-private";
    const env = { PATH: process.env.PATH, TOKEN: secret };
    const records: any[] = [];
    assert.throws(
        () =>
            tool(
                "original-pack",
                "node",
                [
                    "-e",
                    'process.stdout.write(process.env.TOKEN);process.stderr.write("\\nCS2008 " + process.env.TOKEN);process.exit(23)',
                ],
                tmpdir(),
                env,
                10000,
                undefined,
                (r) => records.push(r),
            ),
        /tool-failed-original-pack/,
    );
    assert.equal(records[0].exitCode, 23);
    assert.deepEqual(records[0].codes, ["CS2008"]);
    assert.ok(!JSON.stringify(records).includes(secret));
    assert.throws(
        () =>
            tool(
                "original-pack",
                "node",
                ["-e", "setTimeout(() => {}, 5000)"],
                tmpdir(),
                env,
                50,
                undefined,
                (r) => records.push(r),
            ),
        /tool-failed-original-pack/,
    );
    assert.equal(records[1].exitCode, null);
    assert.equal(records[1].signal, osConstants.signals.SIGTERM);
    assert.equal(records[1].timedOut, true);
    assert.equal(records[1].spawnFailed, true);
    assert.throws(
        () =>
            tool(
                "original-pack",
                "node",
                ["-e", 'process.kill(process.pid, "SIGTERM")'],
                tmpdir(),
                env,
                10000,
                undefined,
                (r) => records.push(r),
            ),
        /tool-failed-original-pack/,
    );
    assert.equal(records[2].signal, osConstants.signals.SIGTERM);
    assert.equal(records[2].timedOut, false);
});
test("real missing executable and checkout failure plus thrown spawn exception remain bounded", () => {
    const records: any[] = [];
    assert.throws(
        () =>
            tool(
                "registry-restore",
                "dotnet",
                ["restore"],
                tmpdir(),
                { PATH: "/nonexistent-diagnostic-tool-path" },
                1000,
                undefined,
                (r) => records.push(r),
            ),
        /tool-failed-registry-restore/,
    );
    assert.equal(records[0].spawnFailed, true);
    assert.equal(records[0].exitCode, null);
    assert.equal(records[0].timedOut, false);
    assert.throws(
        () =>
            tool(
                "operation-checkout",
                "git",
                ["rev-parse", "HEAD"],
                tmpdir(),
                { PATH: process.env.PATH },
                1000,
                undefined,
                (r) => records.push(r),
            ),
        /tool-failed-operation-checkout/,
    );
    assert.equal(records[1].exitCode, 128);
    assert.throws(
        () =>
            tool(
                "original-pack",
                "node",
                [],
                tmpdir(),
                {},
                1000,
                () => {
                    throw new Error("synthetic-secret-in-exception");
                },
                (r) => records.push(r),
            ),
        /tool-failed-original-pack/,
    );
    assert.equal(records[2].spawnFailed, true);
    assert.equal(records[2].exitCode, null);
    assert.deepEqual(records[2].codes, []);
    assert.ok(!JSON.stringify(records).includes("synthetic-secret"));
});
test("production default diagnostic sink emits only its closed JSON record", () => {
    const logs: unknown[] = [];
    const original = console.error;
    console.error = (value) => {
        logs.push(value);
    };
    try {
        assert.throws(
            () =>
                tool(
                    "registry-restore",
                    "dotnet",
                    ["secret-arg"],
                    "/secret-cwd",
                    { TOKEN: "secret-env" },
                    1000,
                    () => ({
                        status: 1,
                        signal: null,
                        stdout: Buffer.from("NU1301 secret-out"),
                        stderr: Buffer.from("secret-err"),
                    }),
                ),
            /tool-failed-registry-restore/,
        );
    } finally {
        console.error = original;
    }
    assert.equal(logs.length, 1);
    const record = JSON.parse(logs[0] as string);
    assert.deepEqual(record, {
        event: "exact-tool-failure",
        phase: "registry-restore",
        exitCode: 1,
        signal: null,
        timedOut: false,
        spawnFailed: false,
        timeoutMs: 1000,
        codes: ["NU1301"],
    });
    assert.ok(!String(logs[0]).includes("secret"));
});

const sourceRoot = resolve(import.meta.dirname, "../..");
const reportHead = "a".repeat(40); // Synthetic parser context, never a remote execution SHA.
function validOriginalReport() {
    return {
        event: "original-pack-evidence",
        source: lock.manifest.source.commit,
        operationHead: reportHead,
        node: "24.21.0",
        zlib: "1.3.2.1-motley-8002e91",
        sourceVerified: true,
        sdk: {
            expected: "10.0.109",
            selected: "10.0.109",
            available: ["10.0.109"],
            failure: null,
        },
        api: { verified: true, failure: null },
        pack: { ok: true, failure: null },
    };
}
test("bridge preserves actual unchanged pack and Api guard exceptions before CLI collapse", async () => {
    const dir = mkdtempSync(join(tmpdir(), "solus-bridge-guards-"));
    try {
        const output = join(dir, "already-exists");
        mkdirSync(output);
        let failure: unknown;
        try {
            await originalPack("0.1.0-internal.1", output);
        } catch (error) {
            failure = error;
        }
        const overwrite = classifyOriginalError(
            failure,
            sourceRoot,
            OriginalToolFailure,
        );
        assert.equal(overwrite.id, "candidate_overwrite");
        assert.equal(overwrite.stage, "output-preflight");
        assert.ok(overwrite.frames.some((f) => f.module === "pack"));
        const input = join(dir, "private-input.nupkg");
        writeFileSync(input, "synthetic-invalid-archive");
        try {
            originalActualApi(input, dir);
        } catch (error) {
            failure = error;
        }
        const api = classifyOriginalError(
            failure,
            sourceRoot,
            OriginalToolFailure,
        );
        assert.equal(api.id, "genuine_api_archive_required");
        assert.equal(api.stage, "api-baseline");
        assert.ok(!JSON.stringify([overwrite, api]).includes(dir));
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
test("bridge reads real original unzip ToolFailure without logging private archive path or output", () => {
    const dir = mkdtempSync(join(tmpdir(), "solus-bridge-zip-"));
    try {
        const secret = "synthetic-private-archive-name";
        const input = join(dir, secret + ".nupkg");
        writeFileSync(input, "invalid zip");
        let failure: unknown;
        try {
            originalReadPackage(input, "SolusAgent.Api", "0.1.0-internal.4");
        } catch (error) {
            failure = error;
        }
        assert.ok(failure instanceof OriginalToolFailure);
        const record = classifyOriginalError(
            failure,
            sourceRoot,
            OriginalToolFailure,
        );
        assert.equal(record.id, "tool_failure");
        assert.equal(record.tool?.command, "unzip");
        assert.equal(typeof record.tool?.status, "number");
        assert.ok(record.frames.some((f) => f.module === "package"));
        assert.ok(!JSON.stringify(record).includes(secret));
        assert.ok(!JSON.stringify(record).includes(dir));
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
test("bridge classifies every frozen pack boundary and discards arbitrary message/stack/path data", () => {
    const cases: [string, number, string][] = [
        ["pack", 13, "output-preflight"],
        ["pack", 18, "source-inventory"],
        ["pack", 19, "toolchain"],
        ["projects", 48, "api-baseline"],
        ["projects", 59, "library-pack"],
        ["payload", 18, "payload-build"],
        ["archive", 10, "payload-archive"],
        ["pack", 34, "action-pin"],
        ["manifest", 34, "manifest"],
        ["pack", 60, "output-copy"],
        ["pack", 62, "manifest"],
        ["pack", 71, "cleanup"],
    ];
    const filenames: Record<string, string> = {
        pack: "scripts/logging/pack.ts",
        projects: "scripts/logging/pack-projects.ts",
        payload: "scripts/logging/build-payload.ts",
        archive: "actions/logging/archive.mjs",
        manifest: "scripts/logging/manifest.ts",
    };
    for (const [module, line, stage] of cases) {
        const error = new Error("synthetic-secret-not-a-source-id");
        error.stack = `Error: synthetic-secret\n    at ignored (file://${join(sourceRoot, filenames[module])}:${line}:1)\n    at hidden (/private/secret-value:1:1)`;
        const result = classifyOriginalError(
            error,
            sourceRoot,
            OriginalToolFailure,
        );
        assert.equal(result.stage, stage);
        assert.equal(result.id, "unclassified");
        assert.deepEqual(result.frames, [{ module, line }]);
        assert.ok(!JSON.stringify(result).includes("secret"));
        assert.ok(!JSON.stringify(result).includes(sourceRoot));
    }
    const literal = classifyOriginalError(
        new Error("Payload publish failed."),
        sourceRoot,
        OriginalToolFailure,
    );
    assert.equal(literal.id, "payload-publish-failed");
    assert.equal(literal.stage, "payload-build");
    const unknown = classifyOriginalError(
        { message: "secret", stack: "secret", code: "secret" },
        sourceRoot,
        OriginalToolFailure,
    );
    assert.deepEqual(unknown, {
        stage: "unknown",
        id: "unclassified",
        frames: [],
        tool: null,
    });
});
test("bridge original ToolFailure keeps bounded numeric/compiler evidence including SDK errors", () => {
    const error = new OriginalToolFailure(
        "dotnet",
        145,
        "synthetic-secret path=/private/secret NETSDK1045 NU1301 CS2008 MSB1009 Solus.Logging.csproj private-secret.csproj detected dubious ownership",
    );
    const record = classifyOriginalError(
        error,
        sourceRoot,
        OriginalToolFailure,
        "toolchain",
    );
    assert.equal(record.stage, "toolchain");
    assert.deepEqual(record.tool, {
        command: "dotnet",
        status: 145,
        codes: ["CS2008", "MSB1009", "NETSDK1045", "NU1301"],
        projects: ["Solus.Logging"],
        hints: ["git-dubious-ownership"],
    });
    assert.ok(!JSON.stringify(record).includes("secret"));
});
test("parent bridge report parser is closed metadata, never candidate qualification", () => {
    assert.equal(
        validateOriginalReport(validOriginalReport(), reportHead).pack.ok,
        true,
    );
    const mutations = [
        (v: any) => {
            v.extra = "secret";
        },
        (v: any) => {
            v.source = "b".repeat(40);
        },
        (v: any) => {
            v.operationHead = "b".repeat(40);
        },
        (v: any) => {
            v.node = "secret_value";
        },
        (v: any) => {
            v.sdk.selected = "secret-value";
        },
        (v: any) => {
            v.sdk.available = Array(17).fill("10.0.109");
        },
        (v: any) => {
            v.api.extra = "secret";
        },
        (v: any) => {
            v.sourceVerified = false;
        },
        (v: any) => {
            v.pack.failure = {
                stage: "secret",
                id: "unclassified",
                frames: [],
                tool: null,
            };
        },
        (v: any) => {
            v.pack.ok = false;
        },
        (v: any) => {
            v.api.failure = {
                stage: "api-baseline",
                id: "secret",
                frames: [],
                tool: null,
            };
        },
    ];
    for (const mutate of mutations) {
        const v = validOriginalReport();
        mutate(v);
        assert.throws(
            () => validateOriginalReport(v, reportHead),
            /original_pack_report_invalid/,
        );
    }
});
test("bridge report filesystem boundary refuses links, oversize, malformed and missing private reports", () => {
    const dir = mkdtempSync(join(tmpdir(), "solus-bridge-report-"));
    try {
        const path = join(dir, "report.json");
        writeFileSync(path, JSON.stringify(validOriginalReport()));
        assert.equal(
            readOriginalReport(path, reportHead).source,
            lock.manifest.source.commit,
        );
        const link = join(dir, "link.json");
        symlinkSync(path, link);
        assert.throws(
            () => readOriginalReport(link, reportHead),
            /original_pack_report_invalid/,
        );
        writeFileSync(path, "synthetic-secret".repeat(900));
        assert.throws(
            () => readOriginalReport(path, reportHead),
            /original_pack_report_invalid/,
        );
        writeFileSync(path, "{secret malformed");
        assert.throws(
            () => readOriginalReport(path, reportHead),
            /original_pack_report_invalid/,
        );
        assert.throws(
            () =>
                readOriginalReport(
                    join(dir, "missing-secret-path"),
                    reportHead,
                ),
            /original_pack_report_invalid/,
        );
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
test("real child boundary refuses credentials and wrong original source before loading or packing", async () => {
    const dir = mkdtempSync(join(tmpdir(), "solus-bridge-refusal-"));
    try {
        mkdirSync(join(dir, "api-fetch"));
        mkdirSync(join(dir, "candidate"));
        const input = {
            source: sourceRoot,
            output: join(dir, "candidate", "logging-packages"),
            api: join(dir, "api-fetch", "input.nupkg"),
            report: join(dir, "original-pack-report.json"),
            operationHead: reportHead,
        };
        const credential = await runOriginalPack(input, {
            PATH: process.env.PATH,
            GITHUB_TOKEN: "synthetic-secret",
        });
        assert.equal(
            credential.pack.failure.id,
            "credential_environment_refused",
        );
        assert.equal(credential.sourceVerified, false);
        assert.ok(!JSON.stringify(credential).includes("synthetic-secret"));
        rmSync(input.report);
        const identity = await runOriginalPack(input, {
            PATH: process.env.PATH,
        });
        assert.equal(identity.pack.failure.id, "original_source_mismatch");
        assert.equal(identity.sourceVerified, false);
        assert.equal(identity.pack.ok, false);
        assert.equal(existsSync(input.output), false);
        const elsewhere = join(dir, "wrong-report.json");
        await assert.rejects(
            runOriginalPack(
                { ...input, report: elsewhere },
                { PATH: process.env.PATH },
            ),
            /bridge_arguments_refused/,
        );
        assert.equal(existsSync(elsewhere), false);
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
test("exact operator installs the immutable SDK version without global-json roll-forward selection", () => {
    const workflow = readFileSync(
        new URL("../../.github/workflows/logging-release.yml", import.meta.url),
        "utf8",
    );
    const operation = workflow.split("  exact-operation:")[1];
    assert.match(operation, /dotnet-version: 10\.0\.109/);
    assert.doesNotMatch(operation, /global-json-file:/);
    assert.match(
        workflow.split("  exact-operation:")[0],
        /global-json-file: global.json/,
    );
});

test("bridge failure schema rejects every private nested metadata escape", () => {
    const base = validOriginalReport();
    base.pack = {
        ok: false,
        failure: {
            stage: "library-pack",
            id: "tool_failure",
            frames: [{ module: "projects", line: 59 }],
            tool: {
                command: "dotnet",
                status: 1,
                codes: ["CS2008"],
                projects: [],
                hints: [],
            },
        },
    } as any;
    validateOriginalReport(base, reportHead);
    for (const mutate of [
        (v: any) => {
            v.pack.failure.frames[0].module = "/private/secret";
        },
        (v: any) => {
            v.pack.failure.frames[0].line = 1000000;
        },
        (v: any) => {
            v.pack.failure.frames[0].path = "/secret";
        },
        (v: any) => {
            v.pack.failure.tool.command = "secret-command";
        },
        (v: any) => {
            v.pack.failure.tool.status = "secret";
        },
        (v: any) => {
            v.pack.failure.tool.codes = Array(9).fill("NU1301");
        },
        (v: any) => {
            v.pack.failure.tool.codes = ["NU1301-secret"];
        },
        (v: any) => {
            v.pack.failure.tool.hints = [{ toString: "secret" }];
        },
        (v: any) => {
            v.sdk.failure = {
                stage: "toolchain",
                id: "secret",
                frames: [],
                tool: null,
            };
        },
        (v: any) => {
            v.pack.failure.stack = "secret";
        },
        (v: any) => {
            v.pack.failure.tool.projects = ["private-secret-project"];
        },
        (v: any) => {
            v.pack.failure.tool.projects = Array(7).fill("Solus.Logging");
        },
        (v: any) => {
            v.zlib = "1-secret-with-unknown-shape";
        },
    ]) {
        const value = structuredClone(base);
        mutate(value);
        assert.throws(
            () => validateOriginalReport(value, reportHead),
            /original_pack_report_invalid/,
        );
    }
});
test("real bridge CLI and parent tool/report channel fail closed without exposing data", () => {
    const dir = mkdtempSync(join(tmpdir(), "solus-bridge-cli-"));
    try {
        mkdirSync(join(dir, "api-fetch"));
        mkdirSync(join(dir, "candidate"));
        const report = join(dir, "original-pack-report.json");
        const observations: any[] = [];
        assert.throws(
            () =>
                tool(
                    "original-pack",
                    "node",
                    [
                        join(sourceRoot, "scripts/logging/pack-exact-child.ts"),
                        sourceRoot,
                        join(dir, "candidate", "logging-packages"),
                        join(dir, "api-fetch", "input.nupkg"),
                        report,
                        reportHead,
                    ],
                    sourceRoot,
                    { PATH: process.env.PATH },
                    10000,
                    undefined,
                    (r) => observations.push(r),
                ),
            /tool-failed-original-pack/,
        );
        const parsed = readOriginalReport(report, reportHead);
        assert.equal(parsed.sourceVerified, false);
        assert.equal(parsed.pack.failure.id, "original_source_mismatch");
        assert.equal(observations.length, 1);
        assert.equal(observations[0].exitCode, 1);
        assert.ok(!JSON.stringify([parsed, observations]).includes(dir));
        assert.equal(
            existsSync(join(dir, "candidate", "logging-packages")),
            false,
        );
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});

const apiSecret = "SYNTHETIC_API_TOKEN_BODY_SECRET";
const sourceRepo = { id: 1406091678, full_name: "SolusQuest/solus-agent" };
function resourceFixture(p: any) {
    const index = lock.manifest.packages.findIndex((v: any) => v.id === p.id);
    return {
        package: {
            id: index === 0 ? 16460732 : 90000 + index,
            name: p.id,
            package_type: "nuget",
            visibility: "private",
            repository: sourceRepo,
        },
        version: {
            id: index === 0 ? 1481941083 : 80000 + index,
            name: p.version,
        },
    };
}
function exactResourceFetch(
    change: (
        url: string,
        fallback: () => Response,
    ) => Response | Promise<Response> = (_u, f) => f(),
) {
    const calls: string[] = [];
    const fetcher = async (input: any, options: any) => {
        const url = String(input);
        calls.push(url);
        assert.equal(options.method, "GET");
        assert.equal(options.redirect, "error");
        assert.equal(options.headers.Authorization, `Bearer ${apiSecret}`);
        assert.ok(
            url.startsWith(
                "https://api.github.com/orgs/SolusQuest/packages/nuget/",
            ),
        );
        const tail = url.split("/packages/nuget/")[1];
        const id = decodeURIComponent(tail.split("/")[0]);
        const p = lock.manifest.packages.find((v: any) => v.id === id);
        assert.ok(p);
        const fixture = resourceFixture(p);
        return change(
            url,
            () =>
                new Response(
                    JSON.stringify(
                        tail.includes("/versions?")
                            ? [fixture.version]
                            : tail.includes("/versions/")
                              ? fixture.version
                              : fixture.package,
                    ),
                ),
        );
    };
    return { calls, fetcher: fetcher as typeof fetch };
}
const creationContext = {
    mode: "publish-one",
    member: "Solus.Logging",
    operationHead: "a".repeat(40),
    operatorActorId: "16307884",
    workflowRef:
        "SolusQuest/solus-agent/.github/workflows/logging-release.yml@refs/heads/codex/l1-123-logging-publication",
};
function admissionReceipt(member = "Solus.Logging") {
    return {
        kind: "operator-confirmed-new-package",
        operationHead: creationContext.operationHead,
        source: lock.manifest.source.commit,
        candidate: lock.manifest.candidateSha256,
        member,
        version: lock.manifest.version,
        privateFeed: lock.manifest.agentApi.registry,
        beforeWorkflowRunNumber: 5,
        expectedWorkflowRunNumber: 6,
        exclusiveWindow: true,
        operatorAbsence: {
            actorId: 16307884,
            profile: "configured-operator-package-read",
            status: 200,
            complete: true,
            matches: 0,
            snapshotSha256: "b".repeat(64),
        },
    };
}
test("exact existing resource GETs admit original identities even when organization inventory is unavailable", async () => {
    for (const p of lock.manifest.packages) {
        const t = exactResourceFetch();
        const v = await readNugetIdentity(p, apiSecret, t.fetcher);
        assert.deepEqual(v, {
            id: p.id,
            version: p.version,
            packageId: resourceFixture(p).package.id,
            versionId: resourceFixture(p).version.id,
        });
        assert.equal(t.calls.length, 3);
        assert.ok(t.calls.every((u) => !u.includes("/packages?")));
    }
});
test("missing, forbidden, malformed, conflicting or unknown existing metadata NEVER returns absence or triggers verification", async () => {
    const p = lock.manifest.packages[0];
    for (const status of [403, 404, 500]) {
        let verified = 0;
        const t = exactResourceFetch(() => new Response(apiSecret, { status }));
        await assert.rejects(
            requireOriginalNugetResources(
                [p],
                apiSecret,
                async () => {
                    verified++;
                },
                t.fetcher,
            ),
            (e) =>
                String(e).includes("nuget-resource-unavailable") &&
                !String(e).includes(apiSecret),
        );
        assert.equal(verified, 0);
        assert.equal(t.calls.length, 1);
    }
    for (const mutate of [
        (v: any) => {
            v.visibility = "public";
        },
        (v: any) => {
            v.id = 3;
        },
        (v: any) => {
            v.package_type = "container";
        },
        (v: any) => {
            v.name = "other";
        },
        (v: any) => {
            v.repository = { id: 1, full_name: sourceRepo.full_name };
        },
        (v: any) => {
            v.repository = { ...sourceRepo, full_name: "wrong/repo" };
        },
    ]) {
        const t = exactResourceFetch((u, f) => {
            if (u.includes("/versions")) return f();
            const x = structuredClone(resourceFixture(p).package);
            mutate(x);
            return new Response(JSON.stringify(x));
        });
        await assert.rejects(
            readNugetIdentity(p, apiSecret, t.fetcher),
            /nuget-resource-identity/,
        );
    }
    for (const response of [
        () => new Response(apiSecret),
        () => new Response("x".repeat(1024 * 1024 + 1)),
        () => {
            throw Error(apiSecret);
        },
    ]) {
        const t = exactResourceFetch(() => response());
        await assert.rejects(
            readNugetIdentity(p, apiSecret, t.fetcher),
            (e) => !String(e).includes(apiSecret),
        );
    }
});
test("exact version inventory must be complete, unique and agree with fixed known Contracts ID and direct version", async () => {
    const p = lock.manifest.packages[0],
        f = resourceFixture(p);
    for (const values of [
        [],
        [f.version, f.version],
        [{ ...f.version, id: 2 }],
        { body: apiSecret },
    ]) {
        const t = exactResourceFetch((u, next) =>
            u.includes("/versions?")
                ? new Response(JSON.stringify(values))
                : next(),
        );
        await assert.rejects(readNugetIdentity(p, apiSecret, t.fetcher));
    }
    const full = Array.from({ length: 100 }, () => ({ id: 1, name: "other" }));
    const limited = exactResourceFetch((u, next) =>
        u.includes("/versions?") ? new Response(JSON.stringify(full)) : next(),
    );
    await assert.rejects(
        readNugetIdentity(p, apiSecret, limited.fetcher),
        /nuget-resource-pagination/,
    );
    assert.equal(limited.calls.length, 11);
    const changed = exactResourceFetch((u, next) =>
        u.includes("/versions/")
            ? new Response(
                  JSON.stringify({ ...f.version, name: "0.1.0-internal.2" }),
              )
            : next(),
    );
    await assert.rejects(
        readNugetIdentity(p, apiSecret, changed.fetcher),
        /nuget-resource-identity/,
    );
});
test("unavailable nested version views remain fatal and cannot be promoted to new creation", async () => {
    const p = lock.manifest.packages[0];
    for (const route of ["/versions?", "/versions/"])
        for (const status of [403, 404, 500]) {
            const t = exactResourceFetch((u, next) =>
                u.includes(route)
                    ? new Response(apiSecret, { status })
                    : next(),
            );
            await assert.rejects(
                readNugetIdentity(p, apiSecret, t.fetcher),
                (e) =>
                    String(e).includes("nuget-resource-unavailable") &&
                    !String(e).includes(apiSecret),
            );
        }
});
test("mapper, Logging, Supabase and CLI prerequisites share strict exact metadata plus original whole bytes before admission", async () => {
    const roles = [
        [lock.manifest.packages[0]],
        [lock.manifest.packages[0], lock.manifest.packages[2]],
        lock.manifest.packages,
    ];
    for (const packages of roles) {
        const t = exactResourceFetch();
        let verified = 0;
        const admitted = await requireOriginalNugetResources(
            packages,
            apiSecret,
            async (p) => {
                assert.deepEqual(
                    packages.find((v: any) => v.id === p.id),
                    p,
                );
                verified++;
            },
            t.fetcher,
        );
        assert.equal(verified, packages.length);
        assert.equal(admitted.length, packages.length);
    }
    let downstream = 0;
    const unavailable = exactResourceFetch((u, next) =>
        u.includes("SolusAgent.Telemetry")
            ? new Response("missing", { status: 404 })
            : next(),
    );
    await assert.rejects(
        (async () => {
            await requireOriginalNugetResources(
                lock.manifest.packages,
                apiSecret,
                async () => {},
                unavailable.fetcher,
            );
            downstream++;
        })(),
    );
    assert.equal(downstream, 0);
    const t = exactResourceFetch();
    await assert.rejects(
        requireOriginalNugetResources(
            [lock.manifest.packages[0]],
            apiSecret,
            async () => {
                throw Error("whole original archive mismatch");
            },
            t.fetcher,
        ),
    );
});
test("new creation admission is optional and strict, independently attested, one-member/head/candidate and next actual run-number bound", () => {
    const raw = JSON.stringify(admissionReceipt());
    const a = creationAdmission(raw, creationContext, "6");
    assert.ok(a);
    assert.match(a.sha256, /^[a-f0-9]{64}$/);
    assert.equal(creationAdmission(undefined, creationContext, "6"), undefined);
    for (const change of [
        (v: any) => {
            v.kind = "automatic";
        },
        (v: any) => {
            v.member = "Solus.Telemetry.Contracts";
        },
        (v: any) => {
            v.member = "CLI";
        },
        (v: any) => {
            v.operationHead = "c".repeat(40);
        },
        (v: any) => {
            v.source = "c".repeat(40);
        },
        (v: any) => {
            v.candidate = "c".repeat(64);
        },
        (v: any) => {
            v.version = "0.1.0-internal.2";
        },
        (v: any) => {
            v.privateFeed = "https://evil.invalid";
        },
        (v: any) => {
            v.exclusiveWindow = false;
        },
        (v: any) => {
            v.beforeWorkflowRunNumber = 3;
        },
        (v: any) => {
            v.expectedWorkflowRunNumber = 7;
        },
        (v: any) => {
            v.operatorAbsence.profile = "source-list";
        },
        (v: any) => {
            v.operatorAbsence.actorId = 1;
        },
        (v: any) => {
            v.operatorAbsence.status = 404;
        },
        (v: any) => {
            v.operatorAbsence.complete = false;
        },
        (v: any) => {
            v.operatorAbsence.matches = 1;
        },
        (v: any) => {
            v.operatorAbsence.snapshotSha256 = "invalid";
        },
        (v: any) => {
            v.secret = apiSecret;
        },
    ]) {
        const v = admissionReceipt();
        change(v);
        assert.throws(
            () => creationAdmission(JSON.stringify(v), creationContext, "6"),
            /nuget-creation-admission/,
        );
    }
    assert.throws(
        () => creationAdmission(raw, creationContext, "7"),
        /nuget-creation-admission/,
    );
    assert.throws(
        () =>
            creationAdmission(
                raw,
                { ...creationContext, operatorActorId: "1" },
                "6",
            ),
        /nuget-creation-admission/,
    );
    assert.throws(
        () =>
            creationAdmission(
                raw,
                { ...creationContext, workflowRef: "wrong/workflow" },
                "6",
            ),
        /nuget-creation-admission/,
    );
    assert.throws(() =>
        creationAdmission(
            raw,
            { ...creationContext, mode: "validate-exact" },
            "6",
        ),
    );
    assert.throws(
        () => creationAdmission(apiSecret, creationContext, "6"),
        (e) => !String(e).includes(apiSecret),
    );
});
test("known Contracts or missing/invalid admission cannot reach any creation read or push", async () => {
    let push = 0,
        read = 0;
    const fetcher = (async () => {
        read++;
        throw Error("unexpected");
    }) as typeof fetch;
    for (const p of [lock.manifest.packages[0], lock.manifest.packages[2]]) {
        const ctx = { ...creationContext, member: p.id };
        const receipt =
            p.id === "Solus.Telemetry.Contracts"
                ? JSON.stringify(admissionReceipt(p.id))
                : "";
        await assert.rejects(
            createNugetResource(
                p,
                receipt,
                ctx,
                "6",
                apiSecret,
                async () => {
                    push++;
                },
                async () => {},
                fetcher,
            ),
        );
    }
    assert.equal(push, 0);
    assert.equal(read, 0);
});
test("only explicit independently admitted new identity can push once; exact post metadata and complete original bytes are mandatory", async () => {
    const p = lock.manifest.packages[2];
    let pushed = 0,
        verified = 0;
    const t = exactResourceFetch((_u, next) =>
        pushed
            ? next()
            : new Response("unavailable source view", { status: 404 }),
    );
    const v = await createNugetResource(
        p,
        JSON.stringify(admissionReceipt()),
        creationContext,
        "6",
        apiSecret,
        async () => {
            pushed++;
        },
        async () => {
            verified++;
        },
        t.fetcher,
    );
    assert.equal(pushed, 1);
    assert.equal(verified, 1);
    assert.equal(v.identity.id, p.id);
    assert.equal(t.calls.length, 5);
    // Same receipt cannot be replayed in a distinct later workflow run.
    await assert.rejects(
        createNugetResource(
            p,
            JSON.stringify(admissionReceipt()),
            creationContext,
            "7",
            apiSecret,
            async () => {
                pushed++;
            },
            async () => {},
            t.fetcher,
        ),
    );
    assert.equal(pushed, 1);
});
test("creation never treats forbidden, unknown or unexpectedly positive resources as an absent slot", async () => {
    const p = lock.manifest.packages[2];
    for (const status of [200, 403, 500]) {
        let pushed = 0;
        const t = exactResourceFetch((_u, next) =>
            status === 200 ? next() : new Response(apiSecret, { status }),
        );
        await assert.rejects(
            createNugetResource(
                p,
                JSON.stringify(admissionReceipt()),
                creationContext,
                "6",
                apiSecret,
                async () => {
                    pushed++;
                },
                async () => {},
                t.fetcher,
            ),
        );
        assert.equal(pushed, 0);
    }
    let pushed = 0;
    const t = exactResourceFetch(() => {
        throw Error(apiSecret);
    });
    await assert.rejects(
        createNugetResource(
            p,
            JSON.stringify(admissionReceipt()),
            creationContext,
            "6",
            apiSecret,
            async () => {
                pushed++;
            },
            async () => {},
            t.fetcher,
        ),
        (e) => !String(e).includes(apiSecret),
    );
    assert.equal(pushed, 0);
    for (const status of [200, 403, 500]) {
        let pushes = 0;
        const mixed = exactResourceFetch((u, next) =>
            u.includes("/versions?")
                ? status === 200
                    ? next()
                    : new Response(apiSecret, { status })
                : new Response("missing", { status: 404 }),
        );
        await assert.rejects(
            createNugetResource(
                p,
                JSON.stringify(admissionReceipt()),
                creationContext,
                "6",
                apiSecret,
                async () => {
                    pushes++;
                },
                async () => {},
                mixed.fetcher,
            ),
        );
        assert.equal(pushes, 0);
        assert.equal(mixed.calls.length, 2);
    }
});
test("unknown push outcome or post-read/whole-byte failure stops with no automatic retry or rollback", async () => {
    const p = lock.manifest.packages[2];
    for (const failure of ["push", "post", "bytes"]) {
        let pushes = 0,
            verify = 0;
        const t = exactResourceFetch((_u, next) =>
            pushes === 0 || failure === "post"
                ? new Response("missing", { status: 404 })
                : next(),
        );
        await assert.rejects(
            createNugetResource(
                p,
                JSON.stringify(admissionReceipt()),
                creationContext,
                "6",
                apiSecret,
                async () => {
                    pushes++;
                    if (failure === "push") throw Error("unknown outcome");
                },
                async () => {
                    verify++;
                    throw Error("wrong original bytes");
                },
                t.fetcher,
            ),
        );
        assert.equal(pushes, 1);
        assert.equal(verify, failure === "bytes" ? 1 : 0);
    }
});
