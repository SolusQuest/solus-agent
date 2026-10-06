# Source of truth

| Surface | Owns |
| --- | --- |
| `README.md` and current source | Behavior and artifacts available in the current tree. |
| Project and architecture docs | Long-lived rules, selected boundaries, and design requirements. |
| Issues | Focused work, unresolved decisions, acceptance criteria, and dependencies. |
| Pull requests | Implementation review and validation evidence for their actual revision. |
| Future releases | Consumer-facing artifacts and the commitments explicitly made for them. |
| Project boards | Planning views; never the sole execution contract. |

## Current behavior and selected design

The initial source contains project skeletons only. Architecture documents describe selected boundaries and requirements for subsequent implementation. Keep these two states distinct: an agreed API responsibility, a project reference, or a successful build is not proof that the API or runtime behavior exists.

When implementation changes, update the actual affected specifications and current-state documents in the same coherent change. Historical evidence remains bound to the revision and conditions that produced it.

## Durable decisions

Chats, task prompts, local scratch files, agent memory, and expiring logs can inform work, but they are not durable repository authority. Record accepted decisions in the owning repository document, issue, or pull request without copying raw conversations.

SolusAgent owns the adapted rules in this repository. A linked downstream document provides context; it does not silently amend SolusAgent's contracts or authorize work in another repository.

## Evidence and links

Living planning references can link a current repository path. An immutable historical claim should link the exact full commit SHA after verifying that the commit belongs to the applicable maintained history. Live tracker links remain mutable references.

For uncommitted local work, identify the working tree and actual commands. Do not invent a commit identity or imply that local evidence came from CI or a published revision.

Use [Pre-release engineering](pre-release-engineering.md) for proportional process and [Contract lifecycle](contract-lifecycle.md) for real compatibility boundaries.
