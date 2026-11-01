// Issue123: fixed original resources. Organization listing is NOT an existence oracle.
import { readFileSync } from "node:fs";
import { isDeepStrictEqual } from "node:util";
import { createHash } from "node:crypto";
const lock = JSON.parse(
    readFileSync(new URL("./accepted-candidate.json", import.meta.url), "utf8"),
);
const accepted = lock.manifest;
const api = "https://api.github.com";
const prefix = "/orgs/SolusQuest/packages/nuget/";
const repo = { id: 1406091678, full_name: "SolusQuest/solus-agent" };
const fail = (code: string): never => {
    throw new Error(`exact-publication:${code}`);
};
const integer = (v: unknown): v is number =>
    Number.isSafeInteger(v) && Number(v) > 0;
function fixed(p: any) {
    const original = accepted.packages.find((v: any) => v.id === p?.id);
    if (!original || !isDeepStrictEqual(original, p))
        fail("nuget-fixed-resource");
    return original;
}
export type NugetIdentity = {
    id: string;
    version: string;
    packageId: number;
    versionId: number;
};
async function request(path: string, token: string, fetcher: typeof fetch) {
    let response: Response;
    try {
        response = await fetcher(api + path, {
            method: "GET",
            redirect: "error",
            signal: AbortSignal.timeout(30000),
            headers: {
                Authorization: `Bearer ${token}`,
                Accept: "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        });
    } catch {
        return fail("nuget-resource-unavailable");
    }
    if (response.status !== 200) {
        await response.body?.cancel().catch(() => {});
        return { status: response.status, value: null };
    }
    try {
        const reader = response.body?.getReader();
        if (!reader) fail("nuget-resource-shape");
        const chunks: Uint8Array[] = [];
        let size = 0;
        while (true) {
            const part = await reader.read();
            if (part.done) break;
            size += part.value.length;
            if (size > 1024 * 1024) {
                await reader.cancel();
                fail("nuget-resource-shape");
            }
            chunks.push(part.value);
        }
        return {
            status: 200,
            value: JSON.parse(Buffer.concat(chunks).toString("utf8")),
        };
    } catch {
        return fail("nuget-resource-shape");
    }
}
// Every unavailable/missing view throws. There is deliberately NO false/absent return.
export async function readNugetIdentity(
    p: any,
    token: string,
    fetcher: typeof fetch = fetch,
): Promise<NugetIdentity> {
    p = fixed(p);
    if (!token) fail("existing-workflow-credential");
    const path = prefix + encodeURIComponent(p.id);
    const result = await request(path, token, fetcher);
    const v = result.value;
    if (result.status !== 200) fail("nuget-resource-unavailable");
    if (
        !v ||
        v.name !== p.id ||
        v.package_type !== "nuget" ||
        v.visibility !== "private" ||
        !integer(v.id) ||
        v.repository?.id !== repo.id ||
        v.repository?.full_name !== repo.full_name
    )
        fail("nuget-resource-identity");
    if (p.id === "Solus.Telemetry.Contracts" && v.id !== 16460732)
        fail("nuget-resource-identity");
    const versions: any[] = [];
    let complete = false;
    for (let page = 1; page <= 10; page++) {
        const r = await request(
            `${path}/versions?per_page=100&page=${page}`,
            token,
            fetcher,
        );
        if (r.status !== 200) fail("nuget-resource-unavailable");
        if (!Array.isArray(r.value) || r.value.length > 100)
            fail("nuget-resource-shape");
        versions.push(...r.value);
        if (r.value.length < 100) {
            complete = true;
            break;
        }
    }
    if (!complete) fail("nuget-resource-pagination");
    const matches = versions.filter((x) => x?.name === p.version);
    if (matches.length !== 1 || !integer(matches[0].id))
        fail("nuget-version-unavailable");
    if (p.id === "Solus.Telemetry.Contracts" && matches[0].id !== 1481941083)
        fail("nuget-resource-identity");
    const direct = await request(
        `${path}/versions/${matches[0].id}`,
        token,
        fetcher,
    );
    if (direct.status !== 200) fail("nuget-resource-unavailable");
    if (direct.value?.id !== matches[0].id || direct.value?.name !== p.version)
        fail("nuget-resource-identity");
    return {
        id: p.id,
        version: p.version,
        packageId: v.id,
        versionId: matches[0].id,
    };
}

type Context = {
    mode: string | undefined;
    member: string | undefined;
    operationHead: string | undefined;
    operatorActorId: string | undefined;
    workflowRef: string | undefined;
};
export type CreationAdmission = {
    kind: string;
    operationHead: string;
    source: string;
    candidate: string;
    member: string;
    version: string;
    privateFeed: string;
    beforeWorkflowRunNumber: number;
    expectedWorkflowRunNumber: number;
    exclusiveWindow: boolean;
    operatorAbsence: {
        actorId: number;
        profile: string;
        status: number;
        complete: boolean;
        matches: number;
        snapshotSha256: string;
    };
};
function keys(v: any, expected: string[]) {
    if (
        !v ||
        typeof v !== "object" ||
        Array.isArray(v) ||
        !isDeepStrictEqual(Object.keys(v).sort(), expected.sort())
    )
        fail("nuget-creation-admission");
}
// This is a trusted operator attestation, NOT a provider proof or a claim source404 means absence.
// Root must actually verify its independent authenticated snapshot/exclusive window before dispatch.
export function creationAdmission(
    text: string | undefined,
    context: Context,
    actualRunNumber: string | undefined,
) {
    if (!text) return undefined;
    if (text.length > 4096) fail("nuget-creation-admission");
    let v: CreationAdmission;
    try {
        v = JSON.parse(text);
    } catch {
        return fail("nuget-creation-admission");
    }
    keys(v, [
        "kind",
        "operationHead",
        "source",
        "candidate",
        "member",
        "version",
        "privateFeed",
        "beforeWorkflowRunNumber",
        "expectedWorkflowRunNumber",
        "exclusiveWindow",
        "operatorAbsence",
    ]);
    keys(v.operatorAbsence, [
        "actorId",
        "profile",
        "status",
        "complete",
        "matches",
        "snapshotSha256",
    ]);
    if (
        context.mode !== "publish-one" ||
        context.operatorActorId !== "16307884" ||
        context.workflowRef !==
            "SolusQuest/solus-agent/.github/workflows/logging-release.yml@refs/heads/codex/l1-123-logging-publication" ||
        !/^[a-f0-9]{40}$/.test(context.operationHead ?? "") ||
        v.operationHead !== context.operationHead ||
        v.member !== context.member ||
        ![
            "SolusAgent.Telemetry",
            "Solus.Logging",
            "Solus.Logging.Supabase",
        ].includes(v.member) ||
        v.kind !== "operator-confirmed-new-package" ||
        v.source !== accepted.source.commit ||
        v.candidate !== accepted.candidateSha256 ||
        v.version !== accepted.version ||
        v.privateFeed !== accepted.agentApi.registry ||
        v.exclusiveWindow !== true ||
        !integer(v.beforeWorkflowRunNumber) ||
        !integer(v.expectedWorkflowRunNumber) ||
        v.expectedWorkflowRunNumber !== v.beforeWorkflowRunNumber + 1 ||
        String(v.expectedWorkflowRunNumber) !== actualRunNumber ||
        v.operatorAbsence.profile !== "configured-operator-package-read" ||
        v.operatorAbsence.actorId !== 16307884 ||
        v.operatorAbsence.status !== 200 ||
        v.operatorAbsence.complete !== true ||
        v.operatorAbsence.matches !== 0 ||
        !/^[a-f0-9]{64}$/.test(v.operatorAbsence.snapshotSha256)
    )
        fail("nuget-creation-admission");
    return {
        receipt: v,
        sha256: createHash("sha256").update(text).digest("hex"),
    };
}
// A404 here is only a noncontradictory SOURCE view under independent operator authority.
// It never creates authority. Missing/403/unknown in readNugetIdentity remain fatal.
async function requireNoReadableConflict(
    p: any,
    token: string,
    fetcher: typeof fetch,
) {
    const path = prefix + encodeURIComponent(p.id);
    const packageView = await request(path, token, fetcher);
    if (packageView.status !== 404) fail("nuget-creation-precondition");
    const versionView = await request(
        `${path}/versions?per_page=100&page=1`,
        token,
        fetcher,
    );
    if (versionView.status !== 404) fail("nuget-creation-precondition");
}
export async function requireOriginalNugetResources(
    packages: any[],
    token: string,
    verify: (p: any, index: number) => Promise<unknown>,
    fetcher: typeof fetch = fetch,
) {
    const identities: NugetIdentity[] = [];
    for (let i = 0; i < packages.length; i++) {
        const p = fixed(packages[i]);
        const identity = await readNugetIdentity(p, token, fetcher);
        await verify(p, i); // Whole original named-feed archive must pass too, before admission.
        identities.push(identity);
    }
    return identities;
}
export async function createNugetResource(
    p: any,
    rawAdmission: string,
    context: Context,
    actualRunNumber: string | undefined,
    token: string,
    push: () => Promise<unknown>,
    verify: () => Promise<unknown>,
    fetcher: typeof fetch = fetch,
) {
    p = fixed(p);
    const admission = creationAdmission(rawAdmission, context, actualRunNumber);
    if (!admission || admission.receipt.member !== p.id || !token)
        fail("nuget-creation-admission");
    await requireNoReadableConflict(p, token, fetcher);
    await push(); // Exactly once, only this explicit creation authority; no existing-member route here.
    const identity = await readNugetIdentity(p, token, fetcher);
    await verify();
    return { identity, creationAdmissionSha256: admission.sha256 };
}
