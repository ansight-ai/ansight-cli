# App learning MVP

`ansight app learn` explores an SDK-connected app and builds a small library of
reusable navigation tasks. It reuses App Graph exploration and the replay task
author's evidence checks, TypeScript compiler, and live task runner.

```sh
ansight app register com.example.app --codebase /path/to/app
ansight app learn com.example.app --focus "Search and item details" --max-tasks 3 --json
```

The app must have a connected SDK session. Use `--session-id` when more than one
instance is connected, or `--launch --device-id <id>` to launch a registered app.
Learning needs the same AI gateway configuration as App Graph exploration, plus
the Node.js and TypeScript compiler used by task extraction. These prerequisites
are checked before task authoring; compiler availability is checked before exploration.

## What it learns

The first observed destination is the starting state. Learning finds short routes
from that state through observed UI tap bindings, then ranks them by navigation
steps and how many other routes share their setup. Cycles do not create additional
candidates. Routes are limited to 12 transitions, and parameterized or unsupported
bindings are excluded. This estimates potential reuse; it does not measure actual
user frequency or treat crawler backtracking as demand.

For each selected route, the task author inspects the exploration's recorded
evidence and writes a fixed-input TypeScript task. Each task must declare its
starting and success states, use grounded selectors and bounded waits, and have
named `starting-state` and `destination-reached` assertions. The author is instructed
to keep actions to reversible navigation. Missing evidence becomes a review
diagnostic instead of an invented selector.

By default, a bounded agent first restores the required starting state. The
deterministic draft then runs once on the same session. Only passing tasks with
both named assertions are saved into the registered workspace's `ansight/tasks`
directory. Failed tasks are retained as drafts; learning does not retry their
actions blindly. Existing task files are never replaced. Task IDs are derived from
destination names and the observed action sequence so a later learning pass can
skip tasks already saved by an earlier pass.

```sh
# Explore and author without replaying or adding tasks to the active library.
ansight app learn com.example.app --draft-only --max-actions 40 --max-tasks 2

# Use the same reasoning mode for exploration, task authoring, and preparation.
ansight app learn com.example.app --reasoning balanced --result-file ./learn-report.json
```

The default exploration budget is 64 actions (maximum 128). The default task limit
is 3 (maximum 10). Preparation has separate bounds of 32 model turns and 96 tool
calls per candidate; task authoring and task execution retain their existing
limits. `--headless` applies when launching a simulator. Learning leaves the app
open so its final state can be inspected.

## Results and recovery

The versioned `ansight.app-learn/v1` report contains the exploration graph and audit
ID, candidate ranking reasons, task source and draft paths, static diagnostics,
live verification results, assertions, and saved paths. It is updated after each
candidate and on cancellation after exploration. Reports live under the host's
`app-learn-runs` directory unless `--result-file` selects a new path. Existing
reports are preserved.

`completed` means all selected candidates were saved, or all were statically
validated when `--draft-only` was used. `partial` means at least one candidate
needs review, could not be prepared, or failed verification. `no-candidates` means
no new supported route was found. Partial and failed runs exit nonzero.

Drafts remain outside the registered task directory. Inspect their source and
diagnostics through the paths in the report before editing or adopting them.
Saved tasks are discoverable using the ordinary task commands:

```sh
ansight task list --app-id com.example.app
ansight task run <learned-task-id> --app-id com.example.app --session-id <session-id>
```

This MVP covers fixed-input navigation from one observed starting state. Learning
from historical repetition, generalized parameters, SDK-less capture, mutating
workflows, automatic repair, and `app execute --learn` are not implemented.
