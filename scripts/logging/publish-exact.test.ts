import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
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
    publishNugetMember,
    tool,
} from "./publish-exact.ts";
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
        .split("  exact-operation:")[0];
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

test("actual NuGet single-member flow retains equal versions and never retries unknown writes or unreadable/conflicting state", async () => {
    for (const initial of [true, false]) {
        let reads = 0,
            pushes = 0,
            verifies = 0;
        const result = await publishNugetMember({
            exists: async () => {
                reads++;
                return initial || pushes === 1;
            },
            push: async () => {
                pushes++;
            },
            verify: async () => {
                verifies++;
            },
        });
        assert.equal(
            result,
            initial ? "retained-identical" : "published-readback",
        );
        assert.equal(pushes, initial ? 0 : 1);
        assert.equal(verifies, 1);
        assert.equal(reads, initial ? 1 : 3);
    }
    for (const phase of ["exists", "verify", "push"]) {
        let pushes = 0;
        await assert.rejects(
            publishNugetMember({
                exists: async () => {
                    if (phase === "exists") throw new Error("unreadable");
                    return phase === "verify";
                },
                verify: async () => {
                    throw new Error("whole-byte conflict");
                },
                push: async () => {
                    pushes++;
                    throw new Error("outcome unknown");
                },
            }),
        );
        assert.equal(pushes, phase === "push" ? 1 : 0);
    }
    await assert.rejects(
        publishNugetMember({
            exists: async () => false,
            push: async () => {},
            verify: async () => {},
        }),
        /nuget-readback-missing/,
    );
});
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
