#!/usr/bin/env python3
"""Verify Pencil through the public CLI, SDK capture, and independent app receipts."""

import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cli", required=True, type=Path)
    parser.add_argument("--data-dir", required=True, type=Path)
    parser.add_argument("--workspace", required=True, type=Path)
    parser.add_argument("--xcode", required=True, type=Path)
    parser.add_argument("--device", required=True)
    parser.add_argument("--surface", required=True, choices=["native", "web"])
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    developer = args.xcode / "Contents/Developer" if args.xcode.suffix == ".app" else args.xcode
    environment = dict(os.environ, DEVELOPER_DIR=str(developer))
    app_id = "ai.ansight.testapps.pencil"

    def run(command, check=True, timeout=100):
        result = subprocess.run([str(value) for value in command], env=environment,
                                capture_output=True, text=True, timeout=timeout)
        if check and result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        return result

    def cli(*arguments, check=True):
        result = run([args.cli, *arguments, "--data-dir", args.data_dir, "--json"], check=check)
        return json.loads(result.stdout)

    def save(name, value):
        (args.output / name).write_text(json.dumps(value, indent=2) + "\n")

    def simctl(*arguments, **options):
        return run(["/usr/bin/xcrun", "simctl", *arguments], **options)

    known = {item["sessionId"] for item in cli("session", "list", "--app-id", app_id)["sessions"]}
    launch_args = ["--web-pencil"] if args.surface == "web" else []
    session_id = None
    simctl("launch", args.device, app_id, *launch_args)
    try:
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            sessions = cli("session", "list", "--connected", "--app-id", app_id)["sessions"]
            fresh = [item for item in sessions if item["sessionId"] not in known]
            if len(fresh) == 1:
                session_id = fresh[0]["sessionId"]
                break
            time.sleep(0.5)
        if session_id is None:
            raise AssertionError("The app did not establish a fresh SDK session")
        print(f"SDK session: {session_id} ({args.surface})", flush=True)
        save("session-start.json", cli("session", "show", session_id))
        time.sleep(2)
        task = cli("task", "run", "pencil.hid-demo", "--app-id", app_id,
                   "--repository", args.workspace, "--session-id", session_id, check=False)
        save("task-result.json", task)
        captured = cli("session", "touches", session_id, "--limit", "1000")
        save("touches.json", captured)
        cli("session", "screenshot", "export", session_id, "--output", args.output / "drawing.png")
        container = Path(simctl("get_app_container", args.device, app_id, "data").stdout.strip())
        name = "web-pencil-observations.json" if args.surface == "web" else "pencil-observations.json"
        receipt_path = container / "Documents" / name
        observations = json.loads(receipt_path.read_text()) if receipt_path.exists() else []
        save("app-observations.json", observations)
        print(json.dumps({"sessionId": session_id, "result": task["result"].get("message"),
                          "assertions": task["result"].get("assertions"),
                          "output": task["result"].get("output")}), flush=True)
        assert task["result"]["status"] == 0, "The SDK Pencil task failed; see task-result.json"
        fixture = json.loads((args.workspace / "ansight/fixtures/pencil-demo.pen.json").read_text())
        expected_contact = [item for item in fixture["samples"] if item["phase"] in ("down", "move", "up")]
        recorded_contact = [item for item in captured["items"] if item["action"] in ("down", "move", "up")]
        assert len(recorded_contact) == len(expected_contact), "The SDK lost contact samples"
        assert all(item["details"].get("inputSource") == "simulator-hid+sdk"
                   for item in recorded_contact), "The SDK lost simulation properties on a contact sample"
        for received, expected in zip(recorded_contact, expected_contact):
            assert received["action"] == expected["phase"]
            assert abs(received["details"]["force"] - expected.get("pressure", 0)) < 0.0001
            for field in ("altitudeRadians", "azimuthRadians", "rollRadians"):
                default = math.pi / 2 if field == "altitudeRadians" else 0
                assert abs(received["details"][field] - expected.get(field, default)) < 0.0001
        contact = [item for item in observations if item.get("phase") in
                   ("pointerdown", "pointermove", "pointerup", "down", "move", "up")]
        assert len(contact) > 40, f"Only {len(contact)} app contact receipts"
        assert sum(item["phase"] in ("down", "pointerdown") for item in contact) == 4
        assert max(item["pressure"] for item in contact) - min(item["pressure"] for item in contact) > 0.7
        moves = [item for item in contact if item["phase"] in ("move", "pointermove")]
        assert min(item["pressure"] for item in moves) >= 0.09, "Moving Pencil samples lost pressure"
        assert max(item["pressure"] for item in moves) > 0.9, "Moving Pencil samples lost the pressure peak"
        if args.surface == "web":
            assert all(item["isTrusted"] and item["pointerType"] == "pen" for item in contact)
            assert any(item["phase"] == "gotpointercapture" for item in observations)
            assert any(item["phase"] == "lostpointercapture" for item in observations)
        else:
            assert all(item["tool"] == "stylus" for item in contact)
        save("app-assertions.json", {"passed": True, "surface": args.surface,
                                    "contactCount": len(contact), "sessionId": session_id})
    finally:
        simctl("terminate", args.device, app_id, check=False)
        if session_id:
            deadline = time.monotonic() + 15
            while time.monotonic() < deadline:
                connected = cli("session", "list", "--connected", "--app-id", app_id)["sessions"]
                if all(item["sessionId"] != session_id for item in connected):
                    break
                time.sleep(0.5)
            else:
                raise AssertionError("The exact SDK recording did not finalize")
            save("session-final.json", cli("session", "show", session_id))
            cli("session", "export", session_id, args.output / "session.ansight.zip")


if __name__ == "__main__":
    main()
