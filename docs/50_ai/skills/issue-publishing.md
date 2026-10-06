# Issue publishing

Use this procedure only when creating an issue or publishing an issue refinement is authorized. Follow [Issue workflow](../../10_workflow/issue-workflow.md) for normative content and type rules.

## Preflight

- Confirm the exact repository and target issue, if updating.
- Read current title, body, native type, state, and any relationships the task intends to change.
- Enumerate authorized writes. A body update does not authorize changing labels, milestone, assignees, dependencies, parent, project membership, or repository settings.
- For creation or retyping, verify the enabled native type and a client path that can write and read it.
- Keep the draft self-contained and suitable for repository publication.

## Apply

Prefer a creation path that sets the native type in the initial request. Do not treat a title prefix or body field as a native type. If the selected client cannot set or verify it, keep the local draft and report that publication is incomplete rather than inventing a replacement classification or changing repository settings.

Update an existing issue in place. Preserve fields outside the authorized change, including its current type for a body-only correction. Use structured arguments or a body file to preserve actual newlines and literal content.

Apply authorized relationships through their native fields or endpoints. For bulk structural changes, follow the reviewed synchronization plan and its dependency order. Do not retry an uncertain mutation until readback has reconciled its outcome.

## Readback

Verify the actual changed title, body, native type, state, and every authorized relationship or metadata field. A bulk migration verifies its complete changed graph; an ordinary text update verifies the target and changed fields.

Report publication as complete only when the requested writes match the remote state. If a partial write or missing capability prevents completion, record the observed target and remaining operation and finish unaffected local work. Do not replay completed writes or silently substitute another issue.
