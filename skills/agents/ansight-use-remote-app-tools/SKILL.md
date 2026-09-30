---
name: ansight-use-remote-app-tools
description: Discover and call bundled or app-specific remote tools exposed by one connected Ansight session. Use for authoritative in-process framework or domain state and authorized remote operations; do not use merely to establish the host/session, implement new tools, or replace a visible UI workflow.
---

# Use Ansight Remote App Tools

Use the live app's authenticated tool catalog as the source of truth. Remote tools expose framework, platform, storage, diagnostic, or app-domain state from inside the connected process; they are not inferred APIs.

This skill requires one exact connected session. Use the Ansight Operate Live App skill when the host, device, app, or session must first be started or selected.

## Prerequisites And Routing

Remote-tool use requires:

- the Ansight SDK installed and initialized in the app's development or QA build;
- the relevant bundled or app-specific tool packages included by that build;
- runtime policy and local guards configured to expose the permitted maximum policy; and
- a live SDK session connected to the resident host.

If the SDK integration is missing, report the blocker. Only when installation is in scope, follow `https://www.ansight.ai/skills/ansight-install.md`. If the CLI or resident host is not ready, report the missing prerequisite. Use setup only within the requested scope: `https://www.ansight.ai/skills/ansight-cli-setup.md`. If the app is installed but not connected, follow the Ansight Operate Live App skill.

An empty or blocked catalog does not by itself prove that installation is missing. It may reflect build gating, omitted tool packages, current app state, pairing limits, or a runtime guard. Report the observed catalog and denial state before proposing an integration change; do not enable remote tools in a protected build merely to complete an inspection.

External physical-phone and simulator/emulator sessions use `captureSource: device` and have no SDK app-tool provider. This is an expected capture limit. If visible UI or host-captured evidence can answer the request, use the live-operation or investigation skill within the selected mode. If the request truly needs app internals, explain the SDK option and obtain a mode choice before changing the app unless SDK integration is already authorized. Host file access in an external session does not imply an app-supplied file or database tool exists.

## Decide When A Remote Tool Is Appropriate

Prefer a remote tool when it can answer an in-process question more authoritatively than screenshots or logs, such as current navigation state, bindings, selected domain objects, database rows, sandbox files, preferences, registered reflection roots, or an app-generated diagnostic artifact.

Keep semantic UI interaction as the default when the requested behavior is a visible user flow. Do not bypass taps, typing, navigation, or observable postconditions with an opaque internal mutation merely because a write tool exists.

## Discover The Current Catalog

Search the catalog immediately before choosing a tool. Start with compact,
executable read-tool metadata using a few intent-bearing terms:

```sh
ansight app tools <session-id> \
  --query "<focused terms>" \
  --policy read \
  --detail summary \
  --json
```

Use `--feature <domain>` for a broad product capability, `--category <exact>`
or `--id-prefix <prefix>` for a known family, and `--max-results <1-50>` to
bound direct matches. Focused searches return executable tools by default. Use
`--include-unavailable` only when the task is to inspect denial or availability
state. If `isTruncated` is true or the matches remain ambiguous, refine the
search instead of fetching the full catalog.

After selecting one exact ID, fetch its current complete definition:

```sh
ansight app tools <session-id> \
  --tool-id <exact-tool-id> \
  --detail full \
  --json
```

This second response is the authority for the selected tool's schemas,
policy, executability, denial, and prerequisite IDs. Do not use an unfiltered
full catalog as the normal discovery step.

Each entry can expose:

- exact tool ID, category, name, description, and keywords;
- `read`, `write`, or `critical` policy;
- whether it is currently executable;
- a `denial` object when app state, the local guard, or the paired-client maximum blocks it;
- argument and result schemas;
- prerequisite tool IDs;
- catalog revision and argument encoding.

Catalog availability is live state. Query it again after reconnecting, changing
app state, or receiving a stale-availability failure. Use `--if-revision
<revision>` only to avoid transferring an unchanged catalog; an unchanged
response is not a new inspection.

Never invent a tool ID, assume a bundled family is installed, or reuse a schema from another app version.

## Choose The Narrowest Tool

1. Start with a compact search for executable read tools.
2. Select one exact ID, fetch its full definition, and match the returned description and schemas to the question.
3. Follow every returned `prerequisiteToolIds` entry and copy exact returned identifiers into the dependent call.
4. Treat tool IDs, node IDs, automation IDs, database IDs, paths, tags, surface IDs, and reflection root IDs as separate namespaces unless a schema explicitly connects them.
5. Use a write tool only when the user requested the corresponding app mutation.
6. Use a critical tool only with explicit authorization for that exact effect and only when the catalog and runtime guard permit it.

Do not attempt to bypass a denial by changing local guards, calling a differently named tool, or using reflection as a substitute.

## Recognize Bundled Families Without Assuming Them

The catalog may contain:

- framework inspection such as `maui.*`, `flutter.*`, `react.*`, or `dom.*`;
- native visual-tree and screenshot tools such as `ui.*`;
- database or structured data tools;
- sandbox file tools;
- preferences or allow-listed secure-storage tools;
- registered-root reflection tools under `reflect.*`;
- artifact query and request tools;
- app-specific domain tools.

Use platform-specific inspection skills when a returned family has platform semantics that affect interpretation. Use a platform's `ansight-create-remote-tool-*` skill only when the user asks to implement a new tool; creation is outside this skill.

## Call One Exact Tool

Prefer an arguments file for nontrivial or nested schemas:

```sh
ansight app call <session-id> <tool-id> --arguments-file <arguments.json> --json
```

For a small object, inline arguments are acceptable:

```sh
ansight app call <session-id> <tool-id> --arguments '<json-object>' --json
```

Validate the object against the returned argument schema. Do not add speculative fields, translate identifiers across namespaces, or reinterpret omitted required values.

For a state-changing call or visual diagnostic action, request post-call evidence when it is useful:

```sh
ansight app call <session-id> <tool-id> \
  --arguments-file <arguments.json> \
  --after-tree \
  --after-screenshot \
  --after-delay-ms <0-2000> \
  --json
```

Post-call evidence supplements the tool result. It does not establish success unless it proves the requested postcondition.

## Batch Only Already-Known Calls

Use `app batch` for up to 32 ordered calls only when their exact IDs, schemas, arguments, and dependencies are already known and no intermediate reasoning is required:

```sh
ansight app batch <session-id> --calls-file <calls.json> --json
```

Each item can contain `toolId`, `arguments`, `after`, and `callId`. Keep the default stop-on-failure behavior when later calls depend on earlier ones. Use `--continue-on-error` only for independent evidence reads whose remaining results are still useful after one failure.

Do not batch exploratory mutations, critical operations, or calls that require inspecting an intermediate result before choosing the next arguments.

## Mutation And Verification

For an authorized write or critical operation:

1. Capture a read-only baseline from the same authoritative surface.
2. State the exact target and expected effect.
3. Execute the smallest call once.
4. Re-run the baseline read or another independent verification.
5. Verify the visible UI as well when the effect is user-facing.
6. Report rollback or cleanup when the operation created temporary state.

For files, preferences, secure storage, databases, and reflection, remain inside the roots, keys, schemas, and members explicitly published by the app. Do not expose secrets or broaden access to complete the task.

## Handle Failure As Evidence

Report errors and partial effects. Allow one evidence-supported recovery per
requested operation, shared with live operation, only when it cannot duplicate a
mutation. If execution is uncertain, read state once and stop if still unresolved.
Diagnose or repair only when requested.

- A denial means the tool is unavailable under the current app state or guard. Report its code and reason.
- A schema error means the call was malformed; correct it only from the returned schema.
- A prerequisite failure means the dependent call must not proceed.
- A disconnect is a blocker. Rediscover the session and catalog only for a safe retry within that allowance.
- A successful tool response proves only the operation described by its result schema; verify broader product outcomes separately.

## Report The Tool Trail

Report the selected session, catalog revision, exact tool IDs and policies, prerequisites followed, arguments at a safe summary level, material result fields, mutations performed, before-and-after evidence, verification outcome, denials, and any fallback outside the remote-tool surface.
