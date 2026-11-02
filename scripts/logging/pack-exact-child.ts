// One incident boundary for the unchanged accepted c96 packer. Never publishes.
import {
    readFileSync,
    writeFileSync,
    mkdtempSync,
    mkdirSync,
    rmSync,
    lstatSync,
} from "node:fs";
import { resolve, join, dirname, sep } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { spawnSync } from "node:child_process";
import { isDeepStrictEqual } from "node:util";

const accepted = JSON.parse(
    readFileSync(new URL("./accepted-candidate.json", import.meta.url), "utf8"),
).manifest;
const modules = {
    "scripts/logging/pack.ts": "pack",
    "scripts/logging/common.ts": "common",
    "scripts/logging/pack-projects.ts": "projects",
    "scripts/logging/build-payload.ts": "payload",
    "scripts/logging/package-archive.ts": "package",
    "scripts/logging/manifest.ts": "manifest",
    "actions/logging/archive.mjs": "archive",
} as const;
const stages = [
    "source-identity",
    "load-original",
    "source-inventory",
    "output-preflight",
    "toolchain",
    "api-baseline",
    "project-staging",
    "library-pack",
    "payload-build",
    "payload-archive",
    "action-pin",
    "manifest",
    "output-copy",
    "cleanup",
    "unknown",
] as const;
const fixedErrors = [
    "invalid_version",
    "candidate_overwrite",
    "candidate_toolchain_requires_pinned_SDK_and_Node24.21.0",
    "payload_package_binary",
    "action_source_changed",
    "source_project_graph_changed",
    "fixture_identity",
    "genuine_api_archive_required",
    "genuine_api_binary_required",
    "package_semantics_changed",
    "source_nonregular",
    "api_source_baseline_changed",
    "package_assets",
    "package_identity",
    "package_dependencies",
    "package_metadata",
    "package_relationships",
    "manifest_path",
    "manifest_selection",
    "manifest_source",
    "candidate_feed_inventory",
    "candidate_hash",
    "candidate_dependencies",
    "manifest_payload",
    "payload_portable_gzip",
    "payload_manifest_hash",
    "manifest_toolchain",
    "oci_manifest",
    "manifest_destination",
    "fixture_in_payload",
    "manifest_digest",
    "invalid_artifact",
    "payload-version",
    "payload-publish-failed",
    "payload-flat-required",
    "tool_failure",
    "original_source_mismatch",
    "credential_environment_refused",
    "bridge_arguments_refused",
    "node-ENOENT",
    "node-EACCES",
    "node-EPERM",
    "node-ENOSPC",
    "node-EIO",
    "node-EMFILE",
    "node-ENFILE",
    "node-ENOTDIR",
    "node-ELOOP",
    "node-ERR_MODULE_NOT_FOUND",
    "node-ERR_UNKNOWN_FILE_EXTENSION",
    "unclassified",
] as const;
const messageAliases: Record<string, string> = {
    "An exact payload version is required.": "payload-version",
    "Payload publish failed.": "payload-publish-failed",
    "The payload format requires flat regular files.": "payload-flat-required",
};
const hints = {
    "git-dubious-ownership": /detected dubious ownership/i,
    "git-home-unset": /\$HOME not set/i,
    "sdk-not-found":
        /requested SDK version|no \.NET SDKs were found|compatible \.NET SDK was not found/i,
    "nuget-service-index": /unable to load the service index/i,
    "unzip-filename-unmatched": /filename not matched/i,
    "unzip-invalid-archive":
        /end-of-central-directory signature not found|overlapped components/i,
    "permission-denied": /permission denied/i,
    "no-space": /no space left on device/i,
} as const;
const commands = ["git", "dotnet", "unzip", "unknown"];
const projectIds = [
    ...accepted.packages.map((p: any) => p.id),
    accepted.agentApi.id,
    "Solus.Logging.Cli",
] as string[];
const versionPattern = /^\d{1,2}\.\d{1,2}\.\d{3,4}$/;
const nodeVersionPattern = /^\d{1,2}\.\d{1,2}\.\d{1,4}$/;
const zlibVersionPattern = /^\d{1,2}(?:\.\d{1,2}){1,3}(?:-[a-z0-9.-]{1,40})?$/;
const integer = (value: unknown, max = 255): value is number =>
    Number.isInteger(value) && Number(value) >= 0 && Number(value) <= max;
const tail = (value: unknown) =>
    Buffer.isBuffer(value)
        ? value.subarray(-65536).toString("utf8")
        : typeof value === "string"
          ? value.slice(-65536)
          : "";
const codeList = (value: string) =>
    [
        ...new Set(
            [
                ...value.matchAll(
                    /(?<![A-Za-z0-9_])(?:NU|MSB|CS|NETSDK)[0-9]{4}(?![A-Za-z0-9_])/g,
                ),
            ].map((m) => m[0]),
        ),
    ]
        .sort()
        .slice(0, 8);
const refuse = (): never => {
    throw new Error("original_pack_report_invalid");
};

type Failure = {
    stage: string;
    id: string;
    frames: { module: string; line: number }[];
    tool: null | {
        command: string;
        status: number | null;
        codes: string[];
        hints: string[];
        projects: string[];
    };
};

function sourceFrames(stack: unknown, source: string) {
    const frames: Failure["frames"] = [];
    if (typeof stack !== "string") return frames;
    for (const line of stack.slice(0, 16384).split("\n").slice(1, 24)) {
        for (const [file, module] of Object.entries(modules)) {
            const path = join(source, file);
            for (const prefix of [pathToFileURL(path).href + ":", path + ":"]) {
                const index = line.indexOf(prefix);
                if (index < 0) continue;
                const match = /^(\d{1,4}):\d+\)?$/.exec(
                    line.slice(index + prefix.length),
                );
                if (
                    match &&
                    integer(Number(match[1]), 2000) &&
                    Number(match[1]) > 0 &&
                    !frames.some(
                        (f) =>
                            f.module === module && f.line === Number(match[1]),
                    )
                )
                    frames.push({ module, line: Number(match[1]) });
            }
        }
        if (frames.length >= 8) break;
    }
    return frames.slice(0, 8);
}
function frameStage(frames: Failure["frames"]) {
    // Fixed c96 source locations only, never arbitrary function or path text.
    if (frames.some((f) => f.module === "payload")) return "payload-build";
    const project = frames.find((f) => f.module === "projects");
    if (project)
        return project.line >= 55
            ? "library-pack"
            : project.line >= 47
              ? "api-baseline"
              : project.line >= 24
                ? "source-inventory"
                : "project-staging";
    if (frames.some((f) => f.module === "manifest")) return "manifest";
    if (frames.some((f) => f.module === "archive")) return "payload-archive";
    if (frames.some((f) => f.module === "package")) return "api-baseline";
    const pack = frames.find((f) => f.module === "pack");
    if (pack) {
        if (pack.line <= 17) return "output-preflight";
        if (pack.line === 18) return "source-inventory";
        if (pack.line <= 21) return "toolchain";
        if (pack.line <= 24) return "api-baseline";
        if (pack.line === 25) return "library-pack";
        if (pack.line <= 28) return "payload-build";
        if (pack.line <= 30) return "payload-archive";
        if (pack.line <= 35) return "action-pin";
        if (pack.line <= 56) return "manifest";
        if (pack.line <= 61) return "output-copy";
        if (pack.line === 62) return "manifest";
        if (pack.line <= 67) return "output-copy";
        return "cleanup";
    }
    return frames.some((f) => f.module === "common")
        ? "source-inventory"
        : "unknown";
}

export function classifyOriginalError(
    error: unknown,
    source: string,
    ToolFailure: any,
    fallback = "unknown",
): Failure {
    let result: Failure = {
        stage: stages.includes(fallback as any) ? fallback : "unknown",
        id: "unclassified",
        frames: [],
        tool: null,
    };
    try {
        if (!(error instanceof Error)) return result;
        const id = Object.hasOwn(messageAliases, error.message)
            ? messageAliases[error.message]
            : error.message;
        if (fixedErrors.includes(id as any)) result.id = id;
        else if (
            typeof (error as any).code === "string" &&
            fixedErrors.includes(("node-" + (error as any).code) as any)
        )
            result.id = "node-" + (error as any).code;
        result.frames = sourceFrames(error.stack, source);
        const inferred = frameStage(result.frames);
        if (inferred !== "unknown" && fallback === "unknown")
            result.stage = inferred;
        if (ToolFailure && error instanceof ToolFailure) {
            const e = error as any;
            const output = tail(e.output);
            result.id = "tool_failure";
            result.tool = {
                command: commands.includes(e.command) ? e.command : "unknown",
                status: integer(e.status) ? e.status : null,
                codes: codeList(output),
                projects: projectIds
                    .filter((id) => {
                        const text = output.toLowerCase();
                        const version =
                            id === accepted.agentApi.id
                                ? accepted.agentApi.version
                                : accepted.version;
                        return (
                            text.includes((id + ".csproj").toLowerCase()) ||
                            text.includes(
                                (id + "." + version + ".nupkg").toLowerCase(),
                            )
                        );
                    })
                    .sort(),
                hints: Object.entries(hints)
                    .filter(([, pattern]) => pattern.test(output))
                    .map(([name]) => name),
            };
        }
        if (
            result.id ===
            "candidate_toolchain_requires_pinned_SDK_and_Node24.21.0"
        )
            result.stage = "toolchain";
        if (
            result.id === "payload-publish-failed" ||
            result.id === "payload-version" ||
            result.id === "payload-flat-required"
        )
            result.stage = "payload-build";
        if (result.id === "candidate_overwrite")
            result.stage = "output-preflight";
        if (result.id === "action_source_changed") result.stage = "action-pin";
    } catch {
        result = {
            stage: "unknown",
            id: "unclassified",
            frames: [],
            tool: null,
        };
    }
    return result;
}

function keys(value: any, expected: string[]) {
    if (
        !value ||
        typeof value !== "object" ||
        Array.isArray(value) ||
        !isDeepStrictEqual(Object.keys(value).sort(), [...expected].sort())
    )
        refuse();
}
function validateFailure(value: any) {
    if (value === null) return;
    keys(value, ["stage", "id", "frames", "tool"]);
    if (
        !stages.includes(value.stage) ||
        !fixedErrors.includes(value.id) ||
        !Array.isArray(value.frames) ||
        value.frames.length > 8
    )
        refuse();
    for (const f of value.frames) {
        keys(f, ["module", "line"]);
        if (
            !Object.values(modules).includes(f.module) ||
            !integer(f.line, 2000) ||
            f.line === 0
        )
            refuse();
    }
    if (value.tool !== null) {
        keys(value.tool, ["command", "status", "codes", "hints", "projects"]);
        if (
            !commands.includes(value.tool.command) ||
            (value.tool.status !== null && !integer(value.tool.status)) ||
            !Array.isArray(value.tool.codes) ||
            value.tool.codes.length > 8 ||
            value.tool.codes.some(
                (c: unknown) =>
                    typeof c !== "string" ||
                    !/^(NU|MSB|CS|NETSDK)[0-9]{4}$/.test(c),
            ) ||
            !Array.isArray(value.tool.hints) ||
            value.tool.hints.length > 8 ||
            value.tool.hints.some(
                (h: unknown) =>
                    typeof h !== "string" || !Object.hasOwn(hints, h),
            )
        )
            refuse();
        if (
            !Array.isArray(value.tool.projects) ||
            value.tool.projects.length > 6 ||
            value.tool.projects.some(
                (p: unknown) =>
                    typeof p !== "string" || !projectIds.includes(p),
            )
        )
            refuse();
    }
}
export function validateOriginalReport(value: any, operationHead: string) {
    keys(value, [
        "event",
        "source",
        "operationHead",
        "node",
        "zlib",
        "sourceVerified",
        "sdk",
        "api",
        "pack",
    ]);
    if (
        value.event !== "original-pack-evidence" ||
        value.source !== accepted.source.commit ||
        value.operationHead !== operationHead ||
        !/^[a-f0-9]{40}$/.test(operationHead) ||
        typeof value.node !== "string" ||
        !nodeVersionPattern.test(value.node) ||
        typeof value.zlib !== "string" ||
        !zlibVersionPattern.test(value.zlib) ||
        typeof value.sourceVerified !== "boolean"
    )
        refuse();
    keys(value.sdk, ["expected", "selected", "available", "failure"]);
    if (
        value.sdk.expected !== accepted.buildTools.dotnetSdk ||
        (value.sdk.selected !== null &&
            (typeof value.sdk.selected !== "string" ||
                !versionPattern.test(value.sdk.selected))) ||
        !Array.isArray(value.sdk.available) ||
        value.sdk.available.length > 16 ||
        value.sdk.available.some(
            (v: unknown) => typeof v !== "string" || !versionPattern.test(v),
        )
    )
        refuse();
    validateFailure(value.sdk.failure);
    keys(value.api, ["verified", "failure"]);
    if (
        typeof value.api.verified !== "boolean" ||
        (value.api.verified && value.api.failure !== null)
    )
        refuse();
    validateFailure(value.api.failure);
    keys(value.pack, ["ok", "failure"]);
    if (
        typeof value.pack.ok !== "boolean" ||
        value.pack.ok === (value.pack.failure !== null)
    )
        refuse();
    validateFailure(value.pack.failure);
    if (
        !value.sourceVerified &&
        (value.pack.ok ||
            value.sdk.selected !== null ||
            value.sdk.available.length ||
            value.sdk.failure !== null ||
            value.api.verified ||
            value.api.failure !== null)
    )
        refuse();
    return value;
}
export function readOriginalReport(path: string, operationHead: string) {
    try {
        const stat = lstatSync(path);
        if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 8192)
            refuse();
        return validateOriginalReport(
            JSON.parse(readFileSync(path, "utf8")),
            operationHead,
        );
    } catch {
        return refuse();
    }
}

export async function runOriginalPack(
    input: {
        source: string;
        output: string;
        api: string;
        report: string;
        operationHead: string;
    },
    env = process.env,
) {
    const source = resolve(input.source);
    const report: any = {
        event: "original-pack-evidence",
        source: accepted.source.commit,
        operationHead: input.operationHead,
        node: process.versions.node,
        zlib: process.versions.zlib,
        sourceVerified: false,
        sdk: {
            expected: accepted.buildTools.dotnetSdk,
            selected: null,
            available: [],
            failure: null,
        },
        api: { verified: false, failure: null },
        pack: {
            ok: false,
            failure: {
                stage: "source-identity",
                id: "unclassified",
                frames: [],
                tool: null,
            },
        },
    };
    let original: any;
    let scratch: string | undefined;
    let reportWritable = false;
    try {
        const workspace = dirname(resolve(input.report));
        if (
            !/^[a-f0-9]{40}$/.test(input.operationHead) ||
            resolve(input.output) !==
                join(workspace, "candidate", accepted.packageDirectory) ||
            resolve(input.report) !==
                join(workspace, "original-pack-report.json") ||
            !resolve(input.api).startsWith(join(workspace, "api-fetch") + sep)
        )
            throw new Error("bridge_arguments_refused");
        reportWritable = true;
        if (
            Object.keys(env).some((k) =>
                /TOKEN|PASSWORD|SECRET|CREDENTIAL|BEARER|API_KEY/i.test(k),
            )
        )
            throw new Error("credential_environment_refused");
        const identity = spawnSync("git", ["rev-parse", "HEAD"], {
            cwd: source,
            env,
            timeout: 10000,
            maxBuffer: 65536,
        });
        if (
            identity.error ||
            identity.signal ||
            identity.status !== 0 ||
            identity.stdout.toString().trim() !== accepted.source.commit
        ) {
            report.pack.failure = {
                stage: "source-identity",
                id: "original_source_mismatch",
                frames: [],
                tool: {
                    command: "git",
                    status: integer(identity.status) ? identity.status : null,
                    codes: [],
                    projects: [],
                    hints: Object.entries(hints)
                        .filter(([, pattern]) =>
                            pattern.test(tail(identity.stderr)),
                        )
                        .map(([name]) => name),
                },
            };
            return report;
        }
        original = await import(
            pathToFileURL(join(source, "scripts/logging/common.ts")).href
        );
        if (!isDeepStrictEqual(original.selectedSource(), accepted.source))
            throw new Error("original_source_mismatch");
        report.sourceVerified = true;
        const projects = await import(
            pathToFileURL(join(source, "scripts/logging/pack-projects.ts")).href
        );
        const { pack } = await import(
            pathToFileURL(join(source, "scripts/logging/pack.ts")).href
        );
        scratch = mkdtempSync(join(workspace, "pack-probes-"));
        const sdkStage = join(scratch, "sdk");
        mkdirSync(sdkStage);
        projects.setupStage(sdkStage);
        try {
            const sdk = original
                .run(
                    "dotnet",
                    ["--version"],
                    sdkStage,
                    original.cleanEnvironment(join(scratch, "sdk-home")),
                )
                .toString()
                .trim();
            if (versionPattern.test(sdk)) report.sdk.selected = sdk;
        } catch (error) {
            report.sdk.failure = classifyOriginalError(
                error,
                source,
                original.ToolFailure,
                "toolchain",
            );
        }
        try {
            report.sdk.available = [
                ...new Set(
                    original
                        .run(
                            "dotnet",
                            ["--list-sdks"],
                            sdkStage,
                            original.cleanEnvironment(
                                join(scratch, "sdk-list-home"),
                            ),
                        )
                        .toString()
                        .split(/\r?\n/)
                        .map((v: string) => v.split(" ")[0])
                        .filter((v: string) => versionPattern.test(v)),
                ),
            ]
                .sort()
                .slice(0, 16);
        } catch {
            /* SDK selection failure above owns the diagnosis; never emit raw list output. */
        }
        const apiStage = join(scratch, "api");
        mkdirSync(apiStage);
        try {
            projects.actualApi(resolve(input.api), apiStage);
            report.api.verified = true;
        } catch (error) {
            report.api.failure = classifyOriginalError(
                error,
                source,
                original.ToolFailure,
                "api-baseline",
            );
        }
        try {
            await pack(
                accepted.version,
                resolve(input.output),
                resolve(input.api),
            );
            report.pack = { ok: true, failure: null };
        } catch (error) {
            report.pack.failure = classifyOriginalError(
                error,
                source,
                original.ToolFailure,
            );
        }
    } catch (error) {
        report.pack.failure = classifyOriginalError(
            error,
            source,
            original?.ToolFailure,
            original ? "source-inventory" : "load-original",
        );
    } finally {
        if (!reportWritable) throw new Error("bridge_arguments_refused");
        validateOriginalReport(report, input.operationHead);
        writeFileSync(input.report, JSON.stringify(report) + "\n", {
            flag: "wx",
            mode: 0o600,
        });
        // Parent owns the whole private workspace; auxiliary cleanup cannot erase the captured original exception.
        if (scratch) {
            try {
                rmSync(scratch, { recursive: true, force: true });
            } catch {}
        }
    }
    return report;
}
if (
    process.argv[1] &&
    resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
    try {
        if (process.argv.length !== 7)
            throw new Error("bridge_arguments_refused");
        const [, , source, output, api, report, operationHead] = process.argv;
        const result = await runOriginalPack({
            source,
            output,
            api,
            report,
            operationHead,
        });
        if (!result.pack.ok) process.exitCode = 1;
    } catch {
        console.error("original-pack-boundary-refused");
        process.exitCode = 1;
    }
}
