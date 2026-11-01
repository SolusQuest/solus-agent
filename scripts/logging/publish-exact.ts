// Issue123 operator entry: original c96 bytes only; never a general release path.
import {
    readFileSync,
    writeFileSync,
    mkdirSync,
    mkdtempSync,
    rmSync,
    copyFileSync,
} from "node:fs";
import { resolve, join } from "node:path";
import { pathToFileURL, fileURLToPath } from "node:url";
import { tmpdir, constants as osConstants } from "node:os";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { isDeepStrictEqual } from "node:util";
import { githubOperations } from "../release/publish.ts";

export const lock = JSON.parse(
    readFileSync(new URL("./accepted-candidate.json", import.meta.url), "utf8"),
);
const accepted = lock.manifest;
const origin = "https://ghcr.io";
const namespace = "/v2/solusquest/solus-logging-cli";
const manifestType = "application/vnd.oci.image.manifest.v1+json";
const hash = (bytes: Uint8Array) =>
    createHash("sha256").update(bytes).digest("hex");
const fail = (code: string): never => {
    throw new Error(`exact-publication:${code}`);
};
export const members = [...accepted.packages.map((p: any) => p.id), "CLI"];

export function operationContext(env = process.env) {
    const mode = env.EXACT_MODE;
    const member = env.EXACT_MEMBER;
    if (
        !["validate-exact", "publish-one"].includes(mode ?? "") ||
        !members.includes(member)
    )
        fail("mode-member");
    if (
        env.GITHUB_ACTIONS !== "true" ||
        env.GITHUB_EVENT_NAME !== "workflow_dispatch" ||
        env.GITHUB_REPOSITORY !== "SolusQuest/solus-agent" ||
        env.GITHUB_REPOSITORY_ID !== "1406091678" ||
        env.GITHUB_REF !== "refs/heads/codex/l1-123-logging-publication" ||
        env.GITHUB_RUN_ATTEMPT !== "1" ||
        !/^\d+$/.test(env.GITHUB_RUN_ID ?? "") ||
        env.RUNNER_OS !== "Linux" ||
        env.RUNNER_ARCH !== "X64" ||
        !/^[a-f0-9]{40}$/.test(env.EXPECTED_OPERATION_HEAD ?? "") ||
        env.EXPECTED_OPERATION_HEAD !== env.GITHUB_SHA ||
        env.WORKFLOW_SHA !== env.GITHUB_SHA ||
        env.SELECTED_SOURCE !== accepted.source.commit ||
        env.SELECTED_VERSION !== accepted.version
    )
        fail("context");
    return { mode, member, operationHead: env.GITHUB_SHA };
}

// This boundary runs before even constructing any immutable transport operation.
export function compareOriginal(
    manifest: any,
    bytes: Record<string, Uint8Array>,
) {
    if (!isDeepStrictEqual(manifest, accepted)) fail("original-manifest");
    const expected = [
        ...accepted.packages.map((p: any) => [p.file, p.sha256]),
        [accepted.payload.filename, accepted.payload.sha256],
        ["oci-config.json", accepted.payload.ociConfigSha256],
        ["oci-manifest.json", accepted.payload.ociManifestSha256],
    ];
    if (
        !isDeepStrictEqual(
            Object.keys(bytes).sort(),
            expected.map(([name]) => name).sort(),
        )
    )
        fail("byte-inventory");
    for (const [name, digest] of expected)
        if (
            bytes[name].length !== lock.sizes[name] ||
            hash(bytes[name]) !== digest
        )
            fail("original-bytes");
}

export async function guardOriginalOperation(
    manifest: any,
    bytes: Record<string, Uint8Array>,
    publicationGuard: () => unknown,
    operation: () => Promise<unknown>,
) {
    compareOriginal(manifest, bytes);
    publicationGuard();
    return operation();
}

const toolCommands = {
    "operation-checkout": "git",
    "registry-restore": "dotnet",
    "original-pack": "node",
} as const;
type ToolPhase = keyof typeof toolCommands;
type ToolResult = {
    status: number | null;
    signal: string | null;
    error?: unknown;
    stdout: Buffer | string | null;
    stderr: Buffer | string | null;
};
type ToolDiagnostic = {
    event: "exact-tool-failure";
    phase: ToolPhase;
    exitCode: number | null;
    signal: number | null;
    timedOut: boolean;
    spawnFailed: boolean;
    timeoutMs: number;
    codes: string[];
};

// Exported for tests of this production boundary. Never emit child output,
// arguments, paths, environment, error messages/causes/stacks or arbitrary codes.
export function tool(
    phase: ToolPhase,
    command: string,
    args: string[],
    cwd: string,
    env: NodeJS.ProcessEnv,
    timeout = 180_000,
    spawn: (
        command: string,
        args: string[],
        options: any,
    ) => ToolResult = spawnSync,
    observe: (record: ToolDiagnostic) => void = (record) =>
        console.error(JSON.stringify(record)),
) {
    if (
        !Object.hasOwn(toolCommands, phase) ||
        toolCommands[phase] !== command ||
        !Number.isSafeInteger(timeout) ||
        timeout < 1 ||
        timeout > 900_000
    )
        fail("diagnostic-callsite");
    let result: ToolResult;
    try {
        result = spawn(command, args, {
            cwd,
            env,
            timeout,
            maxBuffer: 16 * 1024 ** 2,
        });
    } catch {
        // A thrown spawn exception is equally untrusted; no exception data is logged.
        observe({
            event: "exact-tool-failure",
            phase,
            exitCode: null,
            signal: null,
            timedOut: false,
            spawnFailed: true,
            timeoutMs: timeout,
            codes: [],
        });
        return fail(`tool-failed-${phase}`);
    }
    if (result.error || result.signal || result.status !== 0) {
        const codes = new Set<string>();
        for (const output of [result.stdout, result.stderr]) {
            // Bound scanning too: the existing subprocess buffer cap remains unchanged.
            const tail = Buffer.isBuffer(output)
                ? output.subarray(-65_536).toString("utf8")
                : typeof output === "string"
                  ? output.slice(-65_536)
                  : "";
            for (const match of tail.matchAll(
                /(?<![A-Za-z0-9_])(?:NU|MSB|CS)[0-9]{4}(?![A-Za-z0-9_])/g,
            ))
                codes.add(match[0]);
        }
        const signal =
            typeof result.signal === "string" &&
            Object.hasOwn(osConstants.signals, result.signal)
                ? osConstants.signals[
                      result.signal as keyof typeof osConstants.signals
                  ]
                : null;
        observe({
            event: "exact-tool-failure",
            phase,
            exitCode:
                Number.isInteger(result.status) &&
                result.status !== null &&
                result.status >= 0 &&
                result.status <= 255
                    ? result.status
                    : null,
            signal:
                Number.isInteger(signal) &&
                signal !== null &&
                signal > 0 &&
                signal <= 128
                    ? signal
                    : null,
            timedOut:
                (result.error as { code?: unknown } | null | undefined)
                    ?.code === "ETIMEDOUT",
            spawnFailed: Boolean(result.error),
            timeoutMs: timeout,
            codes: [...codes].sort().slice(0, 8),
        });
        fail(`tool-failed-${phase}`);
    }
    return result.stdout!.toString().trim();
}

// Cold restore authenticates via an in-memory NuGet environment credential, never a login file.
async function acquirePackages(
    source: string,
    directory: string,
    packages: any[],
    actor: string,
    token: string,
) {
    const { cleanEnvironment } = await import(
        pathToFileURL(join(source, "scripts/logging/common.ts")).href
    );
    mkdirSync(directory);
    const cache = join(directory, "packages");
    const env = cleanEnvironment(join(directory, "home"));
    env.NUGET_PACKAGES = cache;
    env.NuGetPackageSourceCredentials_selected = `Username=${actor};Password=${token};ValidAuthenticationTypes=Basic`;
    copyFileSync(join(source, "global.json"), join(directory, "global.json"));
    writeFileSync(
        join(directory, "NuGet.Config"),
        '<configuration><packageSources><clear/><add key="selected" value="https://nuget.pkg.github.com/SolusQuest/index.json"/></packageSources><packageSourceMapping><clear/><packageSource key="selected"><package pattern="Solus.*"/><package pattern="SolusAgent.*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders><disabledPackageSources><clear/></disabledPackageSources></configuration>\n',
    );
    writeFileSync(
        join(directory, "Fetch.csproj"),
        `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup><ItemGroup>${packages.map((p) => `<PackageReference Include="${p.id}" Version="[${p.version}]"/>`).join("")}</ItemGroup></Project>`,
    );
    try {
        tool(
            "registry-restore",
            "dotnet",
            [
                "restore",
                "Fetch.csproj",
                "--configfile",
                "NuGet.Config",
                "--no-http-cache",
                "-p:ImportDirectoryBuildProps=false",
                "-p:ImportDirectoryBuildTargets=false",
                "-p:ManagePackageVersionsCentrally=false",
                "-p:RestoreFallbackFolders=",
                `-p:RestorePackagesPath=${cache}`,
            ],
            directory,
            env,
        );
    } finally {
        delete env.NuGetPackageSourceCredentials_selected;
    }
    return packages.map((p) => {
        const base = join(cache, p.id.toLowerCase(), p.version);
        const archive = join(base, `${p.id.toLowerCase()}.${p.version}.nupkg`);
        const metadata = JSON.parse(
            readFileSync(join(base, ".nupkg.metadata"), "utf8"),
        );
        if (
            metadata.source?.replace(/\/$/, "") !==
                accepted.agentApi.registry ||
            hash(readFileSync(archive)) !== p.sha256
        )
            fail("registry-package-source-bytes");
        return archive;
    });
}

// A finite scope request is not evidence of effective push authorization.
export async function registryBearer(
    actor: string,
    token: string,
    request = fetch,
) {
    const response = await request(
        `${origin}/token?service=ghcr.io&scope=repository%3Asolusquest%2Fsolus-logging-cli%3Apull%2Cpush`,
        {
            headers: {
                Authorization: `Basic ${Buffer.from(`${actor}:${token}`).toString("base64")}`,
            },
            redirect: "error",
            signal: AbortSignal.timeout(30_000),
        },
    );
    if (!response.ok) fail("registry-bearer-capability");
    const value = await response.json();
    const bearer = value.token || value.access_token;
    const seconds = value.expires_in ?? 60;
    if (
        typeof bearer !== "string" ||
        !bearer ||
        /\s/.test(bearer) ||
        !Number.isFinite(seconds) ||
        seconds <= 0
    )
        fail("registry-bearer-response");
    return {
        bearer,
        expiresAt: Date.now() + seconds * 1000,
        declaredLifetimeSeconds: seconds,
    };
}

// Tests inject this exact transport, not an alternate protocol implementation.
export function ociTransport(
    bearer: string,
    expiresAt: number,
    request = fetch,
    observe: (entry: {
        method: string;
        endpoint: string;
        status: number;
    }) => void = () => {},
) {
    async function send(
        url: string,
        method = "GET",
        body?: Uint8Array,
        contentType?: string,
    ) {
        const parsed = new URL(url);
        if (
            parsed.origin !== origin ||
            !parsed.pathname.startsWith(namespace + "/") ||
            parsed.username ||
            parsed.password ||
            parsed.hash
        )
            fail("upload-location-origin");
        if (Date.now() + 5000 >= expiresAt) fail("registry-bearer-expiry");
        const response = await request(url, {
            method,
            headers: {
                Authorization: `Bearer ${bearer}`,
                Accept: manifestType,
                ...(contentType ? { "Content-Type": contentType } : {}),
            },
            body: body as any,
            redirect: "error",
            signal: AbortSignal.timeout(90_000),
        });
        // Never retain an opaque upload session path, query or Location header.
        const endpoint = parsed.pathname.startsWith(
            namespace + "/blobs/uploads/",
        )
            ? namespace + "/blobs/uploads/<session>"
            : parsed.pathname;
        observe({ method, endpoint, status: response.status });
        return response;
    }
    async function readManifest(reference: string) {
        const response = await send(
            `${origin}${namespace}/manifests/${reference}`,
        );
        if (response.status === 404) return undefined;
        if (
            response.status !== 200 ||
            response.headers.get("content-type")?.split(";")[0] !== manifestType
        )
            fail("manifest-unreadable");
        const bytes = Buffer.from(await response.arrayBuffer());
        if (
            bytes.length !== lock.sizes["oci-manifest.json"] ||
            hash(bytes) !== accepted.payload.ociManifestSha256 ||
            response.headers.get("docker-content-digest") !==
                `sha256:${accepted.payload.ociManifestSha256}`
        )
            fail("manifest-conflict");
        return bytes;
    }
    async function blob(bytes: Uint8Array) {
        const digest = `sha256:${hash(bytes)}`;
        const base = `${origin}${namespace}/blobs/${digest}`;
        const head = await send(base, "HEAD");
        if (head.status !== 200 && head.status !== 404)
            fail("blob-state-unknown");
        if (head.status === 404) {
            const created = await send(
                `${origin}${namespace}/blobs/uploads/`,
                "POST",
            );
            if (created.status !== 202 || !created.headers.get("location"))
                fail("upload-start-outcome-unknown");
            const location = new URL(
                created.headers.get("location")!,
                `${origin}${namespace}/blobs/uploads/`,
            );
            // Preserve opaque upload query parameters. No forwarding to a different origin.
            if (
                location.origin !== origin ||
                !location.pathname.startsWith(namespace + "/blobs/uploads/") ||
                location.username ||
                location.password ||
                location.hash ||
                location.searchParams.has("digest")
            )
                fail("upload-location-origin");
            const uploadUrl =
                location.href +
                (location.search ? "&" : "?") +
                "digest=" +
                encodeURIComponent(digest);
            const uploaded = await send(
                uploadUrl,
                "PUT",
                bytes,
                "application/octet-stream",
            );
            if (
                uploaded.status !== 201 ||
                uploaded.headers.get("docker-content-digest") !== digest
            )
                fail("blob-upload-outcome-unknown");
        }
        return { base, digest };
    }
    return { readManifest, blob, send };
}

export async function publishOci(
    bytes: Record<string, Uint8Array>,
    transport: ReturnType<typeof ociTransport>,
    download: (url: string) => Promise<Uint8Array>,
) {
    async function verifyBlobs() {
        for (const name of ["oci-config.json", accepted.payload.filename]) {
            const expected = bytes[name];
            const actual = await download(
                `${origin}${namespace}/blobs/sha256:${hash(expected)}`,
            );
            if (
                actual.length !== expected.length ||
                hash(actual) !== hash(expected)
            )
                fail("blob-readback");
        }
    }
    if (await transport.readManifest(accepted.version)) {
        await verifyBlobs();
        return "retained-identical";
    }
    for (const name of ["oci-config.json", accepted.payload.filename]) {
        await transport.blob(bytes[name]);
        // Read each immutable blob before progressing to the manifest.
        const actual = await download(
            `${origin}${namespace}/blobs/sha256:${hash(bytes[name])}`,
        );
        if (
            actual.length !== bytes[name].length ||
            hash(actual) !== hash(bytes[name])
        )
            fail("blob-readback");
    }
    // Exclusive external publisher window is an operator gate; this is not CAS.
    if (await transport.readManifest(accepted.version)) {
        await verifyBlobs();
        return "retained-identical";
    }
    const response = await transport.send(
        `${origin}${namespace}/manifests/${accepted.version}`,
        "PUT",
        bytes["oci-manifest.json"],
        manifestType,
    );
    if (
        response.status !== 201 ||
        response.headers.get("docker-content-digest") !==
            `sha256:${accepted.payload.ociManifestSha256}`
    )
        fail("manifest-upload-outcome-unknown");
    if (
        !(await transport.readManifest(accepted.version)) ||
        !(await transport.readManifest(
            `sha256:${accepted.payload.ociManifestSha256}`,
        ))
    )
        fail("manifest-readback-missing");
    await verifyBlobs();
    return "published-readback";
}

export async function publishNugetMember(operations: {
    exists: () => Promise<boolean>;
    verify: () => Promise<unknown>;
    push: () => Promise<unknown>;
}) {
    if (await operations.exists()) {
        await operations.verify();
        return "retained-identical";
    }
    // The second authenticated check narrows the exclusive operator collision window.
    if (await operations.exists()) {
        await operations.verify();
        return "retained-identical";
    }
    await operations.push();
    if (!(await operations.exists())) fail("nuget-readback-missing");
    await operations.verify();
    return "published-readback";
}

async function execute() {
    const context = operationContext();
    const operationRoot = fileURLToPath(new URL("../../", import.meta.url));
    if (
        tool(
            "operation-checkout",
            "git",
            ["rev-parse", "HEAD"],
            operationRoot,
            process.env,
        ) !== context.operationHead
    )
        fail("operation-checkout");
    const source = resolve(
        process.env.EXACT_SOURCE_ROOT ?? fail("source-path"),
    );
    const token = process.env.GITHUB_TOKEN;
    const actor = process.env.GITHUB_ACTOR;
    if (!token || !actor || !/^[-A-Za-z0-9\[\]]+$/.test(actor))
        fail("existing-workflow-credential");
    const load = (name: string) =>
        import(pathToFileURL(join(source, name)).href);
    const { cleanEnvironment, selectedSource } = await load(
        "scripts/logging/common.ts",
    );
    if (!isDeepStrictEqual(selectedSource(), accepted.source))
        fail("candidate-source");
    const workspace = mkdtempSync(join(tmpdir(), "solus-123-exact-"));
    try {
        const baseline = {
            id: accepted.agentApi.id,
            version: accepted.agentApi.version,
            sha256: accepted.agentApi.packageSha256,
        };
        const [api] = await acquirePackages(
            source,
            join(workspace, "api-fetch"),
            [baseline],
            actor,
            token,
        );
        const output = join(workspace, "candidate");
        mkdirSync(output);
        // Build in a child with only the original clean build environment.
        tool(
            "original-pack",
            "node",
            [
                join(source, "scripts/logging/pack.ts"),
                "--version",
                accepted.version,
                "--output",
                join(output, accepted.packageDirectory),
                "--api-package",
                api,
            ],
            source,
            cleanEnvironment(join(workspace, "build-home")),
            900_000,
        );
        const manifest = JSON.parse(
            readFileSync(join(output, "logging-release.json"), "utf8"),
        );
        const bytes: Record<string, Uint8Array> = {};
        for (const p of accepted.packages)
            bytes[p.file] = readFileSync(
                join(output, accepted.packageDirectory, p.file),
            );
        for (const name of [
            accepted.payload.filename,
            "oci-config.json",
            "oci-manifest.json",
        ])
            bytes[name] = readFileSync(
                join(output, accepted.payload.directory, name),
            );
        const { publicationFiles } = await load("scripts/logging/manifest.ts");
        await guardOriginalOperation(
            manifest,
            bytes,
            () =>
                publicationFiles(
                    join(output, accepted.packageDirectory),
                    output,
                    manifest,
                    api,
                ),
            async () => {
                let result = "validated-original-no-publication";
                if (context.mode === "publish-one") {
                    const operations = githubOperations(
                        "SolusQuest",
                        "solus-agent",
                        token,
                    );
                    async function state(p: any) {
                        const matches = (await operations.packages()).filter(
                            (v) => v.name.toLowerCase() === p.id.toLowerCase(),
                        );
                        if (
                            matches.length > 1 ||
                            (matches.length === 1 &&
                                (matches[0].visibility !== "private" ||
                                    matches[0].package_type !== "nuget"))
                        )
                            fail("nuget-private-state");
                        return (
                            matches.length === 1 &&
                            (
                                await operations.versions(matches[0].name)
                            ).includes(p.version)
                        );
                    }
                    async function installed(p: any, suffix: string) {
                        await acquirePackages(
                            source,
                            join(workspace, suffix),
                            [p],
                            actor,
                            token,
                        );
                    }
                    const selected = accepted.packages.find(
                        (p: any) => p.id === context.member,
                    );
                    const dependencies =
                        context.member === "CLI"
                            ? accepted.packages
                            : accepted.packages.filter((p: any) =>
                                  Object.hasOwn(selected.dependencies, p.id),
                              );
                    for (let i = 0; i < dependencies.length; i++) {
                        if (!(await state(dependencies[i])))
                            fail("dependency-not-published");
                        await installed(dependencies[i], `dependency-${i}`);
                    }
                    if (context.member !== "CLI") {
                        result = await publishNugetMember({
                            exists: () => state(selected),
                            verify: () =>
                                installed(selected, "member-readback"),
                            push: () =>
                                operations.push(
                                    join(
                                        output,
                                        accepted.packageDirectory,
                                        selected.file,
                                    ),
                                ),
                        });
                    } else {
                        const auth = await registryBearer(actor, token);
                        const { downloadArtifact } = await load(
                            "actions/logging/transport.mjs",
                        );
                        result = await publishOci(
                            bytes,
                            ociTransport(
                                auth.bearer,
                                auth.expiresAt,
                                fetch,
                                (entry) =>
                                    console.log(
                                        JSON.stringify({
                                            event: "oci-http",
                                            ...entry,
                                        }),
                                    ),
                            ),
                            (url) =>
                                downloadArtifact(
                                    url,
                                    auth.bearer,
                                    AbortSignal.timeout(60_000),
                                ),
                        );
                        const response = await fetch(
                            "https://api.github.com/orgs/SolusQuest/packages/container/solus-logging-cli",
                            {
                                headers: {
                                    Authorization: `Bearer ${token}`,
                                    Accept: "application/vnd.github+json",
                                    "X-GitHub-Api-Version": "2022-11-28",
                                },
                                redirect: "error",
                                signal: AbortSignal.timeout(30_000),
                            },
                        );
                        if (
                            !response.ok ||
                            (await response.json()).visibility !== "private"
                        )
                            fail("container-private-readback");
                    }
                }
                console.log(
                    JSON.stringify({
                        operationHead: context.operationHead,
                        candidateSource: accepted.source.commit,
                        candidateSha256: accepted.candidateSha256,
                        packageSetSha256: accepted.packageSetSha256,
                        destination:
                            context.member === "CLI"
                                ? {
                                      registry:
                                          accepted.destinations.cli.registry,
                                      repository:
                                          accepted.destinations.cli.repository,
                                      versionTag: accepted.version,
                                      blobUrl:
                                          accepted.destinations.cli.blobUrl,
                                  }
                                : accepted.destinations.nuget,
                        originalIdentities: {
                            packages: accepted.packages.map((p: any) => ({
                                id: p.id,
                                version: p.version,
                                sha256: p.sha256,
                            })),
                            cliArchiveSha256: accepted.payload.sha256,
                            ociManifestSha256:
                                accepted.payload.ociManifestSha256,
                            ociConfigSha256: accepted.payload.ociConfigSha256,
                        },
                        nugetPushHttpStatusExposedByExistingTransport: false,
                        mode: context.mode,
                        member: context.member,
                        result,
                        accessSettingsVerified: false,
                        consumerAccessVerified: false,
                        runId: process.env.GITHUB_RUN_ID,
                        attempt: process.env.GITHUB_RUN_ATTEMPT,
                    }),
                );
            },
        );
    } finally {
        rmSync(workspace, { recursive: true, force: true });
    }
}
if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
    try {
        await execute();
    } catch (error) {
        const code =
            error instanceof Error &&
            /^exact-publication:[a-z-]+$/.test(error.message)
                ? error.message
                : "exact-publication:transport-or-outcome-unknown";
        console.error(
            `${code}; stopped; reconcile original run and registry before recovery; no automatic retry`,
        );
        process.exitCode = 1;
    }
}
