#!/usr/bin/env python3
"""Validate and score an Ansight automation-readiness audit JSON file."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any


CRITERIA = {
    "connection_and_identity": ("integration", 10),
    "runtime_observability": ("integration", 15),
    "inspection_surface": ("integration", 15),
    "guarded_app_control": ("integration", 10),
    "ui_addressability": ("readiness", 20),
    "actionability": ("readiness", 10),
    "deterministic_state": ("readiness", 10),
    "repository_automation_assets": ("readiness", 5),
    "repeatability": ("readiness", 5),
}

READINESS_CAPS = {
    "no_ui_observation": 20,
    "no_ui_action": 24,
    "id_coverage_below_70": 34,
    "duplicate_critical_ids": 34,
    "no_deterministic_reset": 39,
    "no_stable_app_identity": 34,
}

KNOWN_BLOCKERS = set(READINESS_CAPS) | {"unsafe_mutating_tools"}
KNOWN_EVIDENCE_MODES = {"source", "live", "replay", "repeat_run"}


class AuditError(ValueError):
    pass


def require_number(value: Any, field: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise AuditError(f"{field} must be a number")
    return float(value)


def require_non_negative_integer(value: Any, field: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise AuditError(f"{field} must be a non-negative integer")
    return value


def normalize_score(value: float) -> int | float:
    return int(value) if value.is_integer() else round(value, 2)


def overall_label(score: float) -> str:
    if score >= 90:
        return "Strong"
    if score >= 75:
        return "Good"
    if score >= 60:
        return "Developing"
    if score >= 40:
        return "Weak"
    return "Minimal"


def readiness_label(score: float) -> str:
    if score >= 43:
        return "Automation-ready"
    if score >= 35:
        return "Pilot-ready"
    if score >= 25:
        return "Conditional"
    return "Not ready"


def evidence_confidence(modes: set[str]) -> str:
    source = "source" in modes
    runtime = bool(modes & {"live", "replay"})
    repeated = "repeat_run" in modes
    dimensions = sum((source, runtime, repeated))
    if source and runtime and repeated:
        return "High"
    if dimensions >= 2:
        return "Medium"
    return "Low"


def score_audit(audit: dict[str, Any]) -> dict[str, Any]:
    target = audit.get("target")
    if not isinstance(target, str) or not target.strip():
        raise AuditError("target must be a non-empty string")

    raw_modes = audit.get("evidenceModes")
    if not isinstance(raw_modes, list) or not all(isinstance(item, str) for item in raw_modes):
        raise AuditError("evidenceModes must be an array of strings")
    modes = set(raw_modes)
    unknown_modes = modes - KNOWN_EVIDENCE_MODES
    if unknown_modes:
        raise AuditError(f"unknown evidence modes: {', '.join(sorted(unknown_modes))}")

    criteria = audit.get("criteria")
    if not isinstance(criteria, dict):
        raise AuditError("criteria must be an object")
    missing = set(CRITERIA) - set(criteria)
    extra = set(criteria) - set(CRITERIA)
    if missing:
        raise AuditError(f"missing criteria: {', '.join(sorted(missing))}")
    if extra:
        raise AuditError(f"unknown criteria: {', '.join(sorted(extra))}")

    integration = 0.0
    raw_readiness = 0.0
    normalized_criteria: dict[str, Any] = {}
    for key, (dimension, maximum) in CRITERIA.items():
        item = criteria[key]
        if not isinstance(item, dict):
            raise AuditError(f"criteria.{key} must be an object")
        score = require_number(item.get("score"), f"criteria.{key}.score")
        if score < 0 or score > maximum:
            raise AuditError(f"criteria.{key}.score must be between 0 and {maximum}")
        evidence = item.get("evidence", [])
        if not isinstance(evidence, list) or not all(
            isinstance(entry, str) and entry.strip() for entry in evidence
        ):
            raise AuditError(f"criteria.{key}.evidence must be an array of non-empty strings")
        if score > 0 and not evidence:
            raise AuditError(f"criteria.{key}.evidence must contain evidence for a positive score")
        gap = item.get("gap", "")
        if not isinstance(gap, str):
            raise AuditError(f"criteria.{key}.gap must be a string")
        if dimension == "integration":
            integration += score
        else:
            raw_readiness += score
        normalized_criteria[key] = {
            "score": normalize_score(score),
            "maximum": maximum,
            "evidence": evidence,
            "gap": gap,
        }

    raw_metrics = audit.get("metrics", {})
    if not isinstance(raw_metrics, dict):
        raise AuditError("metrics must be an object")
    metric_names = (
        "eligibleTargets",
        "stableUniqueAutomationIds",
        "duplicateCriticalAutomationIds",
        "criticalTargetsMissingIds",
        "representativeFlows",
        "attemptedRepeatedRuns",
        "successfulRepeatedRuns",
    )
    metrics = {
        name: require_non_negative_integer(raw_metrics.get(name, 0), f"metrics.{name}")
        for name in metric_names
    }
    if metrics["stableUniqueAutomationIds"] > metrics["eligibleTargets"]:
        raise AuditError("stableUniqueAutomationIds cannot exceed eligibleTargets")
    if metrics["successfulRepeatedRuns"] > metrics["attemptedRepeatedRuns"]:
        raise AuditError("successfulRepeatedRuns cannot exceed attemptedRepeatedRuns")

    raw_blockers = audit.get("blockers", [])
    if not isinstance(raw_blockers, list) or not all(isinstance(item, str) for item in raw_blockers):
        raise AuditError("blockers must be an array of strings")
    blockers = set(raw_blockers)
    unknown_blockers = blockers - KNOWN_BLOCKERS
    if unknown_blockers:
        raise AuditError(f"unknown blockers: {', '.join(sorted(unknown_blockers))}")

    eligible = metrics["eligibleTargets"]
    stable = metrics["stableUniqueAutomationIds"]
    coverage = None if eligible == 0 else stable / eligible
    if coverage is not None:
        if coverage < 0.40:
            addressability_maximum = 4
        elif coverage < 0.70:
            addressability_maximum = 9
        elif coverage < 0.80:
            addressability_maximum = 13
        elif coverage < 0.90:
            addressability_maximum = 17
        else:
            addressability_maximum = 20
        if metrics["duplicateCriticalAutomationIds"] > 0:
            addressability_maximum = min(addressability_maximum, 13)
        addressability_score = require_number(
            criteria["ui_addressability"]["score"],
            "criteria.ui_addressability.score",
        )
        if addressability_score > addressability_maximum:
            raise AuditError(
                "criteria.ui_addressability.score exceeds the rubric band allowed by "
                f"automation ID metrics (maximum {addressability_maximum})"
            )
    derived_blockers: list[str] = []
    if coverage is not None and coverage < 0.70 and "id_coverage_below_70" not in blockers:
        blockers.add("id_coverage_below_70")
        derived_blockers.append("id_coverage_below_70")
    if metrics["duplicateCriticalAutomationIds"] > 0 and "duplicate_critical_ids" not in blockers:
        blockers.add("duplicate_critical_ids")
        derived_blockers.append("duplicate_critical_ids")

    applicable_caps = [READINESS_CAPS[code] for code in blockers if code in READINESS_CAPS]
    readiness_cap = min(applicable_caps) if applicable_caps else 50
    gated_readiness = min(raw_readiness, readiness_cap)
    total = integration + gated_readiness
    unsafe = "unsafe_mutating_tools" in blockers

    return {
        "target": target.strip(),
        "outcome": "Blocked — unsafe control surface" if unsafe else overall_label(total),
        "overallScore": normalize_score(total),
        "overallMaximum": 100,
        "integrationStrength": normalize_score(integration),
        "integrationMaximum": 50,
        "rawAutomationReadiness": normalize_score(raw_readiness),
        "automationReadiness": normalize_score(gated_readiness),
        "automationReadinessMaximum": 50,
        "automationReadinessLabel": readiness_label(gated_readiness),
        "readinessCap": readiness_cap,
        "evidenceConfidence": evidence_confidence(modes),
        "evidenceModes": sorted(modes),
        "automationIdCoverage": (
            None
            if coverage is None
            else {
                "stableUnique": stable,
                "eligible": eligible,
                "ratio": round(coverage, 4),
                "percentage": round(coverage * 100, 1),
            }
        ),
        "metrics": metrics,
        "blockers": sorted(blockers),
        "derivedBlockers": sorted(derived_blockers),
        "criteria": normalized_criteria,
    }


def render_markdown(result: dict[str, Any]) -> str:
    coverage = result["automationIdCoverage"]
    coverage_text = (
        "unmeasured"
        if coverage is None
        else f'{coverage["stableUnique"]}/{coverage["eligible"]} ({coverage["percentage"]}%)'
    )
    blockers = ", ".join(f"`{item}`" for item in result["blockers"]) or "None"
    lines = [
        f'# {result["target"]}',
        "",
        f'- Outcome: **{result["outcome"]}**',
        f'- Overall: **{result["overallScore"]}/{result["overallMaximum"]}**',
        f'- Integration Strength: **{result["integrationStrength"]}/{result["integrationMaximum"]}**',
        (
            f'- Automation Readiness: **{result["automationReadiness"]}/'
            f'{result["automationReadinessMaximum"]} — {result["automationReadinessLabel"]}**'
        ),
        f'- Evidence confidence: **{result["evidenceConfidence"]}**',
        f'- Automation ID coverage: **{coverage_text}**',
        f'- Blockers: {blockers}',
        "",
        "| Criterion | Score | Gap |",
        "| --- | ---: | --- |",
    ]
    for key, item in result["criteria"].items():
        gap = item["gap"].replace("|", "\\|") or "—"
        lines.append(f'| `{key}` | {item["score"]}/{item["maximum"]} | {gap} |')
    if result["rawAutomationReadiness"] != result["automationReadiness"]:
        lines.extend(
            [
                "",
                (
                    f'Raw readiness was {result["rawAutomationReadiness"]}/50 and was capped at '
                    f'{result["readinessCap"]}/50 by the recorded blockers.'
                ),
            ]
        )
    return "\n".join(lines)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("audit", help="Path to the audit JSON file, or - to read standard input")
    parser.add_argument("--format", choices=("markdown", "json"), default="markdown")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        if args.audit == "-":
            audit = json.load(sys.stdin)
        else:
            with Path(args.audit).open("r", encoding="utf-8") as handle:
                audit = json.load(handle)
        if not isinstance(audit, dict):
            raise AuditError("audit root must be a JSON object")
        result = score_audit(audit)
    except (OSError, json.JSONDecodeError, AuditError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2

    if args.format == "json":
        print(json.dumps(result, indent=2, ensure_ascii=False))
    else:
        print(render_markdown(result))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
