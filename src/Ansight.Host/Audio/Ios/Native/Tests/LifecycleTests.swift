
// Appended to the implementation by run-tests.sh. No audio driver or Simulator is required.
private func testDiscoveryLifecycle() throws {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    try FileManager.default.createDirectory(at: directory.appendingPathComponent("usr/bin"), withIntermediateDirectories: true)
    defer { try? FileManager.default.removeItem(at: directory) }
    let simctl = directory.appendingPathComponent("usr/bin/simctl")
    let marker = directory.appendingPathComponent("started")
    let target = "EC47CEBB-B438-484C-8597-6967D82C7059"
    let values = ["--device-id": target, "--simulator-app": directory.appendingPathComponent("Applications/Simulator.app").path]

    // The pid marker proves cancellation happens while discovery is running.
    // exec keeps the fake discovery command at the process ID owned by the library.
    try "#!/bin/sh\necho $$ > \"$DEVELOPER_DIR/started\"\nexec /bin/sleep 30\n".write(to: simctl, atomically: true, encoding: .utf8)
    try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: simctl.path)
    let state = PlaybackState()
    let cancellationFinished = DispatchSemaphore(value: 0)
    DispatchQueue.global().async {
        let deadline = Date().addingTimeInterval(10)
        while !FileManager.default.fileExists(atPath: marker.path) && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.01)
        }
        state.interrupt()
        cancellationFinished.signal()
    }
    let started = Date()
    do {
        try verifyTarget(Options(state: state, command: "inspect", values: values))
        fatalError("Cancelled discovery unexpectedly succeeded.")
    } catch let error as InjectionError {
        precondition(error.code == "cancelled", "Cancellation must preserve its error code.")
    }
    precondition(cancellationFinished.wait(timeout: .now() + 2) == .success)
    precondition(Date().timeIntervalSince(started) < 3, "Cancellation did not promptly stop discovery.")
    let pid = Int32(try String(contentsOf: marker, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines))!
    precondition(kill(pid, 0) == -1 && errno == ESRCH, "Discovery process survived cancellation.")

    // Large stdout must not deadlock the cancellation-aware discovery loop.
    let document = "{\"devices\":{\"com.apple.CoreSimulator.SimRuntime.iOS-26-0\":[{\"udid\":\"\(target)\",\"state\":\"Booted\",\"padding\":\"\(String(repeating: "x", count: 200000))\"}]}}"
    try document.write(to: directory.appendingPathComponent("devices.json"), atomically: true, encoding: .utf8)
    try "#!/bin/sh\nexec /bin/cat \"$DEVELOPER_DIR/devices.json\"\n".write(to: simctl, atomically: false, encoding: .utf8)
    try verifyTarget(Options(state: PlaybackState(), command: "inspect", values: values))
    print("PASS: Audio native lifecycle (in-flight cancellation, child cleanup, independent operation, large discovery output).")
}

try testDiscoveryLifecycle()

private func testActiveInputRoute() throws {
    let blackHole = AudioDevice(id: 105, name: "BlackHole 2ch", uid: "BlackHole2ch_UID",
        inputChannels: 2, outputChannels: 2, isVirtual: true)
    try verifyActiveInput([], expected: blackHole) // An idle app is not a stale route.
    try verifyActiveInput([ActiveInputDevice(processId: 42, uid: blackHole.uid, name: blackHole.name)], expected: blackHole)

    do {
        try verifyActiveInput([ActiveInputDevice(processId: 42, uid: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone")], expected: blackHole)
        fatalError("The built-in microphone must not satisfy the selected BlackHole route.")
    } catch let error as InjectionError {
        precondition(error.code == "simulator-audio-route-stale")
        precondition(error.message.contains("Simulator audio route is stale—restart required"))
        precondition(error.diagnostics["restartRequired"] as? Bool == true)
        precondition(error.diagnostics["expectedInputDeviceUid"] as? String == blackHole.uid)
        let devices = error.diagnostics["actualInputDevices"] as! [[String: Any]]
        precondition(devices[0]["uid"] as? String == "BuiltInMicrophoneDevice")
    }
    do {
        try verifyActiveInput([
            ActiveInputDevice(processId: 42, uid: blackHole.uid, name: blackHole.name),
            ActiveInputDevice(processId: 42, uid: "BuiltInMicrophoneDevice", name: "MacBook Pro Microphone")
        ], expected: blackHole)
        fatalError("A mixed input must not hide an unexpected microphone.")
    } catch let error as InjectionError {
        precondition(error.code == "simulator-audio-route-stale")
    }
    print("PASS: Active input route (idle, matching BlackHole, wrong/mixed microphone and actionable diagnostics).")
}

try testActiveInputRoute()

private func testAccessibilityPermissionFailure() throws {
    try requireAccessibilityPermission(true)
    do {
        try requireAccessibilityPermission(false)
        fatalError("Missing permission must reject injection.")
    } catch let error as InjectionError {
        precondition(error.code == "accessibility-permission-required")
        precondition(error.message.contains("AUDIO INJECTION BLOCKED"))
        precondition(error.message.contains("NO AUDIO WAS INJECTED"))
        precondition(error.message.contains("System Settings"))
        precondition(error.diagnostics["permissionGranted"] as? Bool == false)
        precondition(error.diagnostics["deliveryStarted"] as? Bool == false)
    }
    print("PASS: Accessibility permission rejects injection with explicit recovery guidance.")
}

try testAccessibilityPermissionFailure()

private func testPlaybackProgress() throws {
    var progress = PlaybackProgress()
    progress.record(sampleTime: nil, sampleRate: 0, elapsedMilliseconds: 100)
    let unavailable = progress.diagnostics(elapsedMilliseconds: 100, expectedMilliseconds: 1000)
    precondition(unavailable["renderProgressObserved"] as? Bool == false)
    precondition(unavailable["millisecondsSinceLastProgress"] is NSNull)
    _ = try JSONSerialization.data(withJSONObject: unavailable)

    progress.record(sampleTime: 8000, sampleRate: 16000, elapsedMilliseconds: 600)
    progress.record(sampleTime: 8000, sampleRate: 16000, elapsedMilliseconds: 1100)
    let stalled = progress.diagnostics(elapsedMilliseconds: 1600, expectedMilliseconds: 1000)
    precondition(stalled["renderedMilliseconds"] as? Double == 500)
    precondition(stalled["millisecondsSinceLastProgress"] as? Double == 1000)
    precondition(stalled["renderReachedExpectedDuration"] as? Bool == false)

    progress.record(sampleTime: 16000, sampleRate: 16000, elapsedMilliseconds: 1700)
    let complete = progress.diagnostics(elapsedMilliseconds: 1800, expectedMilliseconds: 1000)
    precondition(complete["renderReachedExpectedDuration"] as? Bool == true)
    _ = try JSONSerialization.data(withJSONObject: complete)
    print("PASS: Playback progress distinguishes unavailable, stalled and fully rendered audio; diagnostics serialize.")
}
try testPlaybackProgress()

private func testEnginePreparationCancellation() throws {
    let changes = AudioConfigurationChanges()
    changes.record()
    changes.record()
    precondition(changes.offsets.count == 2)
    precondition(changes.offsets[1] >= changes.offsets[0])
    _ = try JSONSerialization.data(withJSONObject: ["configurationChangeOffsetsMilliseconds": changes.offsets])
    let state = PlaybackState()
    state.interrupt()
    // Cancellation must be honored before touching the supplied audio unit.
    do {
        _ = try preparePlaybackEngine(AVAudioEngine(), unit: AudioUnit(bitPattern: 1)!,
            device: AudioDevice(id: 0, name: "test", uid: "test", inputChannels: 0, outputChannels: 0, isVirtual: true),
            state: state, changes: changes)
        fatalError("Cancelled preparation unexpectedly succeeded")
    } catch let error as InjectionError { precondition(error.code == "cancelled") }
    precondition(!state.deliveryStarted)
    print("PASS: Engine preparation cancellation precedes device access; configuration changes serialize.")
}
try testEnginePreparationCancellation()
