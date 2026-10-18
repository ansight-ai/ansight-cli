# Session activity

Ansight retains a metadata journal of host operations independently of detailed test traces. It records an attempt before dispatch and a completion afterwards, including failures and cancellation. Disabling detailed tracing with `--no-trace` does not disable activity recording.

```sh
ansight session activity <session-id> --json
ansight session activity <session-id> --limit 100 --cursor <cursor> --json
ansight session activity <session-id> --jsonl
```

In the local player, use the **Trace** icon in the session header, then select **Operations**. The same viewer opens for sessions with or without a linked test run. Filter operations by caller, outcome or category, and expand an operation to inspect identity attribution, request/trace/run/parent IDs, evidence references and completion time. **Open trace** switches to its detailed execution trace in the same viewer; sessions with a linked test run open that trace initially. **Show evidence** returns to the replay at the retained screenshot or visual tree. Operations use wall-clock times, so edits performed after a recording ends remain visible without extending its replay timeline.

## Caller attribution

CLI does not imply human. External clients can declare their identity with:

```sh
ANSIGHT_CALLER_KIND=agent ANSIGHT_CALLER_NAME=codex ANSIGHT_WORK_ID=chat-123 \
  ansight session show <session-id> --json
```

`ANSIGHT_CALLER_KIND` accepts `human`, `agent`, or `automation`; the default is `unknown`. Names and work IDs are optional, bounded metadata; do not put secrets in these labels. The context travels through resident-host forwarding and interactive connections. MCP calls use the MCP ingress, while browser requests use the player ingress. These identify the entry point, not proof that a human initiated a request.

The caller record separates:

- `principalId` and `principalAttribution`: an identity established by successful account authorization, when one exists. Local commands remain account-free and do not acquire the resident host's account identity.
- `kind`, `name`, and `executorAttribution`: the executor, with `caller-declared`, `host-generated`, or `unknown` attribution. Built-in agents and repository tasks set their own executor and retain the original initiator, client and principal.
- `clientName`, `clientVersion`, `ingress`, and `workId`: client and external work correlation.

Queued SDK-triggered automations identify the host automation executor but leave the original initiator and principal unknown; they do not inherit the identity of whoever started the background host.

Control-pipe metadata cannot assert authentication or host-generated executor attribution. Imported archives label both identities as `imported` and namespace operation IDs so an imported completion cannot complete a local attempt.

## Format and persistence

The source of truth is UTF-8 JSONL under `<application-data>/session-activity/<sha256(session-id)>/`. Operations without a resolved session start in the `host` partition; once a session is resolved it receives a self-contained target event and completion. Multi-session operations complete in every affected partition. The journal sits outside session capture directories and survives session deletion, trimming and compaction.

Each v1 record has a unique event ID, operation/parent/trace IDs, a runtime-generation ID, phase, timestamps, operation/category, caller, optional session/app/request/run/batch IDs, outcome and evidence references. No command arguments, prompts, tool responses, input text, exception messages or credentials are copied into the journal. Existing detailed traces keep their own retention and redaction rules; run audits include activity IDs and individual tool requests share correlation IDs.

Writers acquire an exclusive per-partition file handle across processes, append a line, and flush it to disk. Segments rotate at 4 MiB, retaining eight segments (approximately 32 MiB per session, plus the host partition). Deleting a recording does not delete this history; disk usage across many sessions therefore grows with the number of retained journals. There is no SQL index or external service.

Readers tolerate malformed records and a partially written final line, and report them. A subsequent writer separates an incomplete tail before appending. A start without completion is **incomplete**, not success; the operation may still be running or have been interrupted. `deliveryOutcome` and `evidenceStatus` distinguish input delivery from evidence capture, so a cancelled capture need not imply the input was never delivered.

Persistence failure emits a warning on stderr and remains visible in `persistenceError` for that runtime. It does not retry an action or turn a successful action into a failure. This is local operational history, not a tamper-proof compliance log: the OS user can edit its files, retention is bounded, and a full/unwritable disk can leave gaps.

## HTTP and archives

The local replay server exposes:

```text
GET api/sessions/{session-id}/activity?limit=200&cursor=segment:byte-offset
GET api/sessions/{session-id}/activity?format=jsonl&limit=500
```

The JSON envelope contains `records`, `nextCursor`, `retentionLimited`, `cursorExpired`, `skippedRecords`, `incompleteTail`, `importedHistoryIncomplete`, and `persistenceError`. Limits are 1–500. Cursors are stable append offsets; reuse the last cursor to fetch later events, including after an empty page. An expired cursor restarts at the oldest retained segment and explicitly signals the gap. JSONL responses return `X-Ansight-Next-Cursor` and `X-Ansight-Activity-Incomplete`; the CLI's `--jsonl` emits one bounded page, with warnings on stderr. Reading activity does not record another activity event.

Normal session ZIP exports include `session-data/activity.jsonl` and an export coverage marker. Sanitized exports omit activity because caller/work identifiers can identify people or private work. Imports validate the journal before importing the recording, preserve coverage gaps, and never claim that imported identities were authenticated by the current host. An export is a snapshot: the export operation itself can appear as an unfinished attempt because completion happens after the archive is written.

## Coverage and extension points

The recorder covers dispatched host tools, built-in agent calls (including gateway rejections), repository tasks and nested tasks, repository-triggered automations, direct app-tool APIs, interactive app commands, session inspection commands, session editing and ZIP exports. Passive replay polling, SDK telemetry and raw WebSocket traffic keep their existing evidence formats. Cloud services and third-party extensions executing their own operations need to establish their own trusted context and integrate at their execution boundary; existing historical traces are not retroactively presented as an activity journal.

Add new operations at their shared execution boundary using `SessionActivityService.RecordAsync`, `Record`, or a scoped `Begin` with an explicit outcome. Preserve ownership: a gateway and dispatcher representing the same request share one operation, while nested task actions have child operations. Record only identifiers and outcomes here; put redacted payloads in the existing trace format.
