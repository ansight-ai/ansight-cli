---
name: ansight-assess-automation-readiness
description: Audit and score an app's Ansight integration strength and automation readiness. Use for a requested evidence-backed readiness score, automation-ID coverage assessment, blocker analysis, comparison, or prioritized remediation plan; do not use for routine app inspection or workspace authoring.
---

# Assess Ansight Automation Readiness

Produce an evidence-backed audit with separate scores for Ansight Integration Strength and Automation Readiness. Treat a rich telemetry integration and a reliably automatable app as related but distinct outcomes.

## Preserve Audit Safety

- Audit read-only evidence by default.
- Do not create tests, tasks, triggers, Trends definitions, identifiers, or app tools unless the user also requests implementation.
- Do not sign in, purchase, delete, submit, or otherwise mutate meaningful app data merely to improve audit coverage.
- Use safe navigation only when the user has authorized live interaction. Stop before destructive or externally visible actions.
- Treat remote write tools as privileged. Report their schemas and guards without invoking them unless invocation is necessary and authorized.
- Do not award runtime points from source code alone. Mark implemented-but-unverified capabilities explicitly.

## Establish Scope

1. Resolve the exact repository when available, app ID, build, platform, physical or virtual device, capture mode (`sdk` or external `device`), and session. An external audit can proceed without source code or an SDK.
2. Select one to three representative critical flows. Prefer a primary user journey, a data-entry or state-changing journey, and a recovery or error journey.
3. Name the screen states sampled in each flow. Avoid claiming whole-app coverage from one screen.
4. Record available evidence modes: `source`, `live`, `replay`, and `repeat_run`.
5. If multiple live sessions match, do not guess. Narrow by explicit app, session, platform, device, or build evidence before proceeding.
6. Score materially different build profiles and capture modes separately. If the user does not specify one, audit the development or test build intended for automation and name production or other profiles as unscored. Do not blend a development integration with a production build that intentionally excludes Ansight.

This skill owns the audit, scoring, and report. Load evidence helpers directly when needed: Ansight Operate Live App for lifecycle or visible interaction, Ansight Remote App Tools for in-process state, Ansight Investigate Session for retained evidence, and an already available platform inspection skill for platform-specific semantics. Do not load the Ansight App Inspection router from this skill. Use the Ansight Workspace Automation skill only when the user separately asks to create or run repository-owned automation.

## Gather Evidence

### Inspect The Repository

When source is available, locate SDK initialization, app identity, development-build guards, tool registration, logging or telemetry bridges, and existing `ansight/tests`, `ansight/tasks`, `ansight/triggers`, and `ansight/trends` definitions. Search for platform-native identifier APIs and shared control wrappers, but use source matches only as leads.

Do not calculate identifier coverage from raw text-search counts. A declaration may not render, a shared component may cover many instances, and a platform identifier may not map to Ansight's live `automationId` field.

### Inspect Ansight Runtime Evidence

Prefer structured Ansight CLI evidence before direct device or simulator access:

1. Discover apps and live or captured sessions.
2. Select the exact target and inspect app/session state.
3. For an SDK session, discover the live app-tool catalog rather than assuming a tool exists. External sessions have no SDK app-tool provider; assess their host UI and evidence capabilities instead.
4. Confirm available logs, lifecycle or navigation events, telemetry, screenshots, visual trees, artifacts, data inspection, and custom app tools.
5. Inspect schemas and the ordered `read`, `write`, or `critical` policy for app tools.
6. Record an evidence locator for every scored claim: file and line, session and timestamp, tool result, screenshot, or automation run.

Start with:

```sh
ansight host status --json
ansight app list --json
ansight session list --connected --json
ansight session show <session-id> --json
ansight app tools <session-id> --policy read --detail summary --include-unavailable --max-results 50 --json
ansight app tools <session-id> --policy write --detail summary --include-unavailable --max-results 50 --json
ansight app tools <session-id> --policy critical --detail summary --include-unavailable --max-results 50 --json
```

Run the app-tool queries only for SDK sessions. In an external session, inspect `customProperties.deviceExecution.capabilities` and its provider/reason metadata; distinguish platform access limits from missing app integration. External user touches and instrumented network requests are unavailable.

These summaries partition direct matches by policy without loading every
argument and result schema; supplemental prerequisite entries can have another
policy. If a response is truncated, refine it with `--feature`, `--category`,
`--id-prefix`, or `--query`. Retrieve
`--tool-id <exact-id> --detail full` only for tools that materially support a
scored claim or blocker, adding `--include-unavailable` when the denial itself
is the evidence.

### Measure UI Addressability

For each sampled screen state, inventory every eligible target:

- include actionable controls, navigation targets, input fields, important status or result regions, and elements needed for assertions;
- exclude decorative elements that neither receive actions nor establish a meaningful postcondition;
- count repeated rows individually when automation must distinguish them;
- require a selector to resolve uniquely within the rendered state, either directly or through a stable contextual ancestor.

Count a target as having a stable unique automation ID only when its observed Ansight `automationId` is:

- present in the live or captured visual tree;
- unique in the state where it is used;
- attached to the element that receives the action or exposes the assertion state;
- semantic and independent of localized display text, list position, timestamps, random values, or mutable user content; and
- expected to survive relaunches and ordinary UI refactors.

Calculate:

```text
automation ID coverage = stable unique ID targets / eligible targets
```

Report both counts and the percentage. Also report duplicate critical IDs, missing critical IDs, and IDs that exist but are volatile or attached at the wrong level. Prefer an ID backed by stable domain identity for repeated content; use a stable ancestor plus another exact selector when a globally unique child ID is inappropriate.

### Verify Actionability And Determinism

When safe interaction is authorized, verify focused queries before broad tree dumps. Confirm that representative controls can be queried and acted on, and that the resulting state can be observed through a stable condition. Do not treat a delivered tap or typed value as success without checking its postcondition.

Look for:

- query, assertion, tap, typing, secret entry, scrolling, back, launch, and wait capabilities;
- explicit loading, empty, success, validation, and error states that can be awaited without fixed sleeps;
- repeatable authentication, test-data seeding, reset, cleanup, and environment selection;
- guarded app tools that expose domain state or deterministic setup more reliably than screen scraping;
- named assertions and bounded capabilities in repository tests or tasks;
- event-driven diagnostic enrichment from triggers;
- reusable timing windows from spans;
- deterministic telemetry budgets from Trends definitions; and
- consistent results across at least two clean repetitions when claiming repeatability.

## Score The Audit

Use the rubric below before assigning points or a readiness label. Score only demonstrated evidence, give partial credit when appropriate, and list unverified implementation separately from earned points.

Create an audit JSON matching the rubric. Keep temporary audit input outside the app repository unless the user requests it as a deliverable. Resolve the scorer relative to this `SKILL.md`, then run:

```bash
python3 <skill-directory>/scripts/score_readiness.py <audit.json>
```

Use `--format json` when structured output is more useful. Pass `-` as the audit path to read JSON from standard input when a no-write environment can stream the input. If neither a temporary file nor standard input is available, calculate from the same rubric manually and disclose that the scorer was not executed. Treat the script's applied gates and calculated totals as authoritative whenever it is run. Do not manually raise a gated outcome.

## Report The Result

Lead with the outcome, evidence confidence, scope, and material blockers. Include:

1. overall score and label;
2. Integration Strength out of 50;
3. Automation Readiness out of 50 after gates;
4. capture mode, device/platform, capability limits, evidence modes, and confidence;
5. sampled flows and screen states;
6. automation-ID numerator, denominator, percentage, duplicates, and critical misses;
7. a criterion table containing score, evidence, and gap;
8. critical blockers and unverified claims;
9. prioritized fixes ordered by impact and effort; and
10. the next three automation scenarios that become viable after the fixes.

Keep the existing rubric and gates across modes. An external session can have strong Automation Readiness with lower Integration Strength; intentional SDK-only gaps are not evidence of failed setup. Recommend SDK integration only when the requested evidence or internal control needs it, and describe that as a choice.

Use these remediation priorities:

- **P0:** unsafe control surface, no observable UI, no actionable UI, unstable app identity, or missing identifiers on critical actions and assertions.
- **P1:** insufficient identifier coverage, duplicate IDs, nondeterministic setup, unobservable loading/error states, or missing postconditions.
- **P2:** incomplete telemetry, artifacts, repository automation assets, cross-platform evidence, or convenience tooling.

Never describe an app as automation-ready when a rubric gate limits it to `Conditional` or `Not ready`. When only source evidence is available, provide the score as a provisional baseline and name the exact live checks required to raise confidence.

## Apply The Rubric

Use the strongest available evidence in this order:

1. successful repeated automation run;
2. focused live Ansight query or action plus verified postcondition;
3. captured session evidence with timestamps;
4. source implementation and build or static validation; and
5. documentation or assertion without verification.

Source evidence can prove that integration code exists, but not that the running app exposes it. Give source-only claims no more than half of the applicable runtime-focused criterion and list the missing live verification. Use partial points, keep every score within its maximum, and attach evidence for every positive score.

### Integration Strength: 50 Points

#### `connection_and_identity`: 10

- **9–10:** The exact app and build are discoverable; identity is stable; live/replay sessions reconnect and carry useful platform, version, device, and environment metadata.
- **6–8:** Connection and identity work, but some metadata, lifecycle behavior, or verification is incomplete.
- **3–5:** SDK integration or an external watch is present but discovery, identity, or session continuity is fragile or source-only.
- **0–2:** No reliable evidence that the CLI host can identify and inspect the intended app.

#### `runtime_observability`: 15

- **13–15:** Logs, errors, lifecycle/navigation or domain events, telemetry, screenshots, and time correlation provide a useful causal timeline.
- **9–12:** Most core evidence is present, with one meaningful blind spot or inconsistent capture.
- **5–8:** Basic logs or screenshots exist, but diagnosis still depends heavily on reproduction or inference.
- **0–4:** Runtime evidence is absent, noisy without context, or not available through Ansight.

#### `inspection_surface`: 15

- **13–15:** Current UI hierarchy plus relevant app-owned state, files, preferences, database data, artifacts, or framework-specific inspection are available with clear schemas.
- **9–12:** UI inspection is strong but deeper state is limited, or useful tools exist with notable schema/coverage gaps.
- **5–8:** A partial tree or a small inspection surface exists, but critical state remains opaque.
- **0–4:** The agent is effectively limited to pixels, generic logs, or direct device access.

#### `guarded_app_control`: 10

- **9–10:** Narrow, structured app tools cover needed setup or domain actions; policies are explicit; mutating/debug tools are development-gated and least-privilege.
- **6–8:** Useful control exists with minor schema, guard, or coverage limitations.
- **3–5:** Control is ad hoc, overly broad, source-only, or difficult to use safely.
- **0–2:** No app-owned control exists where needed, or reachable write tools are unsafe.

### Automation Readiness: 50 Points

#### `ui_addressability`: 20

- **18–20:** At least 90% of eligible critical-flow targets have stable unique automation IDs; no critical duplicates; action and assertion targets are correctly attached.
- **14–17:** At least 80% coverage with no unresolved duplicates on critical targets.
- **10–13:** At least 70% coverage, or good coverage with some volatile, misplaced, or ambiguous IDs.
- **5–9:** At least 40% coverage; automation depends substantially on text, coordinates, hierarchy position, or OCR.
- **0–4:** Less than 40% coverage or no trustworthy visual-tree selector surface.

Do not award a band whose percentage threshold is not met. Use the lower band even if the existing IDs are high quality.

#### `actionability`: 10

- **9–10:** Representative controls can be queried, tapped, typed into, scrolled to, and asserted as applicable; every action has an observable postcondition.
- **6–8:** The main happy path works but one control family, gesture, secret input, or state transition is unreliable.
- **3–5:** Only a narrow subset works or actions depend on coordinates/text and weak postconditions.
- **0–2:** The agent cannot safely operate and verify the sampled critical flow.

#### `deterministic_state`: 10

- **9–10:** Launch state, authentication, data seeding, reset/cleanup, environment selection, loading, and asynchronous completion are controllable and observable.
- **6–8:** Most setup is repeatable with one manual or timing-sensitive dependency.
- **3–5:** State can be prepared but relies on shared data, fixed sleeps, manual intervention, or fragile ordering.
- **0–2:** Runs cannot start from or return to a known state.

#### `repository_automation_assets`: 5

- **5:** Critical flows have focused repository tests or deterministic tasks with bounded capabilities, named assertions, and observable validation; triggers enrich evidence only where appropriate; and relevant trend-sensitive behavior uses inline observation spans and deterministic metric budgets.
- **3–4:** Useful definitions exist but coverage, assertions, or reuse is incomplete.
- **1–2:** Only prototypes, external scripts, or undocumented manual prompts exist.
- **0:** No reusable automation asset exists.

Do not hide a zero when a greenfield app intentionally has no automation assets. Explain that the score reflects expected sequencing when the audit is explicitly pre-automation.

#### `repeatability`: 5

- **5:** At least three clean repetitions pass with stable selectors and state; required platforms/builds have evidence.
- **3–4:** Two clean repetitions pass, or one target platform is verified while another remains untested.
- **1–2:** One successful run exists or repetitions expose intermittent failures.
- **0:** No end-to-end repetition evidence exists.

## Apply Readiness Gates

Apply every gate supported by evidence. Gates cap the Automation Readiness subscore after raw points:

| Blocker code | Meaning | Readiness cap |
| --- | --- | ---: |
| `no_ui_observation` | No trustworthy visual tree or equivalent state observation | 20 |
| `no_ui_action` | Sampled critical flow cannot be safely operated | 24 |
| `id_coverage_below_70` | Stable unique ID coverage is below 70% | 34 |
| `duplicate_critical_ids` | A critical selector is ambiguous in a rendered state | 34 |
| `no_deterministic_reset` | A clean start or reset cannot be reproduced | 39 |
| `no_stable_app_identity` | Automation cannot select the target app/build reliably | 34 |

Add `unsafe_mutating_tools` when app or remote write tools are reachable outside the intended development/test boundary or without appropriate scope, authorization, or runtime guards. Broad all-tool access that is compile-time isolated to a clearly identified developer build does not trigger this blocker by itself; penalize `guarded_app_control` when the surface is broader than the audit flows require and document the residual risk. The blocker changes the headline outcome to `Blocked — unsafe control surface` until remediated without automatically altering the numeric score.

The scorer derives `id_coverage_below_70` and `duplicate_critical_ids` from metrics when applicable. Add all other blocker codes explicitly. If no rendered state can be observed, set both target counts to zero, add `no_ui_observation`, and describe coverage as unmeasured rather than 0%.

## Assign Labels And Confidence

Overall labels use the gated total:

- **90–100:** Strong
- **75–89:** Good
- **60–74:** Developing
- **40–59:** Weak
- **0–39:** Minimal

Automation Readiness labels use the gated subscore:

- **43–50:** Automation-ready
- **35–42:** Pilot-ready
- **25–34:** Conditional
- **0–24:** Not ready

Evidence confidence is:

- **High:** `source`, at least one of `live` or `replay`, and `repeat_run`;
- **Medium:** any two of `source`, runtime evidence (`live` or `replay`), and `repeat_run`; and
- **Low:** anything less.

Keep score and confidence separate. A high provisional score with low confidence remains unverified.

## Create Scorer Input

Pass a JSON object with all nine criteria. Scores may be integers or decimals.

```json
{
  "target": "Example app / iOS development build",
  "evidenceModes": ["source", "live", "repeat_run"],
  "criteria": {
    "connection_and_identity": { "score": 9, "evidence": ["Live session abc identifies build 42"], "gap": "" },
    "runtime_observability": { "score": 12, "evidence": ["Logs and screenshots are time-correlated"], "gap": "Navigation events are absent" },
    "inspection_surface": { "score": 13, "evidence": ["Focused visual-tree queries and preferences inspection work"], "gap": "" },
    "guarded_app_control": { "score": 7, "evidence": ["Read-only state tool is schema-described"], "gap": "No reset tool" },
    "ui_addressability": { "score": 15, "evidence": ["17 of 20 eligible targets have stable unique IDs"], "gap": "Three missing IDs" },
    "actionability": { "score": 8, "evidence": ["Tap, type, scroll, and assertions verified"], "gap": "Secret entry untested" },
    "deterministic_state": { "score": 6, "evidence": ["Test account is reusable"], "gap": "No full reset" },
    "repository_automation_assets": { "score": 3, "evidence": ["One bounded login task exists"], "gap": "No recovery-flow coverage" },
    "repeatability": { "score": 4, "evidence": ["Two clean repetitions pass"], "gap": "Android untested" }
  },
  "metrics": {
    "eligibleTargets": 20,
    "stableUniqueAutomationIds": 17,
    "duplicateCriticalAutomationIds": 0,
    "criticalTargetsMissingIds": 1,
    "representativeFlows": 2,
    "attemptedRepeatedRuns": 2,
    "successfulRepeatedRuns": 2
  },
  "blockers": ["no_deterministic_reset"]
}
```

Keep criterion evidence concise in scorer input. Put detailed evidence locators and remediation notes in the final audit report.
