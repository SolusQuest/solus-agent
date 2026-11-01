// Incident-only GET/read path. Does not import any packer or publisher.
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from "node:fs";
import { resolve, join } from "node:path";
import { tmpdir } from "node:os";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { pathToFileURL, fileURLToPath } from "node:url";
import { isDeepStrictEqual } from "node:util";

const lock = JSON.parse(
    readFileSync(new URL("./accepted-candidate.json", import.meta.url), "utf8"),
);
const original = lock.manifest;
export const contracts = original.packages[0];
const packageId = 16460732;
const versionId = 1481941083;
const repoId = 1406091678;
const lockBlob = "d19a8f3153f540664c21296a96d77bf3221acc25";
const api = "https://api.github.com";
const packagePath = "/orgs/SolusQuest/packages/nuget/Solus.Telemetry.Contracts";
const feed = "https://nuget.pkg.github.com/SolusQuest/index.json";
const download = "https://nuget.pkg.github.com/SolusQuest/download";
const archiveUrl = `${download}/solus.telemetry.contracts/0.1.0-internal.1/solus.telemetry.contracts.0.1.0-internal.1.nupkg`;
const sha = (bytes: Uint8Array) =>
    createHash("sha256").update(bytes).digest("hex");
const refuse = (): never => {
    throw new Error("contracts-reconciliation-refused");
};
const cleanEnv = () =>
    Object.fromEntries(
        ["PATH", "HOME", "TMPDIR", "DOTNET_ROOT"]
            .filter((k) => process.env[k])
            .map((k) => [k, process.env[k]!]),
    );

export function reconciliationContext(env = process.env) {
    if (
        env.GITHUB_ACTIONS !== "true" ||
        env.GITHUB_EVENT_NAME !== "workflow_dispatch" ||
        env.GITHUB_REPOSITORY !== "SolusQuest/solus-agent" ||
        env.GITHUB_REPOSITORY_ID !== String(repoId) ||
        env.GITHUB_REF !== "refs/heads/codex/l1-123-logging-publication" ||
        env.GITHUB_RUN_ATTEMPT !== "1" ||
        !/^\d+$/.test(env.GITHUB_RUN_ID ?? "") ||
        env.GITHUB_JOB !== "reconcile-contracts" ||
        env.RUNNER_OS !== "Linux" ||
        env.RUNNER_ARCH !== "X64" ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_OPERATION_HEAD ?? "") ||
        env.EXPECTED_OPERATION_HEAD !== env.GITHUB_SHA ||
        env.WORKFLOW_SHA !== env.GITHUB_SHA ||
        env.EXACT_MODE !== "reconcile-contracts" ||
        env.EXACT_MEMBER !== "Solus.Telemetry.Contracts" ||
        env.SELECTED_SOURCE !== original.source.commit ||
        env.SELECTED_VERSION !== contracts.version ||
        contracts.id !== "Solus.Telemetry.Contracts" ||
        contracts.version !== "0.1.0-internal.1" ||
        original.source.commit !== "c96a1b5cf9746a7fa569a339c583e07e3e103ee6"
    )
        refuse();
    return {
        operationHead: env.GITHUB_SHA!,
        runId: env.GITHUB_RUN_ID!,
        attempt: 1,
    };
}
function git(directory: string, args: string[]) {
    const r = spawnSync("git", args, {
        cwd: directory,
        env: cleanEnv(),
        encoding: "utf8",
        timeout: 10000,
        maxBuffer: 65536,
    });
    if (r.error || r.signal || r.status !== 0) refuse();
    return r.stdout.trim();
}
function sourceGuard(source: string) {
    if (
        git(source, ["rev-parse", "HEAD"]) !== original.source.commit ||
        git(source, ["rev-parse", "HEAD^{tree}"]) !== original.source.tree ||
        git(source, ["status", "--porcelain"]) !== ""
    )
        refuse();
}
export function validateContractMetadata(value: any) {
    if (
        value.dllSha256 !== contracts.dllSha256 ||
        !isDeepStrictEqual(value.dependencies, {}) ||
        typeof value.nuspec !== "string" ||
        value.nuspec.length > 16384
    )
        refuse();
    const repositories = [
        ...value.nuspec.matchAll(/<repository\s+([^>]+)\/?\s*>/g),
    ];
    if (repositories.length !== 1) refuse();
    const attrs = [
        ...repositories[0][1].matchAll(/(?:^|\s)([A-Za-z]+)="([^"]*)"/g),
    ];
    const urls = attrs.filter((a) => a[1] === "url"),
        commits = attrs.filter((a) => a[1] === "commit");
    if (
        urls.length !== 1 ||
        commits.length !== 1 ||
        urls[0][2] !== "https://github.com/SolusQuest/solus-agent" ||
        commits[0][2] !== original.source.commit
    )
        refuse();
    return metadataProof();
}
function metadataProof() {
    return {
        verified: true,
        sha256: contracts.sha256,
        size: lock.sizes[contracts.file],
        dllSha256: contracts.dllSha256,
        dependencies: {},
        repository: "SolusQuest/solus-agent",
        source: original.source.commit,
    };
}
function archiveGuard(bytes: Uint8Array) {
    if (
        bytes.length !== lock.sizes[contracts.file] ||
        sha(bytes) !== contracts.sha256
    )
        refuse();
}
// Credential-free child calls only the unchanged original archive inspector, never pack().
async function inspectChild(source: string, path: string) {
    sourceGuard(source);
    archiveGuard(readFileSync(path));
    const { readPackage, checkDependencies } = await import(
        pathToFileURL(join(source, "scripts/logging/package-archive.ts")).href
    );
    const p = readPackage(path, contracts.id, contracts.version);
    checkDependencies(p.dependencies, contracts.dependencies);
    return validateContractMetadata(p);
}
export function verifyContractsArchive(bytes: Uint8Array, source: string) {
    archiveGuard(bytes);
    const dir = mkdtempSync(join(tmpdir(), "contracts-readonly-"));
    try {
        const path = join(dir, contracts.file);
        writeFileSync(path, bytes, { flag: "wx", mode: 0o600 });
        const r = spawnSync(
            process.execPath,
            [fileURLToPath(import.meta.url), "--inspect", source, path],
            {
                env: cleanEnv(),
                encoding: "utf8",
                timeout: 30000,
                maxBuffer: 8192,
            },
        );
        if (r.error || r.signal || r.status !== 0) refuse();
        const actual = JSON.parse(r.stdout);
        const expected = metadataProof();
        if (!isDeepStrictEqual(actual, expected)) refuse();
        return expected;
    } finally {
        rmSync(dir, { recursive: true, force: true });
    }
}

type ReadResult = {
    status: number | null;
    error: string | null;
    value: any;
    redirectStatus?: number;
};
// All URLs are internally selected constants. No caller-supplied URL, method or request options.
async function get(
    url: string,
    token: string,
    actor: string,
    json: boolean,
    fetcher: typeof fetch,
): Promise<ReadResult> {
    let redirectStatus: number | undefined;
    let observedStatus: number | null = null;
    const result = (
        status: number | null,
        error: string | null,
        value: any,
    ): ReadResult => ({
        status,
        error,
        value,
        ...(redirectStatus ? { redirectStatus } : {}),
    });
    try {
        const isApi = url.startsWith(api + "/");
        const allowed = isApi
            ? [
                  `${api}/orgs/SolusQuest/packages?package_type=nuget&per_page=100&page=`,
                  `${api}${packagePath}/versions?per_page=100&page=`,
              ].some(
                  (prefix) =>
                      url.startsWith(prefix) &&
                      /^[1-9][0-9]?$/.test(url.slice(prefix.length)),
              ) ||
              url === `${api}${packagePath}` ||
              url === `${api}${packagePath}/versions/${versionId}`
            : url === feed || url === archiveUrl;
        if (!allowed) refuse();
        let response = await fetcher(url, {
            method: "GET",
            redirect: url === archiveUrl ? "manual" : "error",
            signal: AbortSignal.timeout(30000),
            headers: {
                Authorization: isApi
                    ? `Bearer ${token}`
                    : `Basic ${Buffer.from(`${actor}:${token}`).toString("base64")}`,
                Accept: isApi
                    ? "application/vnd.github+json"
                    : "application/octet-stream",
                ...(isApi ? { "X-GitHub-Api-Version": "2022-11-28" } : {}),
            },
        });
        observedStatus = response.status;
        // Existing NuGet download protocol: exactly one observed registry backing hop.
        // The signed Location stays in memory. Never forward the source token/cookies.
        if (url === archiveUrl && response.status === 302) {
            redirectStatus = 302;
            const location = response.headers.get("location");
            await response.body?.cancel();
            if (!location || location.length > 4096)
                return result(302, "archive-redirect", null);
            let target: URL;
            try {
                target = new URL(location);
            } catch {
                return result(302, "archive-redirect", null);
            }
            if (
                target.origin !==
                    "https://nugetregistryv2prod.blob.core.windows.net" ||
                target.username ||
                target.password ||
                target.hash ||
                !target.search ||
                target.pathname === "/"
            )
                return result(302, "archive-redirect", null);
            observedStatus = null;
            response = await fetcher(target.href, {
                method: "GET",
                redirect: "error",
                credentials: "omit",
                referrerPolicy: "no-referrer",
                signal: AbortSignal.timeout(30000),
                headers: { Accept: "application/octet-stream" },
            });
        }
        observedStatus = response.status;
        if (response.status !== 200) {
            await response.body?.cancel();
            return result(response.status, "http", null);
        }
        const reader = response.body?.getReader();
        if (!reader) return result(200, "body", null);
        const chunks: Uint8Array[] = [];
        let total = 0;
        while (true) {
            const part = await reader.read();
            if (part.done) break;
            total += part.value.length;
            if (total > (json ? 1024 * 1024 : 65898)) {
                await reader.cancel();
                return result(200, "body-limit", null);
            }
            chunks.push(part.value);
        }
        const bytes = Buffer.concat(chunks);
        if (json) {
            try {
                return result(200, null, JSON.parse(bytes.toString("utf8")));
            } catch {
                return result(200, "json", null);
            }
        }
        return result(200, null, bytes);
    } catch {
        return result(observedStatus, "read-or-json", null);
    }
}
async function pages(
    path: string,
    token: string,
    actor: string,
    fetcher: typeof fetch,
) {
    const values: any[] = [];
    let status: number | null = null;
    for (let page = 1; page <= 10; page++) {
        const r = await get(
            `${api}${path}${path.includes("?") ? "&" : "?"}per_page=100&page=${page}`,
            token,
            actor,
            true,
            fetcher,
        );
        status = r.status;
        if (r.error || !Array.isArray(r.value) || r.value.length > 100)
            return {
                status,
                complete: false,
                error: r.error ?? "shape",
                values,
            };
        values.push(...r.value);
        if (r.value.length < 100)
            return { status, complete: true, error: null, values };
    }
    return { status, complete: false, error: "page-limit", values };
}
const packageMatches = (v: any) =>
    v?.id === packageId &&
    v?.name === contracts.id &&
    v?.package_type === "nuget" &&
    v?.visibility === "private" &&
    v?.repository?.id === repoId &&
    v?.repository?.full_name === "SolusQuest/solus-agent";
const versionMatches = (v: any) =>
    v?.id === versionId && v?.name === contracts.version;

export async function reconcileContractViews(
    token: string,
    actor: string,
    inspect: (bytes: Uint8Array) => unknown,
    fetcher: typeof fetch = fetch,
) {
    if (!token || !/^[A-Za-z0-9][A-Za-z0-9-]{0,38}$/.test(actor)) refuse();
    const inventory = await pages(
        "/orgs/SolusQuest/packages?package_type=nuget",
        token,
        actor,
        fetcher,
    );
    const selected = inventory.values.filter(
        (v) =>
            typeof v?.name === "string" &&
            v.name.toLowerCase() === contracts.id.toLowerCase(),
    );
    const versions = await pages(
        `${packagePath}/versions`,
        token,
        actor,
        fetcher,
    );
    const selectedVersions = versions.values.filter(
        (v) => v?.name === contracts.version || v?.id === versionId,
    );
    const direct = await get(
        `${api}${packagePath}`,
        token,
        actor,
        true,
        fetcher,
    );
    const directVersion = await get(
        `${api}${packagePath}/versions/${versionId}`,
        token,
        actor,
        true,
        fetcher,
    );
    const observations = {
        inventory: {
            status: inventory.status,
            complete: inventory.complete,
            error: inventory.error,
            total: inventory.values.length,
            matches: selected.length,
            identityVerified:
                selected.length === 1 && packageMatches(selected[0]),
        },
        versions: {
            status: versions.status,
            complete: versions.complete,
            error: versions.error,
            total: versions.values.length,
            matches: selectedVersions.length,
            identityVerified:
                selectedVersions.length === 1 &&
                versionMatches(selectedVersions[0]),
        },
        directPackage: {
            status: direct.status,
            error: direct.error,
            identityVerified: !direct.error && packageMatches(direct.value),
        },
        directVersion: {
            status: directVersion.status,
            error: directVersion.error,
            identityVerified:
                !directVersion.error && versionMatches(directVersion.value),
        },
    };
    const index = await get(feed, token, actor, true, fetcher);
    const bases = Array.isArray(index.value?.resources)
        ? index.value.resources.filter(
              (v: any) => v?.["@type"] === "PackageBaseAddress/3.0.0",
          )
        : [];
    const feedVerified =
        !index.error && bases.length === 1 && bases[0]["@id"] === download;
    let archiveStatus: number | null = null;
    let archiveRedirectStatus: number | null = null;
    let bytesVerified = false;
    let archiveError: string | null = "feed";
    if (feedVerified) {
        const archive = await get(archiveUrl, token, actor, false, fetcher);
        archiveStatus = archive.status;
        archiveRedirectStatus = archive.redirectStatus ?? null;
        archiveError = archive.error;
        if (!archive.error) {
            try {
                archiveGuard(archive.value);
                if (!isDeepStrictEqual(inspect(archive.value), metadataProof()))
                    refuse();
                bytesVerified = true;
                archiveError = null;
            } catch {
                archiveError = "original-byte-or-metadata";
            }
        }
    }
    const metadataVerified =
        inventory.complete &&
        versions.complete &&
        Object.values(observations).every((v) => v.identityVerified);
    return {
        event: "contracts-readonly-reconciliation",
        packageId,
        versionId,
        id: contracts.id,
        version: contracts.version,
        source: original.source.commit,
        feed,
        observations,
        feedStatus: index.status,
        feedVerified,
        archiveStatus,
        archiveRedirectStatus,
        archiveError,
        bytesVerified,
        metadataVerified,
        originalArchiveSha256: bytesVerified ? contracts.sha256 : null,
        originalArchiveSize: bytesVerified ? lock.sizes[contracts.file] : null,
        archiveMetadata: bytesVerified ? metadataProof() : null,
        outcome:
            bytesVerified && metadataVerified
                ? "existing-original-readback-verified"
                : "missing-or-contradictory-readback",
        immutableWrites: false,
        originalFailedPublicationAccepted: false,
        accessSettingsVerified: false,
        consumerAccessVerified: false,
    };
}
async function execute() {
    const context = reconciliationContext();
    const here = resolve(dirnameOfSelf(), "../..");
    const source = resolve(process.env.EXACT_SOURCE_ROOT ?? "");
    if (
        source !== join(here, ".tmp", "reconcile-c96") ||
        git(here, ["rev-parse", "HEAD"]) !== context.operationHead ||
        git(here, ["status", "--porcelain"]) !== "" ||
        git(here, [
            "rev-parse",
            "HEAD:scripts/logging/accepted-candidate.json",
        ]) !== lockBlob
    )
        refuse();
    sourceGuard(source);
    const token = process.env.GITHUB_TOKEN ?? "";
    const actor = process.env.GITHUB_ACTOR ?? "";
    const report = await reconcileContractViews(token, actor, (bytes) =>
        verifyContractsArchive(bytes, source),
    );
    console.log(JSON.stringify({ ...context, ...report }));
    if (!report.bytesVerified || !report.metadataVerified) process.exitCode = 1;
}
const dirnameOfSelf = () => resolve(fileURLToPath(import.meta.url), "..");
if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
    try {
        if (process.argv.length === 5 && process.argv[2] === "--inspect") {
            if (
                Object.keys(process.env).some((k) =>
                    /TOKEN|PASSWORD|SECRET|CREDENTIAL|BEARER|API_KEY/i.test(k),
                )
            )
                refuse();
            console.log(
                JSON.stringify(
                    await inspectChild(
                        resolve(process.argv[3]),
                        resolve(process.argv[4]),
                    ),
                ),
            );
        } else {
            if (process.argv.length !== 2) refuse();
            await execute();
        }
    } catch {
        console.error(
            "contracts-reconciliation-refused; no writer fallback or automatic retry",
        );
        process.exitCode = 1;
    }
}
