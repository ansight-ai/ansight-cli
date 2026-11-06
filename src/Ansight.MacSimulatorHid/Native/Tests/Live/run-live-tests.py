#!/usr/bin/env python3
"""Verify observed HID input on an isolated simulator using an explicit Xcode."""

import argparse
import ctypes
import json
import os
import platform
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile
import time


def verify_preview(library, developer, device):
    bridge = ctypes.CDLL(str(library.resolve()))
    bridge.AnsightSimulatorPreviewViewCreate.argtypes = [
        ctypes.c_char_p, ctypes.c_char_p, ctypes.c_char_p, ctypes.c_size_t
    ]
    bridge.AnsightSimulatorPreviewViewCreate.restype = ctypes.c_void_p
    bridge.AnsightSimulatorPreviewViewGetSurfaceSize.argtypes = [
        ctypes.c_void_p, ctypes.POINTER(ctypes.c_int32), ctypes.POINTER(ctypes.c_int32)
    ]
    bridge.AnsightSimulatorPreviewViewGetSurfaceSize.restype = ctypes.c_bool
    bridge.AnsightSimulatorPreviewViewDestroy.argtypes = [ctypes.c_void_p]
    error = ctypes.create_string_buffer(4096)
    view = bridge.AnsightSimulatorPreviewViewCreate(
        str(developer).encode(), device.encode(), error, len(error)
    )
    if not view:
        raise RuntimeError(error.value.decode())

    try:
        width = ctypes.c_int32()
        height = ctypes.c_int32()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if bridge.AnsightSimulatorPreviewViewGetSurfaceSize(view, ctypes.byref(width), ctypes.byref(height)):
                if width.value > 0 and height.value > 0:
                    return {"width": width.value, "height": height.value}
            time.sleep(0.1)
        raise AssertionError("The native preview did not receive a simulator surface")
    finally:
        bridge.AnsightSimulatorPreviewViewDestroy(view)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--xcode", required=True, type=Path)
    parser.add_argument("--runtime", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--preview-library", type=Path,
                        help="Also verify the built native preview library receives a simulator surface.")
    parser.add_argument("--stylus-helper", type=Path,
                        help="Test this packaged Pencil helper without rebuilding or changing its signature.")
    parser.add_argument("--ansight-cli", type=Path,
                        help="Record app receipts and screens with this CLI and an already running isolated host.")
    parser.add_argument("--ansight-data-dir", type=Path)
    parser.add_argument("--keep-on-failure", action="store_true",
                        help="Keep only this test's simulator after a failure for diagnosis.")
    parser.add_argument("--device-type", default="com.apple.CoreSimulator.SimDeviceType.iPad-mini-A17-Pro")
    args = parser.parse_args()
    if bool(args.ansight_cli) != bool(args.ansight_data_dir):
        parser.error("--ansight-cli and --ansight-data-dir must be supplied together")
    architecture = platform.machine()
    if architecture not in ("arm64", "x86_64"):
        parser.error("The probe requires an Apple silicon or Intel Mac.")

    developer = args.xcode.resolve()
    if developer.suffix == ".app":
        developer = developer / "Contents/Developer"
    if not (developer / "usr/bin/simctl").is_file():
        parser.error("The selected Xcode must contain simctl.")

    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).resolve().parent
    native = source.parent.parent / "AnsightSimulatorHid"
    environment = dict(os.environ, DEVELOPER_DIR=str(developer))
    bundle_id = "ai.ansight.xcode-compat-probe"

    def run(arguments, timeout=120, check=True):
        result = subprocess.run(
            [str(value) for value in arguments], env=environment, text=True,
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=timeout,
        )
        if check and result.returncode != 0:
            raise RuntimeError(f"{arguments[0]} failed: {result.stdout}\n{result.stderr}")
        return result.stdout.strip()

    def simctl(*arguments, **options):
        return run(["/usr/bin/xcrun", "simctl", *arguments], **options)

    def ansight(*arguments):
        return json.loads(run([args.ansight_cli.resolve(), *arguments,
                               "--data-dir", args.ansight_data_dir.resolve(), "--json"]))

    def wait_for_capture(watch_id, active):
        deadline = time.monotonic() + 45
        while time.monotonic() < deadline:
            watches = ansight("app", "watch", "list")["watches"]
            watch = next(item for item in watches if item["watch"]["id"] == watch_id)
            selected = next((item for item in watch["devices"] if item["deviceId"] == device), None)
            session_id = selected.get("sessionId") if selected else None
            if active and session_id:
                return session_id
            if not active and not session_id:
                return
            time.sleep(0.5)
        raise AssertionError(f"Ansight watch did not become {'active' if active else 'inactive'}: {watch}")

    (output / "xcode-version.txt").write_text(run(["/usr/bin/xcodebuild", "-version"]) + "\n")
    with tempfile.TemporaryDirectory(prefix="ansight-input-probe-") as temporary:
        build = Path(temporary)
        app = build / "CompatProbe.app"
        app.mkdir()
        info = {
            "CFBundleIdentifier": bundle_id,
            "CFBundleExecutable": "CompatProbe",
            "CFBundleName": "Ansight Input Probe",
            "CFBundlePackageType": "APPL",
            "CFBundleVersion": "1",
            "CFBundleShortVersionString": "1.0",
            "MinimumOSVersion": "17.0",
            "UIDeviceFamily": [1, 2],
            "UILaunchScreen": {},
            "UIApplicationSceneManifest": {
                "UIApplicationSupportsMultipleScenes": False,
                "UISceneConfigurations": {},
            },
        }
        (app / "Info.plist").write_bytes(plistlib.dumps(info))
        run([
            "/usr/bin/xcrun", "--sdk", "iphonesimulator", "swiftc", "-swift-version", "5",
            "-parse-as-library", "-target", f"{architecture}-apple-ios17.0-simulator",
            "-module-name", "CompatProbe", "-module-cache-path", build / "modules",
            source / "CompatProbe.swift", "-o", app / "CompatProbe",
        ])
        run(["/usr/bin/codesign", "--force", "--sign", "-", app])
        driver = build / "input"
        helper = build / "AnsightSimulatorStylusGuest"
        if args.stylus_helper:
            shutil.copy2(args.stylus_helper.resolve(), helper)
            run(["/usr/bin/codesign", "--verify", "--strict", helper])
        else:
            run(["/bin/bash", native / "build-stylus-helper.sh", architecture, helper])
        run([
            "/usr/bin/xcrun", "--sdk", "macosx", "clang", "-fobjc-arc", "-fblocks", "-fmodules",
            f"-fmodules-cache-path={build / 'modules'}", "-I", native,
            native / "AnsightSimulatorHid.m", native / "_Xcode27SimulatorHid.m", source / "input.c",
            "-framework", "Foundation", "-framework", "CoreGraphics", "-o", driver,
        ])

        device = simctl("create", "Ansight HID Compatibility Test", args.device_type, args.runtime)
        print(f"Testing {developer} on isolated simulator {device}", flush=True)
        passed = False
        watch_id = None
        recordings = []
        try:
            simctl("boot", device)
            simctl("bootstatus", device, "-b", timeout=240)
            simctl("install", device, app)
            data = Path(simctl("get_app_container", device, bundle_id, "data")) / "Documents/events.json"
            if args.ansight_cli:
                watch = ansight("app", "watch", "add", bundle_id, "--platform", "ios", "--device-id", device,
                                "--capture-file", "Documents/events.json", "--screenshot-interval-ms", "250")
                watch_id = watch["watchId"]

            for surface in ("native", "web"):
                if data.exists():
                    data.unlink()
                launch_arguments = ["--web"] if surface == "web" else []
                simctl("launch", device, bundle_id, *launch_arguments)
                session_id = wait_for_capture(watch_id, True) if watch_id else None

                def wait_for(predicate, message):
                    deadline = time.monotonic() + 15
                    events = []
                    while time.monotonic() < deadline:
                        try:
                            events = json.loads(data.read_text())
                            (output / f"{surface}.json").write_text(json.dumps(events, indent=2) + "\n")
                            if predicate(events):
                                return
                        except (FileNotFoundError, json.JSONDecodeError):
                            pass
                        time.sleep(0.1)
                    simctl("io", device, "screenshot", output / f"{surface}-failure.png", check=False)
                    raise AssertionError(f"{message}. Observed evidence: {events}")

                def ready(events):
                    surface_ready = any(event.get("surface") == surface and event.get("kind") == "ready"
                                        for event in events)
                    is_active = any(event.get("kind") == "active" for event in events)
                    return surface_ready and is_active

                wait_for(ready, f"The {surface} probe did not become ready and active")

                for action in ("finger", "pencil"):
                    run([driver, developer, device, action], timeout=15)
                    if surface == "native":
                        expected_type = 0 if action == "finger" else 2

                        def received(events):
                            phases = {event.get("phase") for event in events
                                      if event.get("kind") == "native" and event.get("type") == expected_type}
                            return {0, 1, 3}.issubset(phases)
                    else:
                        expected_type = "touch" if action == "finger" else "pen"

                        def received(events):
                            phases = {event.get("phase") for event in events
                                      if event.get("pointerType") == expected_type and event.get("isTrusted") is True}
                            return {"pointerdown", "pointermove", "pointerup"}.issubset(phases)

                    wait_for(received, f"{surface} did not receive the {action} sequence")
                    print(f"PASS: {surface} observed {action} down/move/up", flush=True)
                    if session_id:
                        time.sleep(2)

                if surface == "web":
                    run([driver, developer, device, "focus-keyboard"], timeout=15)
                    wait_for(lambda events: any(event.get("kind") == "keyboard-ready" for event in events),
                             "The web text field did not gain focus from the native tap")

                run([driver, developer, device, "key"], timeout=15)

                def received_key(events):
                    if surface == "native":
                        phases = {event.get("phase") for event in events
                                  if event.get("kind") == "key" and event.get("code") == 4}
                        return {"down", "up"}.issubset(phases)
                    phases = {event.get("phase") for event in events
                              if event.get("key") == "a" and event.get("isTrusted") is True}
                    return {"keydown", "keyup"}.issubset(phases)

                wait_for(received_key, f"{surface} did not receive the keyboard sequence")
                if surface == "web":
                    wait_for(lambda events: any(event.get("kind") == "text" and event.get("value") == "a"
                                                for event in events),
                             "The web text field did not receive the typed character")
                print(f"PASS: {surface} observed keyboard down/up", flush=True)
                if session_id:
                    time.sleep(2)

                run([driver, developer, device, "home"], timeout=15)
                wait_for(lambda events: events and events[-1].get("kind") == "background",
                         f"Home did not background the {surface} probe")
                print(f"PASS: Home backgrounded the {surface} probe", flush=True)
                if session_id:
                    time.sleep(2)
                simctl("terminate", device, bundle_id)
                if session_id:
                    wait_for_capture(watch_id, False)
                    name = f"Xcode {args.xcode.name} / {args.runtime} / {surface} HID receipts"
                    ansight("session", "metadata", session_id, "--name", name)
                    archive = output / f"{surface}.ansight.zip"
                    ansight("session", "export", session_id, archive)
                    recordings.append({"sessionId": session_id, "surface": surface, "archive": str(archive)})
                    (output / "ansight-sessions.json").write_text(json.dumps(recordings, indent=2) + "\n")
                    print(f"RECORDED: {session_id} -> {archive}", flush=True)

            if args.preview_library:
                size = verify_preview(args.preview_library, developer, device)
                (output / "preview.json").write_text(json.dumps(size, indent=2) + "\n")
                print(f"PASS: native preview received {size['width']}x{size['height']} surface", flush=True)
            passed = True
        finally:
            if watch_id:
                ansight("app", "watch", "disable", watch_id)
            if passed or not args.keep_on_failure:
                simctl("shutdown", device, check=False)
                simctl("delete", device, check=False)
            else:
                print(f"Retained failed test simulator for diagnosis: {device}", flush=True)

    print(f"PASS: Simulator HID input. Evidence: {output}", flush=True)


if __name__ == "__main__":
    main()
