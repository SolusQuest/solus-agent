// Fixed read-only run12 for actual failed CLI11. No publisher/packer imports or writes.
import https from "node:https";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const origin = "https://ghcr.io";
const namespace = "/v2/solusquest/solus-logging-cli";
const config =
    "ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356";
const manifest =
    "9d23869078138bd159ebfb3c3686da55215cf49a00b267352d11c371ce201913";
const source = "c96a1b5cf9746a7fa569a339c583e07e3e103ee6";
const version = "0.1.0-internal.1";
const refuse = (): never => {
    throw new Error("cli-readback-inspection-refused");
};
const hash = (v: Uint8Array) => createHash("sha256").update(v).digest("hex");

export function readbackContext(env = process.env) {
    const exact: Record<string, string> = {
        GITHUB_ACTIONS: "true",
        GITHUB_EVENT_NAME: "workflow_dispatch",
        GITHUB_REPOSITORY: "SolusQuest/solus-agent",
        GITHUB_REPOSITORY_ID: "1406091678",
        GITHUB_REF: "refs/heads/codex/l1-123-logging-publication",
        GITHUB_JOB: "inspect-cli-readback",
        GITHUB_RUN_ATTEMPT: "1",
        GITHUB_RUN_NUMBER: "12",
        GITHUB_ACTOR_ID: "16307884",
        RUNNER_OS: "Linux",
        RUNNER_ARCH: "X64",
        EXACT_MODE: "inspect-cli-readback",
        EXACT_MEMBER: "CLI",
        SELECTED_SOURCE: source,
        SELECTED_VERSION: version,
    };
    if (
        Object.entries(exact).some(([k, v]) => env[k] !== v) ||
        !/^[1-9]\d{0,19}$/.test(env.GITHUB_RUN_ID ?? "") ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_OPERATION_HEAD ?? "") ||
        env.EXPECTED_OPERATION_HEAD !== env.GITHUB_SHA ||
        env.WORKFLOW_SHA !== env.GITHUB_SHA
    )
        refuse();
    return {
        operationHead: env.GITHUB_SHA!,
        runId: env.GITHUB_RUN_ID!,
        runNumber: 12,
        attempt: 1,
    };
}

// Raw Location is never returned, stored, logged or followed, including on parse failure.
export function projectReadbackResponse(
    status: number,
    get: (name: string) => string | null,
) {
    const raw = get("location");
    let redirect: any = { status: "missing" };
    if (raw) {
        if (raw.length > 4096) redirect = { status: "over-limit" };
        else
            try {
                const u = new URL(raw, origin);
                redirect = {
                    status: "parsed",
                    originClass:
                        u.origin === origin
                            ? "same-ghcr"
                            : u.protocol === "https:"
                              ? "other-https"
                              : "other",
                    userinfo: Boolean(u.username || u.password),
                    fragment: Boolean(u.hash),
                    queryPresent: Boolean(u.search),
                };
            } catch {
                redirect = { status: "malformed" };
            }
    }
    const media = (get("content-type") ?? "").split(";")[0];
    const length = get("content-length");
    return {
        status,
        contentType: [
            "application/octet-stream",
            "application/json",
            "application/vnd.oci.image.manifest.v1+json",
            "text/plain",
        ].includes(media)
            ? media
            : "other-or-missing",
        contentEncoding:
            !get("content-encoding") || get("content-encoding") === "identity"
                ? "identity-or-missing"
                : "other",
        declaredLength:
            length === null
                ? "missing"
                : /^(0|[1-9]\d{0,8})$/.test(length)
                  ? Number(length)
                  : "invalid-or-over-limit",
        redirect,
    };
}
const errorCode = (error: any) =>
    [
        "ENOTFOUND",
        "ECONNRESET",
        "ECONNREFUSED",
        "ETIMEDOUT",
        "ABORT_ERR",
        "CERT_HAS_EXPIRED",
        "UNABLE_TO_VERIFY_LEAF_SIGNATURE",
        "DEPTH_ZERO_SELF_SIGNED_CERT",
    ].includes(error?.code)
        ? error.code
        : "other";
function identity(bytes: Uint8Array, max: number, digest: string) {
    return {
        length: bytes.length,
        sha256: hash(bytes),
        originalEqual: bytes.length === max && hash(bytes) === digest,
    };
}
async function boundedBody(response: Response, max: number) {
    if (!response.body) refuse();
    const reader = response.body.getReader();
    const chunks: Uint8Array[] = [];
    let n = 0;
    while (true) {
        const p = await reader.read();
        if (p.done) break;
        n += p.value.length;
        if (n > max) {
            await reader.cancel();
            refuse();
        }
        chunks.push(p.value);
    }
    return Buffer.concat(chunks, n);
}

// Exact original node:https request settings, with NO redirect follow and closed response projection.
export async function readConfigNode(bearer: string, get = https.get) {
    let responseSeen = false;
    try {
        return await new Promise<any>((done) => {
            const req = get(
                `${origin}${namespace}/blobs/sha256:${config}`,
                {
                    signal: AbortSignal.timeout(30000),
                    agent: false,
                    maxHeaderSize: 16384,
                    headers: { Authorization: `Bearer ${bearer}` },
                },
                async (response) => {
                    responseSeen = true;
                    const headers = (key: string) =>
                        typeof response.headers[key] === "string"
                            ? (response.headers[key] as string)
                            : null;
                    const projection = projectReadbackResponse(
                        response.statusCode ?? 0,
                        headers,
                    );
                    const base = {
                        transport: "node-https",
                        resource: "original-config",
                        ...projection,
                    };
                    if (response.statusCode !== 200) {
                        response.destroy();
                        done(base);
                        return;
                    }
                    if (
                        projection.contentEncoding !== "identity-or-missing" ||
                        (typeof projection.declaredLength === "number" &&
                            projection.declaredLength > 3)
                    ) {
                        response.destroy();
                        done({ ...base, body: "refused" });
                        return;
                    }
                    try {
                        const chunks: Uint8Array[] = [];
                        let n = 0;
                        for await (const part of response) {
                            n += part.length;
                            if (n > 3) {
                                response.destroy();
                                refuse();
                            }
                            chunks.push(part);
                        }
                        done({
                            ...base,
                            body: identity(Buffer.concat(chunks, n), 3, config),
                        });
                    } catch (e) {
                        response.destroy();
                        done({
                            ...base,
                            body: "unavailable",
                            errorCode: errorCode(e),
                        });
                    }
                },
            );
            req.on("error", (error) => {
                if (!responseSeen)
                    done({
                        transport: "node-https",
                        resource: "original-config",
                        response: "unavailable",
                        errorCode: errorCode(error),
                    });
            });
        });
    } catch (e) {
        return {
            transport: "node-https",
            resource: "original-config",
            response: "unavailable",
            errorCode: errorCode(e),
        };
    }
}

// No configurable URL, method, writer callback, retry or redirect destination request.
export async function inspectReadback(
    env: NodeJS.ProcessEnv,
    token: string,
    request = fetch,
    get = https.get,
) {
    const context = readbackContext(env);
    const base = {
        event: "cli-readback-inspection",
        ...context,
        failedRunId: 41406992322,
        source,
        version,
        registryMutation: false,
        publicationVerified: false,
        accessVerified: false,
        consumerVerified: false,
    };
    const observations: any[] = [];
    let stage = "bearer";
    try {
        if (!token || !env.GITHUB_ACTOR) refuse();
        const auth = await request(
            `${origin}/token?service=ghcr.io&scope=repository%3Asolusquest%2Fsolus-logging-cli%3Apull%2Cpush`,
            {
                method: "GET",
                headers: {
                    Authorization: `Basic ${Buffer.from(`${env.GITHUB_ACTOR}:${token}`).toString("base64")}`,
                },
                redirect: "manual",
                signal: AbortSignal.timeout(30000),
            },
        );
        observations.push({
            transport: "fetch",
            resource: "bearer",
            ...projectReadbackResponse(auth.status, (k) => auth.headers.get(k)),
        });
        if (auth.status !== 200) {
            await auth.body?.cancel();
            refuse();
        }
        const value = JSON.parse(
            (await boundedBody(auth, 16384)).toString("utf8"),
        );
        const bearer = value.token || value.access_token;
        const seconds = value.expires_in ?? 60;
        if (
            typeof bearer !== "string" ||
            !bearer ||
            /\s/.test(bearer) ||
            !Number.isFinite(seconds) ||
            seconds <= 0 ||
            seconds > 3600
        )
            refuse();
        const expiry = Date.now() + seconds * 1000;
        const fresh = () => {
            if (Date.now() + 5000 >= expiry) refuse();
        };
        stage = "node-config";
        fresh();
        observations.push(await readConfigNode(bearer, get));
        for (const resource of ["original-config", "exact-version-tag"]) {
            stage = "fetch-" + resource;
            fresh();
            const response = await request(
                `${origin}${namespace}/${resource === "original-config" ? "blobs/sha256:" + config : "manifests/" + version}`,
                {
                    method: "GET",
                    headers: { Authorization: `Bearer ${bearer}` },
                    redirect: "manual",
                    signal: AbortSignal.timeout(30000),
                },
            );
            const projection = projectReadbackResponse(response.status, (k) =>
                response.headers.get(k),
            );
            const item: any = { transport: "fetch", resource, ...projection };
            observations.push(item);
            if (response.status === 200) {
                if (projection.contentEncoding !== "identity-or-missing") {
                    await response.body?.cancel();
                    item.body = "refused";
                    continue;
                }
                try {
                    const max = resource === "original-config" ? 3 : 710;
                    item.body = identity(
                        await boundedBody(response, max),
                        max,
                        resource === "original-config" ? config : manifest,
                    );
                } catch {
                    item.body = "unavailable-or-over-limit";
                }
            } else await response.body?.cancel();
        }
        return { ...base, state: "observed", observations };
    } catch {
        return { ...base, state: "refused", stage, observations };
    }
}

function localGuard(head: string) {
    const root = fileURLToPath(new URL("../../", import.meta.url));
    function git(args: string[]) {
        const r = spawnSync("git", args, {
            cwd: root,
            env: Object.fromEntries(
                ["PATH", "HOME", "TMPDIR"]
                    .filter((k) => process.env[k])
                    .map((k) => [k, process.env[k]]),
            ),
            encoding: "utf8",
            timeout: 10000,
            maxBuffer: 65536,
        });
        if (r.status !== 0 || r.signal || r.error) refuse();
        return r.stdout.trim();
    }
    if (
        git(["rev-parse", "HEAD"]) !== head ||
        git(["status", "--porcelain"]) !== "" ||
        git(["rev-parse", "HEAD:scripts/logging/accepted-candidate.json"]) !==
            "d19a8f3153f540664c21296a96d77bf3221acc25"
    )
        refuse();
    const lock = JSON.parse(
        readFileSync(
            new URL("./accepted-candidate.json", import.meta.url),
            "utf8",
        ),
    );
    if (
        lock.manifest.source.commit !== source ||
        lock.manifest.version !== version ||
        lock.manifest.payload.ociConfigSha256 !== config ||
        lock.manifest.payload.ociManifestSha256 !== manifest
    )
        refuse();
}
if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
    try {
        const context = readbackContext();
        localGuard(context.operationHead);
        const result = await inspectReadback(
            process.env,
            process.env.GITHUB_TOKEN ?? "",
        );
        console.log(JSON.stringify(result));
        if (result.state !== "observed") process.exitCode = 1;
    } catch {
        console.error("cli-readback-inspection-refused");
        process.exitCode = 1;
    }
}
