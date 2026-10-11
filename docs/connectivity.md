# Connectivity inspection

Connectivity is a passive SDK observation stream, separate from HTTP requests. Import offline captures normally, including their in-field annotations. Both the local and cloud player use the same Connectivity timeline and tab.

```sh
ansight session connectivity <session-id> summary --json
ansight session connectivity <session-id> at --offset 01:15 --json
ansight session connectivity <session-id> events --from 00:30 --to 02:00 --json
ansight session connectivity <session-id> quality --limit 100 --json
ansight session connectivity <session-id> timeline --annotation <annotation-id> --before 120 --after 30 --json
```

Offsets are measured from capture start; ISO-8601 timestamps also work. `events` excludes quality-only records; `quality` includes records carrying a quality sample; `timeline` includes every kind. `--kind snapshot|change|quality|gap` filters further. Pagination returns `nextCursor`; supply it with the same filters and `--cursor`. A changed live capture invalidates the cursor. The limit is 1–1,000 and each page has a bounded serialized size.

Agent tools: `ansight_get_connectivity_summary`, `ansight_get_connectivity_timeline`, and `ansight_get_connectivity_at`. All work with historical sessions. Inputs use `sessionId`, `from`, `to`, `at`, `annotationId`, `beforeSeconds`, `afterSeconds`, `kind`, `limit`, and `cursor` as applicable.

Repository tasks expose the same session-bound queries as `ansight.connectivity.summary()`, `ansight.connectivity.timeline()`, and `ansight.connectivity.at({ at: "01:15" })`.

Summary reports available, unavailable, and unknown durations, changes, disconnects, reconnects, gaps, sample counts, supported measurements, capture bounds, and clipping. State-at-time returns source observations and quality measurement age. Availability does not establish internet or server reachability. Missing optional flags are unknown.

Archives store `connectivity/index.json` and segmented `ansight.connectivity.v1` JSONL observations. Every observation includes full state; segments retain the preceding observation. Imports deduplicate checkpoints. Extractions retain preceding state; cuts save original timestamps while rebasing playback. Existing captures without connectivity are supported.

Use annotation windows to correlate connectivity with `ansight session network`, logs, screenshots, and the unified session timeline. The latter supports the `connectivity` kind. Quality is an OS estimate, not an active throughput or latency test.

## Testing impaired networks

See [Network conditioning](network-conditioning.md) for the host-side TypeScript
and CLI controls, mitmproxy setup, cleanup guarantees and MAUI harness. Applied
conditions are recorded separately from OS-observed connectivity.
