import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { createHash } from "node:crypto";
import { spawnSync } from "node:child_process";
import {
    reconcileContractViews,
    reconciliationContext,
    validateContractMetadata,
    verifyContractsArchive,
    contracts,
} from "./reconcile-contracts.ts";

const source = "c96a1b5cf9746a7fa569a339c583e07e3e103ee6";
const head = "a".repeat(40);
const context = {
    GITHUB_ACTIONS: "true",
    GITHUB_EVENT_NAME: "workflow_dispatch",
    GITHUB_REPOSITORY: "SolusQuest/solus-agent",
    GITHUB_REPOSITORY_ID: "1406091678",
    GITHUB_REF: "refs/heads/codex/l1-123-logging-publication",
    GITHUB_RUN_ATTEMPT: "1",
    GITHUB_RUN_ID: "123",
    GITHUB_JOB: "reconcile-contracts",
    RUNNER_OS: "Linux",
    RUNNER_ARCH: "X64",
    EXPECTED_OPERATION_HEAD: head,
    GITHUB_SHA: head,
    WORKFLOW_SHA: head,
    EXACT_MODE: "reconcile-contracts",
    EXACT_MEMBER: "Solus.Telemetry.Contracts",
    SELECTED_SOURCE: source,
    SELECTED_VERSION: "0.1.0-internal.1",
};
const pkg = {
    id: 16460732,
    name: "Solus.Telemetry.Contracts",
    package_type: "nuget",
    visibility: "private",
    repository: { id: 1406091678, full_name: "SolusQuest/solus-agent" },
};
const version = { id: 1481941083, name: "0.1.0-internal.1" };
const feed = "https://nuget.pkg.github.com/SolusQuest/index.json";
const base = "https://nuget.pkg.github.com/SolusQuest/download";
const secret = "HOSTILE_TOKEN_header_body_query_path";
const response = (value: any, status = 200) =>
    new Response(JSON.stringify(value), { status });

function transport(
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
        assert.equal(
            options.redirect,
            url.endsWith(".nupkg") ? "manual" : "error",
        );
        assert.ok(options.signal instanceof AbortSignal);
        const api = url.startsWith("https://api.github.com/");
        if (
            url.startsWith("https://nugetregistryv2prod.blob.core.windows.net/")
        ) {
            assert.equal(options.method, "GET");
            assert.equal(options.redirect, "error");
            assert.deepEqual(options.headers, {
                Accept: "application/octet-stream",
            });
            assert.equal(options.credentials, "omit");
            assert.equal(options.referrerPolicy, "no-referrer");
            return change(
                url,
                () => new Response(Buffer.from("not original archive")),
            );
        }
        assert.equal(
            options.headers.Authorization,
            api
                ? `Bearer ${secret}`
                : `Basic ${Buffer.from(`reader:${secret}`).toString("base64")}`,
        );
        const fallback = () => {
            if (url.includes("/packages?")) return response([pkg]);
            if (url.includes("/versions?")) return response([version]);
            if (url.endsWith("/versions/1481941083")) return response(version);
            if (url.endsWith("/nuget/Solus.Telemetry.Contracts"))
                return response(pkg);
            if (url === feed)
                return response({
                    resources: [
                        { "@type": "PackageBaseAddress/3.0.0", "@id": base },
                    ],
                });
            assert.equal(
                url,
                `${base}/solus.telemetry.contracts/0.1.0-internal.1/solus.telemetry.contracts.0.1.0-internal.1.nupkg`,
            );
            return new Response(Buffer.from("not original archive"));
        };
        return change(url, fallback);
    };
    return { calls, fetcher: fetcher as typeof fetch };
}
async function probe(t = transport()) {
    let inspected = 0;
    const r = await reconcileContractViews(
        secret,
        "reader",
        () => {
            inspected++;
        },
        t.fetcher,
    );
    assert.equal(
        inspected,
        0,
        "noncanonical bytes must never reach archive inspection",
    );
    assert.equal(r.immutableWrites, false);
    assert.equal(r.originalFailedPublicationAccepted, false);
    assert.equal(r.accessSettingsVerified, false);
    assert.equal(r.consumerAccessVerified, false);
    assert.ok(!JSON.stringify(r).includes(secret));
    return r;
}

test("read-only context binds exact repo/job/ref/head/attempt/source/member before transport", () => {
    assert.deepEqual(reconciliationContext(context), {
        operationHead: head,
        runId: "123",
        attempt: 1,
    });
    for (const key of Object.keys(context))
        assert.throws(() =>
            reconciliationContext({ ...context, [key]: "wrong" }),
        );
    for (const mode of ["publish-one", "validate-exact", "prepare"])
        assert.throws(() =>
            reconciliationContext({ ...context, EXACT_MODE: mode }),
        );
});
test("production GET boundary collects four separate complete views and refuses nonoriginal archive", async () => {
    const t = transport();
    const r = await probe(t);
    assert.equal(t.calls.length, 6);
    assert.equal(r.metadataVerified, true);
    assert.equal(r.bytesVerified, false);
    assert.equal(r.archiveError, "original-byte-or-metadata");
    assert.equal(r.outcome, "missing-or-contradictory-readback");
    assert.equal(r.observations.inventory.matches, 1);
    assert.equal(r.observations.versions.matches, 1);
});
test("absent inventory and present direct/version views remain explicitly contradictory", async () => {
    const r = await probe(
        transport((url, f) =>
            url.includes("/packages?") ? response([]) : f(),
        ),
    );
    assert.equal(r.observations.inventory.complete, true);
    assert.equal(r.observations.inventory.matches, 0);
    assert.equal(r.observations.directPackage.identityVerified, true);
    assert.equal(r.observations.versions.identityVerified, true);
    assert.equal(r.metadataVerified, false);
});
test("inventory versus missing version is independently visible, not a collapsed exists boolean", async () => {
    const r = await probe(
        transport((u, f) => (u.includes("/versions?") ? response([]) : f())),
    );
    assert.equal(r.observations.inventory.identityVerified, true);
    assert.equal(r.observations.versions.matches, 0);
    assert.equal(r.observations.directVersion.identityVerified, true);
    assert.equal(r.metadataVerified, false);
});
test("package identity/privacy/source mismatches and duplicate known identities fail closed", async () => {
    for (const bad of [
        { ...pkg, id: 7 },
        { ...pkg, visibility: "public" },
        { ...pkg, package_type: "container" },
        { ...pkg, repository: { ...pkg.repository, id: 1 } },
        { ...pkg, repository: { ...pkg.repository, full_name: "other/repo" } },
    ]) {
        const r = await probe(
            transport((u, f) =>
                u.includes("/packages?") ? response([bad]) : f(),
            ),
        );
        assert.equal(r.observations.inventory.identityVerified, false);
        assert.equal(r.metadataVerified, false);
    }
    const r = await probe(
        transport((u, f) =>
            u.includes("/packages?") ? response([pkg, pkg]) : f(),
        ),
    );
    assert.equal(r.observations.inventory.matches, 2);
    assert.equal(r.metadataVerified, false);
});
test("version ID/name substitutions and duplicate entries cannot be accepted", async () => {
    for (const bad of [
        { ...version, id: 1 },
        { ...version, name: "0.1.0-internal.2" },
    ]) {
        const r = await probe(
            transport((u, f) =>
                u.includes("/versions?") ? response([bad]) : f(),
            ),
        );
        assert.equal(r.observations.versions.identityVerified, false);
        assert.equal(r.metadataVerified, false);
    }
    const r = await probe(
        transport((u, f) =>
            u.includes("/versions?") ? response([version, version]) : f(),
        ),
    );
    assert.equal(r.observations.versions.matches, 2);
});
test("403/404/500 and redirect statuses preserve bounded observations without response-body leakage or retries", async () => {
    for (const status of [302, 403, 404, 500]) {
        const t = transport((u, f) =>
            u.includes("/packages?") ? response({ secret }, status) : f(),
        );
        const r = await probe(t);
        assert.equal(r.observations.inventory.status, status);
        assert.equal(r.observations.inventory.complete, false);
        assert.equal(r.observations.inventory.error, "http");
        assert.equal(t.calls.filter((u) => u.includes("/packages?")).length, 1);
    }
});
test("network, malformed JSON, unexpected collection and bounded body failures never print hostile material", async () => {
    const variants = [
        () => {
            throw new Error(secret);
        },
        () => new Response(secret),
        () => response({ body: secret }),
        () => new Response("x".repeat(1024 * 1024 + 1)),
    ];
    for (const replacement of variants) {
        const r = await probe(
            transport((u, f) =>
                u.includes("/packages?") ? replacement() : f(),
            ),
        );
        assert.equal(r.observations.inventory.complete, false);
        assert.equal(r.metadataVerified, false);
    }
});
test("pagination completes exact full pages and stops at the fixed hard limit without retry/fallback", async () => {
    const unrelated = Array.from({ length: 100 }, (_, i) => ({
        name: `unknown-${i}-${secret}`,
    }));
    const t = transport((u, f) =>
        u.includes("/packages?")
            ? response(u.endsWith("page=1") ? unrelated : [pkg])
            : f(),
    );
    const r = await probe(t);
    assert.equal(r.observations.inventory.complete, true);
    assert.equal(r.observations.inventory.total, 101);
    assert.equal(r.metadataVerified, true);
    const limited = transport((u, f) =>
        u.includes("/packages?") ? response(unrelated) : f(),
    );
    const lr = await probe(limited);
    assert.equal(lr.observations.inventory.complete, false);
    assert.equal(lr.observations.inventory.error, "page-limit");
    assert.equal(lr.observations.inventory.total, 1000);
    assert.equal(
        limited.calls.filter((u) => u.includes("/packages?")).length,
        10,
    );
});
test("feed identity must be exact with no external credential forwarding or archive fallback", async () => {
    for (const resources of [
        [],
        [
            {
                "@type": "PackageBaseAddress/3.0.0",
                "@id": "https://evil.invalid/" + secret,
            },
        ],
        [{ "@type": "PackageBaseAddress/3.0.0", "@id": base + "?" + secret }],
        [
            { "@type": "PackageBaseAddress/3.0.0", "@id": base },
            { "@type": "PackageBaseAddress/3.0.0", "@id": base },
        ],
    ]) {
        const t = transport((u, f) =>
            u === feed ? response({ resources }) : f(),
        );
        const r = await probe(t);
        assert.equal(r.feedVerified, false);
        assert.equal(t.calls.length, 5);
        assert.equal(r.archiveStatus, null);
    }
});
test("only one named registry backing redirect is admitted, without source token/cookie/referrer or leaked signed URL", async () => {
    const location =
        "https://nugetregistryv2prod.blob.core.windows.net/fixed-object?sig=SIGNED_LOCATION_SECRET";
    const t = transport((u, f) =>
        u.endsWith(".nupkg")
            ? new Response(null, { status: 302, headers: { location } })
            : f(),
    );
    const r = await probe(t);
    assert.equal(t.calls.length, 7);
    assert.equal(r.archiveRedirectStatus, 302);
    assert.equal(r.archiveStatus, 200);
    assert.equal(r.bytesVerified, false);
    assert.equal(JSON.stringify(r).includes("SIGNED_LOCATION_SECRET"), false);
    for (const bad of [
        "https://evil.invalid/object?sig=" + secret,
        "http://nugetregistryv2prod.blob.core.windows.net/object?sig=x",
        "https://user:pass@nugetregistryv2prod.blob.core.windows.net/object?sig=x",
        "https://nugetregistryv2prod.blob.core.windows.net:444/object?sig=x",
        "https://nugetregistryv2prod.blob.core.windows.net/object?sig=x#fragment",
        "https://nugetregistryv2prod.blob.core.windows.net/object",
        "//nugetregistryv2prod.blob.core.windows.net/object?sig=x",
    ]) {
        const denied = transport((u, f) =>
            u.endsWith(".nupkg")
                ? new Response(null, {
                      status: 302,
                      headers: { location: bad },
                  })
                : f(),
        );
        const dr = await probe(denied);
        assert.equal(dr.archiveError, "archive-redirect");
        assert.equal(denied.calls.length, 6);
    }
    const second = transport((u, f) =>
        u.endsWith(".nupkg")
            ? new Response(null, { status: 302, headers: { location } })
            : u.startsWith("https://nugetregistryv2prod.blob.core.windows.net/")
              ? new Response(null, {
                    status: 302,
                    headers: { location: "https://evil.invalid/" + secret },
                })
              : f(),
    );
    const sr = await probe(second);
    assert.equal(sr.archiveError, "http");
    assert.equal(second.calls.length, 7);
});
test("hostile API properties and raw errors are projected away from the report", async () => {
    const t = transport((u, f) =>
        u.endsWith("/nuget/Solus.Telemetry.Contracts")
            ? response({
                  ...pkg,
                  body: secret,
                  token: secret,
                  url: "https://invalid/" + secret,
              })
            : f(),
    );
    const r = await probe(t);
    assert.equal(r.observations.directPackage.identityVerified, true);
    assert.equal(JSON.stringify(r).includes(secret), false);
});
test("metadata verifier enforces canonical DLL, empty dependencies and exact c96 repository", () => {
    const good = {
        dllSha256: contracts.dllSha256,
        dependencies: {},
        nuspec: `<repository type="git" url="https://github.com/SolusQuest/solus-agent" commit="${source}" />`,
    };
    assert.equal(validateContractMetadata(good).verified, true);
    for (const bad of [
        { ...good, dllSha256: "0".repeat(64) },
        { ...good, dependencies: { Other: "[1.0.0]" } },
        { ...good, nuspec: good.nuspec.replace(source, "b".repeat(40)) },
        {
            ...good,
            nuspec: good.nuspec.replace("SolusQuest/solus-agent", "other/repo"),
        },
        { ...good, nuspec: good.nuspec.replace(" url=", " evilurl=") },
        { ...good, nuspec: good.nuspec + good.nuspec },
    ])
        assert.throws(() => validateContractMetadata(bad));
    assert.throws(() =>
        verifyContractsArchive(Buffer.from("wrong archive"), resolve(".")),
    );
});
test("entry refuses hostile contexts/inspection credential env and does not disclose any supplied secret", () => {
    for (const args of [
        [],
        ["--inspect", resolve("."), "not-original.nupkg"],
        ["publish-one"],
    ]) {
        const r = spawnSync(
            process.execPath,
            ["scripts/logging/reconcile-contracts.ts", ...args],
            {
                encoding: "utf8",
                env: { PATH: process.env.PATH, GITHUB_TOKEN: secret },
            },
        );
        assert.equal(r.status, 1);
        assert.equal(r.stdout, "");
        assert.equal(r.stderr.includes(secret), false);
        assert.match(r.stderr, /contracts-reconciliation-refused/);
    }
});
test("actual CLI source/context refusal precedes every credential-bearing request", () => {
    const dir = mkdtempSync(join(tmpdir(), "contracts-no-network-"));
    try {
        const preload = join(dir, "no-network.mjs");
        writeFileSync(
            preload,
            'globalThis.fetch = () => { console.error("FORBIDDEN_TRANSPORT_ATTEMPT"); throw new Error("blocked"); };\n',
        );
        for (const env of [
            { PATH: process.env.PATH, GITHUB_TOKEN: secret },
            {
                ...context,
                PATH: process.env.PATH,
                HOME: process.env.HOME,
                GITHUB_TOKEN: secret,
                EXACT_SOURCE_ROOT: resolve("wrong-source"),
            },
        ]) {
            const r = spawnSync(
                process.execPath,
                ["--import", preload, "scripts/logging/reconcile-contracts.ts"],
                { encoding: "utf8", env },
            );
            assert.equal(r.status, 1);
            assert.equal(r.stdout, "");
            assert.doesNotMatch(r.stderr, /FORBIDDEN_TRANSPORT_ATTEMPT/);
            assert.equal(r.stderr.includes(secret), false);
        }
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
});
test("read-only job is isolated from writer/packer and exact writer code/job remain unchanged", () => {
    const text = readFileSync("scripts/logging/reconcile-contracts.ts", "utf8");
    assert.doesNotMatch(
        text,
        /from\s+["'][^"']*(?:publish|pack-exact|pack\.ts)/,
    );
    assert.doesNotMatch(text, /method:\s*["'](?:POST|PUT|PATCH|DELETE)["']/);
    const workflow = readFileSync(
        ".github/workflows/logging-release.yml",
        "utf8",
    );
    const readJob = workflow
        .split("\n  reconcile-contracts:\n")[1]
        .split("\n  exact-operation:\n")[0];
    assert.match(readJob, /contents: read\n      packages: read/);
    assert.doesNotMatch(
        readJob,
        /packages: write|publish-exact|pack\.ts|dotnet/,
    );
    assert.match(readJob, /EXACT_MEMBER: \$\{\{ inputs.member \}\}/);
    assert.match(readJob, /persist-credentials: false/);
    assert.match(readJob, /path: \.tmp\/reconcile-c96/);
    assert.match(readJob, /node scripts\/logging\/reconcile-contracts\.ts/);
    const hash = (s: string | Buffer) =>
        createHash("sha256").update(s).digest("hex");
    // Frozen accepted-ab42 identities; do not depend on an old PR commit surviving a squash.
    assert.equal(
        hash(workflow.split("\n  exact-operation:\n")[1]),
        "39277e6b11b3e3546759f314034247d62e1c6c809bc0ef18198b677b6a6fca07",
    );
    assert.equal(
        hash(readFileSync("scripts/logging/publish-exact.ts")),
        "cf03051a838adeb4e61566769bb5c66bbee6879c11049cb21645aca1528fb452",
    );
});
