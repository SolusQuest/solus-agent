// Fixed run10 observation for failed CLI run41320103950. Creates one session, never uploads.
import { readFileSync } from "node:fs";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const origin = "https://ghcr.io";
const namespace = "/v2/solusquest/solus-logging-cli";
const manifestType = "application/vnd.oci.image.manifest.v1+json";
const configDigest =
    "ca3d163bab055381827226140568f3bef7eaac187cebd76878e0b63e9e442356";
const source = "c96a1b5cf9746a7fa569a339c583e07e3e103ee6";
const version = "0.1.0-internal.1";
const refuse = (): never => {
    throw new Error("cli-upload-inspection-refused");
};

export function inspectionContext(env = process.env) {
    if (
        env.GITHUB_ACTIONS !== "true" ||
        env.GITHUB_EVENT_NAME !== "workflow_dispatch" ||
        env.GITHUB_REPOSITORY !== "SolusQuest/solus-agent" ||
        env.GITHUB_REPOSITORY_ID !== "1406091678" ||
        env.GITHUB_REF !== "refs/heads/codex/l1-123-logging-publication" ||
        env.GITHUB_JOB !== "inspect-cli-upload" ||
        env.GITHUB_RUN_ATTEMPT !== "1" ||
        env.GITHUB_RUN_NUMBER !== "10" ||
        env.GITHUB_ACTOR_ID !== "16307884" ||
        !/^[1-9]\d{0,19}$/.test(env.GITHUB_RUN_ID ?? "") ||
        env.RUNNER_OS !== "Linux" ||
        env.RUNNER_ARCH !== "X64" ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_OPERATION_HEAD ?? "") ||
        env.EXPECTED_OPERATION_HEAD !== env.GITHUB_SHA ||
        env.WORKFLOW_SHA !== env.GITHUB_SHA ||
        env.EXACT_MODE !== "inspect-cli-upload" ||
        env.EXACT_MEMBER !== "CLI" ||
        env.SELECTED_SOURCE !== source ||
        env.SELECTED_VERSION !== version
    )
        refuse();
    return {
        operationHead: env.GITHUB_SHA!,
        runId: env.GITHUB_RUN_ID!,
        runNumber: 10,
        attempt: 1,
    };
}

// Only a closed projection escapes this function. Never return an opaque URL, path or query.
export function projectUploadLocation(raw: string | null) {
    const invalid = {
        form: "invalid",
        reason: "invalid",
        originMatch: false,
        namespaceMatch: false,
        userinfo: false,
        fragment: false,
        digestQuery: false,
        queryPresent: false,
        pathClass: "unparsed",
        oldGuardWouldAdmit: false,
    };
    if (!raw || raw.length > 4096) return invalid;
    try {
        const url = new URL(raw, `${origin}${namespace}/blobs/uploads/`);
        const originMatch = url.origin === origin;
        const namespaceMatch = url.pathname.startsWith(namespace + "/");
        const uploadPath = url.pathname.startsWith(
            namespace + "/blobs/uploads/",
        );
        const userinfo = Boolean(url.username || url.password);
        const fragment = Boolean(url.hash);
        const digestQuery = url.searchParams.has("digest");
        const reason = !originMatch
            ? "origin"
            : !uploadPath
              ? "upload-path"
              : userinfo
                ? "userinfo"
                : fragment
                  ? "fragment"
                  : digestQuery
                    ? "digest-query"
                    : "admitted";
        const pathClass =
            url.pathname === namespace + "/blobs/uploads/"
                ? "uploads-root"
                : uploadPath
                  ? "uploads-session"
                  : url.pathname.startsWith(namespace + "/blobs/upload/")
                    ? "upload-singular-session"
                    : namespaceMatch
                      ? "namespace-other"
                      : "outside-namespace";
        return {
            form: /^[A-Za-z][A-Za-z0-9+.-]*:/.test(raw)
                ? "absolute"
                : raw.startsWith("//")
                  ? "network-relative"
                  : raw.startsWith("/")
                    ? "root-relative"
                    : "path-relative",
            reason,
            originMatch,
            namespaceMatch,
            userinfo,
            fragment,
            digestQuery,
            queryPresent: Boolean(url.search),
            pathClass,
            oldGuardWouldAdmit: reason === "admitted",
        };
    } catch {
        return invalid;
    }
}

// No configurable endpoint/method, writer callback, retry, body upload or publisher import.
export async function inspectUpload(
    env: NodeJS.ProcessEnv,
    token: string,
    request = fetch,
) {
    const context = inspectionContext(env);
    const observations: { stage: string; status: number }[] = [];
    let stage = "bearer";
    // A sent POST without a response may already have created a session.
    let uploadSessionCreated: boolean | null = false;
    const base = {
        event: "cli-upload-location-inspection",
        ...context,
        failedRunId: 41320103950,
        source,
        version,
        observations,
        immutableBytesUploaded: false,
        publicationVerified: false,
        accessVerified: false,
        consumerVerified: false,
    };
    try {
        if (!token || !env.GITHUB_ACTOR) refuse();
        const auth = await request(
            `${origin}/token?service=ghcr.io&scope=repository%3Asolusquest%2Fsolus-logging-cli%3Apull%2Cpush`,
            {
                method: "GET",
                headers: {
                    Authorization: `Basic ${Buffer.from(`${env.GITHUB_ACTOR}:${token}`).toString("base64")}`,
                },
                redirect: "error",
                signal: AbortSignal.timeout(30000),
            },
        );
        observations.push({ stage, status: auth.status });
        if (auth.status !== 200) refuse();
        if (!auth.body) refuse();
        const reader = auth.body.getReader();
        const chunks: Uint8Array[] = [];
        let size = 0;
        while (true) {
            const chunk = await reader.read();
            if (chunk.done) break;
            size += chunk.value.length;
            if (size > 16384) {
                await reader.cancel();
                refuse();
            }
            chunks.push(chunk.value);
        }
        const value = JSON.parse(Buffer.concat(chunks).toString("utf8"));
        const bearer = value.token || value.access_token;
        const seconds = value.expires_in ?? 60;
        if (
            typeof bearer !== "string" ||
            !bearer ||
            /\s/.test(bearer) ||
            !Number.isFinite(seconds) ||
            seconds <= 5 ||
            seconds > 3600
        )
            refuse();
        const expires = Date.now() + seconds * 1000;
        const headers = {
            Authorization: `Bearer ${bearer}`,
            Accept: manifestType,
        };
        async function fixedRead(url: string, method: "GET" | "HEAD" | "POST") {
            if (Date.now() + 5000 >= expires) refuse();
            if (method === "POST") uploadSessionCreated = null;
            const response = await request(url, {
                method,
                headers,
                redirect: "error",
                signal: AbortSignal.timeout(30000),
            });
            observations.push({ stage, status: response.status });
            return response;
        }
        stage = "tag";
        const tag = await fixedRead(
            `${origin}${namespace}/manifests/${version}`,
            "GET",
        );
        await tag.body?.cancel();
        if (tag.status !== 404) refuse();
        stage = "config";
        const config = await fixedRead(
            `${origin}${namespace}/blobs/sha256:${configDigest}`,
            "HEAD",
        );
        await config.body?.cancel();
        if (config.status !== 404) refuse();
        stage = "upload-session";
        const started = await fixedRead(
            `${origin}${namespace}/blobs/uploads/`,
            "POST",
        );
        if (started.status === 202) uploadSessionCreated = true;
        const projection = projectUploadLocation(
            started.headers.get("location"),
        );
        await started.body?.cancel();
        if (started.status !== 202) refuse();
        return {
            ...base,
            state: "observed",
            uploadSessionCreated,
            location: projection,
        };
    } catch {
        // Provider exceptions/headers/body/query/token values cannot escape through errors.
        return { ...base, state: "refused", stage, uploadSessionCreated };
    }
}

function localSourceGuard(head: string) {
    const root = fileURLToPath(new URL("../../", import.meta.url));
    const clean = Object.fromEntries(
        ["PATH", "HOME", "TMPDIR"]
            .filter((k) => process.env[k])
            .map((k) => [k, process.env[k]]),
    );
    function git(args: string[]) {
        const r = spawnSync("git", args, {
            cwd: root,
            env: clean,
            encoding: "utf8",
            timeout: 10000,
            maxBuffer: 65536,
        });
        if (r.error || r.signal || r.status !== 0) refuse();
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
        lock.manifest.payload.ociConfigSha256 !== configDigest
    )
        refuse();
}

if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
    try {
        const context = inspectionContext();
        localSourceGuard(context.operationHead);
        const result = await inspectUpload(
            process.env,
            process.env.GITHUB_TOKEN ?? "",
        );
        console.log(JSON.stringify(result));
        if (result.state !== "observed") process.exitCode = 1;
    } catch {
        console.error("cli-upload-inspection-refused");
        process.exitCode = 1;
    }
}
