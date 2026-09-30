# Trends

A trends definition says when to observe the app, which telemetry to
calculate, what budget is acceptable now, and whether historical regression
should be monitored. Results are deterministic and do not depend on model
judgement.

Read the full [Ansight workspace Trends guide](https://www.ansight.ai/docs/workspace/trends).

Create JSON files in this folder:

```json
{
  "schemaVersion": 1,
  "id": "login-responsiveness",
  "appId": "com.example.app",
  "span": {
    "start": { "event": { "label": "auth.login.started" } },
    "end": { "event": { "label": "auth.login.completed" } },
    "selection": "lastCompleted",
    "maximumDurationMs": 30000
  },
  "required": true,
  "missingDataOutcome": "fail",
  "metrics": [
    {
      "id": "fps-p10",
      "channel": { "type": "fps" },
      "statistic": "p10",
      "budget": { "gte": 55 },
      "minimumSamples": 10,
      "maximumSampleGapMs": 1000,
      "regression": {
        "percent": 10,
        "confirmRuns": 2
      },
      "display": {
        "title": "Login frame rate",
        "unit": "fps",
        "fractionDigits": 0,
        "minimum": 0,
        "includeZero": true
      }
    }
  ]
}
```

The app must emit stable start and end labels through the Ansight SDK. Matching
non-empty event details create separate span groups. `selection` chooses which
completed occurrence to evaluate, while `maximumDurationMs` prevents an old start
from pairing with an unrelated end.

`minimumSamples` and `maximumSampleGapMs` prevent sparse evidence from appearing
reliable. The metric budget describes the current run. Optional `regression`
compares the same metric with comparable historical runs; it can set `percent`,
`absolute`, confirmation counts, baseline counts, grouping dimensions, and
whether a confirmed regression is blocking.

Registered apps are evaluated automatically when a matching session finishes.

```sh
ansight trends history --app-id com.example.app
```
