// Built and shipped with Ansight. This library never changes system audio defaults,
// Simulator input selection, or app state. It only inspects routing and plays PCM.
import AppKit
import ApplicationServices
import AVFoundation
import AudioToolbox
import CoreAudio
import Darwin
import Foundation

private struct InjectionError: LocalizedError {
    let code: String
    let message: String
    var diagnostics: [String: Any] = [:]
    var errorDescription: String? { message }
}

private struct AudioDevice: Codable {
    let id: AudioDeviceID
    let name: String
    let uid: String
    let inputChannels: Int
    let outputChannels: Int
    let isVirtual: Bool
}

private struct Options {
    let state: PlaybackState
    let command: String
    let values: [String: String]
    func required(_ key: String) throws -> String {
        guard let value = values[key], !value.isEmpty else {
            throw InjectionError(code: "invalid-arguments", message: "Missing \(key).")
        }
        return value
    }
}

private struct InputRoute {
    let process: NSRunningApplication
    let menu: AXUIElement
    let selection: String
}

private final class PlaybackState: @unchecked Sendable {
    private let gate = NSLock()
    var deliveryStarted = false
    private var didFinish = false
    private var interrupted = false
    func finish() { gate.lock(); defer { gate.unlock() }; didFinish = true }
    func interrupt() { gate.lock(); defer { gate.unlock() }; interrupted = true }
    func checkCancellation() throws {
        if cancelled { throw InjectionError(code: "cancelled", message: "Audio injection cancelled.") }
    }
    var finished: Bool { gate.lock(); defer { gate.unlock() }; return didFinish }
    var cancelled: Bool { gate.lock(); defer { gate.unlock() }; return interrupted }
}

private func check(_ status: OSStatus, _ operation: String) throws {
    guard status == noErr else {
        throw InjectionError(code: "coreaudio-error", message: "\(operation) failed (OSStatus \(status)).")
    }
}

private func stringProperty(_ device: AudioObjectID, _ selector: AudioObjectPropertySelector) throws -> String {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                             mElement: kAudioObjectPropertyElementMain)
    let storage = UnsafeMutablePointer<Unmanaged<CFString>?>.allocate(capacity: 1)
    storage.initialize(to: nil)
    defer { storage.deinitialize(count: 1); storage.deallocate() }
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    try check(AudioObjectGetPropertyData(device, &address, 0, nil, &size, storage), "Reading device identity")
    guard let value = storage.pointee else {
        throw InjectionError(code: "coreaudio-error", message: "Device \(device) returned an empty identity.")
    }
    return value.takeRetainedValue() as String
}

private func objectIDs(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                       _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) throws -> [AudioObjectID] {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    try check(AudioObjectGetPropertyDataSize(object, &address, 0, nil, &size), "Reading audio objects")
    var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
    if ids.isEmpty { return [] }
    try check(ids.withUnsafeMutableBytes {
        AudioObjectGetPropertyData(object, &address, 0, nil, &size, $0.baseAddress!)
    }, "Reading audio objects")
    return ids
}

private func integerProperty(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) throws -> UInt32 {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal,
                                             mElement: kAudioObjectPropertyElementMain)
    var value: UInt32 = 0
    var size = UInt32(MemoryLayout<UInt32>.size)
    try check(AudioObjectGetPropertyData(object, &address, 0, nil, &size, &value), "Reading audio state")
    return value
}

private func channelCount(_ device: AudioDeviceID, _ scope: AudioObjectPropertyScope) throws -> Int {
    var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration, mScope: scope,
                                             mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    try check(AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size), "Reading device channel count")
    guard size > 0 else { return 0 }
    let storage = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { storage.deallocate() }
    try check(AudioObjectGetPropertyData(device, &address, 0, nil, &size, storage), "Reading device channels")
    return UnsafeMutableAudioBufferListPointer(storage.assumingMemoryBound(to: AudioBufferList.self))
        .reduce(0) { $0 + Int($1.mNumberChannels) }
}

private func listDevices() throws -> [AudioDevice] {
    try objectIDs(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDevices).map { device in
        AudioDevice(id: device, name: try stringProperty(device, kAudioObjectPropertyName),
                    uid: try stringProperty(device, kAudioDevicePropertyDeviceUID),
                    inputChannels: try channelCount(device, kAudioObjectPropertyScopeInput),
                    outputChannels: try channelCount(device, kAudioObjectPropertyScopeOutput),
                    isVirtual: try integerProperty(device, kAudioDevicePropertyTransportType) == kAudioDeviceTransportTypeVirtual)
    }
}

private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
    return value
}

private func children(_ element: AXUIElement) -> [AXUIElement] {
    attribute(element, kAXChildrenAttribute) as? [AXUIElement] ?? []
}

private func title(_ element: AXUIElement) -> String {
    attribute(element, kAXTitleAttribute) as? String ?? ""
}

private func findInputMenu(_ element: AXUIElement, state: PlaybackState, depth: Int = 0) throws -> AXUIElement? {
    try state.checkCancellation()
    guard depth < 8 else { return nil }
    if ["Audio Input", "Sound Input"].contains(title(element)) { return element }
    for child in children(element) {
        if let result = try findInputMenu(child, state: state, depth: depth + 1) { return result }
    }
    return nil
}

private func selectedMenuItems(_ element: AXUIElement, state: PlaybackState, depth: Int = 0) throws -> [String] {
    try state.checkCancellation()
    guard depth < 3 else { return [] }
    let mark = attribute(element, kAXMenuItemMarkCharAttribute) as? String ?? ""
    var values = mark.isEmpty ? [] : [title(element)]
    for child in children(element) { values += try selectedMenuItems(child, state: state, depth: depth + 1) }
    return values
}

private func verifyTarget(_ options: Options) throws {
    try options.state.checkCancellation()
    let target = try options.required("--device-id")
    guard UUID(uuidString: target) != nil else {
        throw InjectionError(code: "unsupported-device", message: "An exact iOS Simulator UDID is required.")
    }
    let developer = URL(fileURLWithPath: try options.required("--simulator-app"))
        .deletingLastPathComponent().deletingLastPathComponent()
    let process = Process()
    process.executableURL = developer.appendingPathComponent("usr/bin/simctl")
    process.arguments = ["list", "devices", "booted", "--json"]
    process.environment = ProcessInfo.processInfo.environment.merging(["DEVELOPER_DIR": developer.path]) { _, new in new }
    // A file avoids blocking on a full stdout pipe while polling cancellation.
    let outputURL = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
    guard FileManager.default.createFile(atPath: outputURL.path, contents: nil,
                                         attributes: [.posixPermissions: 0o600]) else {
        throw InjectionError(code: "simulator-discovery-failed", message: "Could not create Simulator discovery output.")
    }
    defer { try? FileManager.default.removeItem(at: outputURL) }
    let output = try FileHandle(forWritingTo: outputURL)
    defer { try? output.close() }
    process.standardOutput = output
    process.standardError = FileHandle.nullDevice
    try process.run()
    defer {
        if process.isRunning {
            kill(process.processIdentifier, SIGKILL)
            process.waitUntilExit()
        }
    }
    let deadline = Date().addingTimeInterval(5)
    while process.isRunning {
        try options.state.checkCancellation()
        guard Date() < deadline else {
            throw InjectionError(code: "simulator-discovery-failed", message: "Simulator discovery timed out.")
        }
        Thread.sleep(forTimeInterval: 0.02)
    }
    process.waitUntilExit()
    try options.state.checkCancellation()
    let data = try Data(contentsOf: outputURL)
    guard process.terminationStatus == 0,
          let document = try JSONSerialization.jsonObject(with: data) as? [String: Any],
          let runtimes = document["devices"] as? [String: [[String: Any]]] else {
        throw InjectionError(code: "simulator-discovery-failed", message: "Could not verify booted simulators using the configured Xcode.")
    }
    let devices = runtimes.filter { $0.key.contains(".iOS-") }.values.flatMap { $0 }
        .filter { $0["state"] as? String == "Booted" }
    guard devices.contains(where: { ($0["udid"] as? String)?.caseInsensitiveCompare(target) == .orderedSame }) else {
        throw InjectionError(code: "simulator-not-running", message: "The running session's exact iOS Simulator is not booted.")
    }
    guard devices.count == 1 else {
        throw InjectionError(code: "audio-route-ambiguous", message: "iOS Simulator audio workflows cannot run in parallel on this Mac. Select one device, omit test --parallel, and shut down other iOS simulators before rerunning the complete audio workflow.")
    }
}

private func requireAccessibilityPermission(_ trusted: Bool) throws {
    guard trusted else {
        throw InjectionError(code: "accessibility-permission-required",
            message: """
            AUDIO INJECTION BLOCKED — MACOS ACCESSIBILITY PERMISSION REQUIRED.
            The Ansight host is not allowed to verify Simulator Audio Input. NO AUDIO WAS INJECTED.
            Open System Settings > Privacy & Security > Accessibility and enable the application running the host.
            If the host runs in Terminal, enable Terminal and restart the host there with 'ansight host stop' then 'ansight host run'.
            A Terminal grant does not cover a host started independently in the background. Restart from the permitted application before retrying.
            """,
            diagnostics: ["permission": "accessibility", "permissionGranted": false,
                "hostProcessId": ProcessInfo.processInfo.processIdentifier, "deliveryStarted": false])
    }
}

private func inspectRoute(_ options: Options, device: AudioDevice, devices: [AudioDevice]) throws -> InputRoute {
    try verifyTarget(options)
    let expectedURL = URL(fileURLWithPath: try options.required("--simulator-app")).resolvingSymlinksInPath()
    let applications = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.iphonesimulator")
        .filter { $0.bundleURL?.resolvingSymlinksInPath() == expectedURL && !$0.isTerminated }
    guard applications.count == 1 else {
        throw InjectionError(code: "simulator-not-running",
            message: "The Simulator application belonging to the configured Xcode must already be running.")
    }
    let process = applications[0]
    let application = AXUIElementCreateApplication(process.processIdentifier)
    AXUIElementSetMessagingTimeout(application, 1)
    guard let menuBarValue = attribute(application, kAXMenuBarAttribute),
          CFGetTypeID(menuBarValue) == AXUIElementGetTypeID() else {
        throw InjectionError(code: "input-route-unverified", message: "Simulator's menu bar could not be inspected through Accessibility.")
    }
    let menuBar = unsafeBitCast(menuBarValue, to: AXUIElement.self)
    guard let menu = try findInputMenu(menuBar, state: options.state) else {
        throw InjectionError(code: "input-route-unverified",
            message: "Simulator Audio Input menu is unavailable. Select I/O > Audio Input > \(device.name) in Simulator, then retry. This build requires an English Simulator menu.")
    }
    try verifyInputSelection(menu, device: device, devices: devices, state: options.state)
    return InputRoute(process: process, menu: menu, selection: device.name)
}

private func verifyInputSelection(_ menu: AXUIElement, device: AudioDevice, devices: [AudioDevice], state: PlaybackState) throws {
    let selected = try selectedMenuItems(menu, state: state)
    guard selected.count == 1, selected[0] == device.name,
          devices.filter({ $0.name == device.name && $0.inputChannels > 0 }).count == 1 else {
        throw InjectionError(code: "input-route-unverified",
            message: "Simulator must explicitly select the unique input '\(device.name)' (UID \(device.uid)); observed selection: \(selected.joined(separator: ", ")). Select I/O > Audio Input in Simulator. System defaults and recent-device preferences are not route evidence.")
    }
}

// Bind CoreAudio observations to this app on this simulator, not another host reader.
private struct ActiveInputDevice {
    let processId: UInt32
    let uid: String
    let name: String
}

private func inputDevices(_ device: AudioObjectID) throws -> [AudioObjectID] {
    if try integerProperty(device, kAudioDevicePropertyTransportType) == kAudioDeviceTransportTypeAggregate {
        return try objectIDs(device, kAudioAggregateDevicePropertyActiveSubDeviceList)
            .filter { try channelCount($0, kAudioObjectPropertyScopeInput) > 0 }
    }
    return try channelCount(device, kAudioObjectPropertyScopeInput) > 0 ? [device] : []
}

private func activeInputs(_ options: Options) throws -> [ActiveInputDevice] {
    guard #available(macOS 14.2, *) else {
        throw InjectionError(code: "microphone-readiness-unavailable",
            message: "Inspecting the simulator app's active audio route requires macOS 14.2 or newer.")
    }
    let appId = try options.required("--app-id")
    let deviceId = try options.required("--device-id")
    var inputs: [ActiveInputDevice] = []

    for process in try objectIDs(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyProcessObjectList) {
        try options.state.checkCancellation()
        guard try stringProperty(process, kAudioProcessPropertyBundleID) == appId,
              try integerProperty(process, kAudioProcessPropertyIsRunningInput) != 0 else { continue }
        let pid = try integerProperty(process, kAudioProcessPropertyPID)
        var path = [CChar](repeating: 0, count: 4096)
        guard proc_pidpath(Int32(pid), &path, UInt32(path.count)) > 0 else { continue }
        let executable = String(decoding: path.prefix(while: { $0 != 0 }).map { UInt8(bitPattern: $0) }, as: UTF8.self)
        guard executable.localizedCaseInsensitiveContains("/CoreSimulator/Devices/\(deviceId)/") else { continue }

        for device in try objectIDs(process, kAudioProcessPropertyDevices, kAudioObjectPropertyScopeInput) {
            for input in try inputDevices(device) {
                inputs.append(ActiveInputDevice(processId: pid,
                    uid: try stringProperty(input, kAudioDevicePropertyDeviceUID),
                    name: try stringProperty(input, kAudioObjectPropertyName)))
            }
        }
    }
    return inputs
}

private func verifyActiveInput(_ inputs: [ActiveInputDevice], expected: AudioDevice) throws {
    guard inputs.contains(where: { $0.uid != expected.uid }) else { return }
    let names = Array(Set(inputs.map { $0.name })).sorted().joined(separator: ", ")
    throw InjectionError(code: "simulator-audio-route-stale",
        message: "Simulator audio route is stale—restart required. Simulator selects \(expected.name), but the app is recording from \(names). Start a fresh app run to recover; this run was not replayed.",
        diagnostics: ["expectedInputDeviceUid": expected.uid, "expectedInputDeviceName": expected.name,
            "actualInputDevices": inputs.map { ["processId": $0.processId, "uid": $0.uid, "name": $0.name] as [String: Any] },
            "restartRequired": true, "activeInputRouteVerified": false])
}

private func verifyOutput(_ unit: AudioUnit, _ device: AudioDevice) throws {
    var current: AudioDeviceID = 0
    var size = UInt32(MemoryLayout<AudioDeviceID>.size)
    try check(AudioUnitGetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &current, &size), "Checking output route")
    guard current == device.id, try integerProperty(device.id, kAudioDevicePropertyDeviceIsAlive) != 0 else {
        throw InjectionError(code: "audio-route-lost", message: "The explicit output device changed or disconnected; playback stopped.")
    }
}

private func pause(_ state: PlaybackState) throws {
    try state.checkCancellation()
    if !RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.05)) {
        Thread.sleep(forTimeInterval: 0.02)
    }
}

// Player time measures render progress, not microphone capture or audible output.
private struct PlaybackProgress {
    var sampleCount = 0
    var renderedMilliseconds: Double = 0
    var lastProgressElapsedMilliseconds: Double?

    mutating func record(sampleTime: AVAudioFramePosition?, sampleRate: Double, elapsedMilliseconds: Double) {
        sampleCount += 1
        guard let sampleTime, sampleTime >= 0, sampleRate > 0 else { return }
        let rendered = Double(sampleTime) / sampleRate * 1000
        if rendered > renderedMilliseconds {
            renderedMilliseconds = rendered
            lastProgressElapsedMilliseconds = elapsedMilliseconds
        }
    }

    func diagnostics(elapsedMilliseconds: Double, expectedMilliseconds: Double) -> [String: Any] {
        return ["progressSampleCount": sampleCount,
                "renderedMilliseconds": renderedMilliseconds,
                "expectedDurationMilliseconds": expectedMilliseconds,
                "playbackElapsedMilliseconds": elapsedMilliseconds,
                "lastProgressElapsedMilliseconds": lastProgressElapsedMilliseconds as Any? ?? NSNull(),
                "millisecondsSinceLastProgress": lastProgressElapsedMilliseconds.map { max(0, elapsedMilliseconds - $0) } as Any? ?? NSNull(),
                "renderProgressObserved": lastProgressElapsedMilliseconds != nil,
                "renderReachedExpectedDuration": renderedMilliseconds >= expectedMilliseconds]
    }
}

private final class AudioConfigurationChanges: @unchecked Sendable {
    private let gate = NSLock()
    private var events: [Double] = []
    private let started = ProcessInfo.processInfo.systemUptime
    func record() {
        gate.lock(); defer { gate.unlock() }
        events.append((ProcessInfo.processInfo.systemUptime - started) * 1000)
    }
    var offsets: [Double] { gate.lock(); defer { gate.unlock() }; return events }
}

// A device change can asynchronously stop AVAudioEngine. Settle that change
// before scheduling any fixture; restarting after delivery could replay samples.
private func preparePlaybackEngine(_ engine: AVAudioEngine, unit: AudioUnit, device: AudioDevice,
                                   state: PlaybackState, changes: AudioConfigurationChanges) throws -> Int {
    let deadline = ProcessInfo.processInfo.systemUptime + 3
    var stableSince = ProcessInfo.processInfo.systemUptime
    var observedChanges = changes.offsets.count
    var starts = 0
    while ProcessInfo.processInfo.systemUptime < deadline {
        try state.checkCancellation()
        try verifyOutput(unit, device)
        let count = changes.offsets.count
        if count != observedChanges || !engine.isRunning {
            stableSince = ProcessInfo.processInfo.systemUptime
            observedChanges = count
        }
        if !engine.isRunning {
            engine.prepare()
            try engine.start()
            starts += 1
        }
        if engine.isRunning && ProcessInfo.processInfo.systemUptime - stableSince >= 0.25 { return starts }
        try pause(state)
    }
    throw InjectionError(code: "audio-engine-unstable",
        message: "The audio engine did not stabilize after selecting the output device. No audio was scheduled.",
        diagnostics: ["engineRunning": engine.isRunning, "engineStartAttempts": starts,
            "configurationChangeOffsetsMilliseconds": changes.offsets, "audioDeviceUid": device.uid])
}

private func play(_ options: Options, device: AudioDevice, devices: [AudioDevice], route: InputRoute) throws -> [String: Any] {
    let file = try AVAudioFile(forReading: URL(fileURLWithPath: try options.required("--file")))
    guard file.length > 0 else { throw InjectionError(code: "invalid-audio", message: "The audio fixture has no samples.") }
    let waitText = options.values["--wait-ms"] ?? "0"
    guard let waitMs = Int(waitText), (0...10000).contains(waitMs) else {
        throw InjectionError(code: "invalid-arguments", message: "--wait-ms must be between 0 and 10000.")
    }
    let state = options.state
    try state.checkCancellation()
    try verifyActiveInput(activeInputs(options), expected: device)
    var inputClients: [UInt32] = []
    if waitMs > 0 {
        let deadline = Date().addingTimeInterval(Double(waitMs) / 1000)
        repeat {
            let inputs = try activeInputs(options)
            try verifyActiveInput(inputs, expected: device)
            inputClients = Array(Set(inputs.filter { $0.uid == device.uid }.map { $0.processId }))
            if !inputClients.isEmpty { break }
            try pause(state)
        } while Date() < deadline
        guard !inputClients.isEmpty else {
            throw InjectionError(code: "microphone-not-ready",
                message: "The target app is not reading the selected loopback device. Start microphone recording in the app and retry.")
        }
    }
    try verifyInputSelection(route.menu, device: device, devices: devices, state: state)
    try verifyTarget(options)
    let engine = AVAudioEngine()
    let player = AVAudioPlayerNode()
    let configurationChanges = AudioConfigurationChanges()
    let observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange,
        object: engine, queue: nil) { _ in configurationChanges.record() }
    defer { NotificationCenter.default.removeObserver(observer) }
    defer { player.stop(); engine.stop() }
    guard let unit = engine.outputNode.audioUnit else {
        throw InjectionError(code: "coreaudio-error", message: "CoreAudio did not provide an output audio unit.")
    }
    var deviceID = device.id
    try check(AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
        &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size)), "Selecting explicit player output")
    try verifyOutput(unit, device)
    engine.attach(player)
    engine.connect(player, to: engine.mainMixerNode, format: file.processingFormat)
    let engineStartAttempts = try preparePlaybackEngine(engine, unit: unit, device: device,
        state: state, changes: configurationChanges)
    try verifyOutput(unit, device)
    player.scheduleFile(file, at: nil, completionCallbackType: .dataPlayedBack) { callback in
        if callback == .dataPlayedBack { state.finish() }
    }
    try state.checkCancellation()
    state.deliveryStarted = true
    player.play()
    let expectedMilliseconds = Double(file.length) / file.processingFormat.sampleRate * 1000
    let playbackStarted = ProcessInfo.processInfo.systemUptime
    var progress = PlaybackProgress()
    let deadline = Date().addingTimeInterval(expectedMilliseconds / 1000 + 15)
    var nextInputCheck = Date.distantPast
    var nextTargetCheck = Date.distantPast
    while !state.finished {
        let elapsedMilliseconds = (ProcessInfo.processInfo.systemUptime - playbackStarted) * 1000
        let renderTime = player.lastRenderTime
        let playerTime = renderTime.flatMap { player.playerTime(forNodeTime: $0) }
        progress.record(sampleTime: playerTime?.isSampleTimeValid == true ? playerTime?.sampleTime : nil,
                        sampleRate: playerTime?.sampleRate ?? 0, elapsedMilliseconds: elapsedMilliseconds)
        let engineStopped = !engine.isRunning || !player.isPlaying
        if engineStopped || Date() >= deadline {
            // Completion can race with this observation. A completed callback wins.
            if state.finished { break }
            var diagnostics = progress.diagnostics(elapsedMilliseconds: elapsedMilliseconds, expectedMilliseconds: expectedMilliseconds)
            diagnostics["engineStartAttempts"] = engineStartAttempts
            diagnostics["configurationChangeOffsetsMilliseconds"] = configurationChanges.offsets
            diagnostics["engineRunning"] = engine.isRunning
            diagnostics["playerPlaying"] = player.isPlaying
            diagnostics["renderTimeAvailable"] = renderTime != nil
            diagnostics["playerTimeAvailable"] = playerTime != nil
            diagnostics["completionCallbackReceived"] = state.finished
            diagnostics["audioDeviceName"] = device.name
            diagnostics["audioDeviceUid"] = device.uid
            diagnostics["audioDeviceAlive"] = (try? integerProperty(device.id, kAudioDevicePropertyDeviceIsAlive)) as Any? ?? NSNull()
            diagnostics["audioDeviceRunning"] = (try? integerProperty(device.id, kAudioDevicePropertyDeviceIsRunning)) as Any? ?? NSNull()
            diagnostics["outputSampleRate"] = engine.outputNode.outputFormat(forBus: 0).sampleRate
            diagnostics["playerSampleRate"] = playerTime?.sampleRate as Any? ?? NSNull()
            diagnostics["hostInputClientPidsAtStart"] = inputClients
            do {
                diagnostics["activeInputDevicesAtTimeout"] = try activeInputs(options).map {
                    ["processId": $0.processId, "uid": $0.uid, "name": $0.name] as [String: Any]
                }
            } catch { diagnostics["activeInputInspectionError"] = error.localizedDescription }
            let reason = engineStopped ? "The audio engine or player stopped before playback completed." : "The audio device did not complete playback within its deadline."
            throw InjectionError(code: engineStopped ? "audio-engine-stopped" : "playback-timeout",
                message: "\(reason) Player render progress: \(Int(progress.renderedMilliseconds)) / \(Int(expectedMilliseconds)) ms. See playback diagnostics; render progress does not verify microphone capture.",
                diagnostics: diagnostics)
        }
        guard !route.process.isTerminated else {
            throw InjectionError(code: "simulator-disconnected", message: "The Simulator application exited during playback.")
        }
        try verifyOutput(unit, device)
        if Date() >= nextInputCheck {
            try verifyInputSelection(route.menu, device: device, devices: try listDevices(), state: state)
            nextInputCheck = Date().addingTimeInterval(0.25)
        }
        if Date() >= nextTargetCheck {
            try verifyTarget(options)
            nextTargetCheck = Date().addingTimeInterval(1)
        }
        try pause(state)
    }
    try verifyInputSelection(route.menu, device: device, devices: try listDevices(), state: state)
    try verifyTarget(options)
    try verifyOutput(unit, device)
    return ["completionKind": "host-output-played", "submittedFrames": file.length,
            "deliveryStarted": true,
            "hostInputClientPids": inputClients, "microphoneReadiness": waitMs > 0 ? "target-loopback-input-active" : "not-requested",
            "captureVerified": false, "targetMicrophoneVerified": false, "transcriptionVerified": false]
}

private func parseOptions(_ arguments: [String], state: PlaybackState) throws -> Options {
    guard let command = arguments.first, ["devices", "accessibility", "inspect", "inject"].contains(command), arguments.count % 2 == 1 else {
        throw InjectionError(code: "invalid-arguments", message: "Expected devices | accessibility | inspect/inject --device-uid UID --simulator-app PATH --device-id UDID --app-id ID [--file WAV --wait-ms N]")
    }
    var values: [String: String] = [:]
    for index in stride(from: 1, to: arguments.count, by: 2) {
        let key = arguments[index]
        guard ["--device-uid", "--simulator-app", "--device-id", "--app-id", "--file", "--wait-ms"].contains(key), values[key] == nil else {
            throw InjectionError(code: "invalid-arguments", message: "Unknown or repeated option \(key).")
        }
        values[key] = arguments[index + 1]
    }
    return Options(state: state, command: command, values: values)
}

private func execute(_ arguments: [String], state: PlaybackState) throws -> [String: Any] {
    try state.checkCancellation()
    let options = try parseOptions(arguments, state: state)
    if options.command == "accessibility" {
        // Query the calling host process without prompting, opening Simulator, or requiring a driver.
        return ["success": true, "accessibilityGranted": AXIsProcessTrusted()]
    }
    if options.command == "inspect" || options.command == "inject" {
        try requireAccessibilityPermission(AXIsProcessTrusted())
    }
    let devices = try listDevices()
    if options.command == "devices" {
        let encoded = try JSONEncoder().encode(devices)
        return ["success": true, "devices": try JSONSerialization.jsonObject(with: encoded)]
    } else {
        let uid = try options.required("--device-uid")
        guard let device = devices.first(where: { $0.uid == uid }), device.inputChannels > 0, device.outputChannels > 0 else {
            throw InjectionError(code: "audio-device-missing",
                message: "Duplex CoreAudio device UID '\(uid)' is not registered. Install BlackHole 2ch with 'brew install --cask blackhole-2ch', then follow the installer's restart guidance.")
        }
        guard device.isVirtual else {
            throw InjectionError(code: "unsupported-audio-device", message: "The selected CoreAudio UID must be a dedicated virtual loopback device; physical audio outputs are not used for microphone injection.")
        }
        let route = try inspectRoute(options, device: device, devices: devices)
        let inputs = try activeInputs(options)
        try verifyActiveInput(inputs, expected: device)
        var diagnostics: [String: Any] = ["audioDeviceUid": device.uid, "audioDeviceName": device.name,
            "simulatorDeviceId": try options.required("--device-id"), "simulatorPid": route.process.processIdentifier,
            "inputRouteVerified": true, "configuredInputSelectionVerified": true,
            "activeInputRouteVerified": !inputs.isEmpty, "inputRouteEvidence": "simulator-menu-checked-item",
            "systemAudioDefaultsChanged": false, "routeScope": "host-global", "captureVerified": false, "targetMicrophoneVerified": false]
        if options.command == "inject" {
            diagnostics.merge(try play(options, device: device, devices: devices, route: route)) { _, new in new }
        }
        return ["success": true, "code": "available", "message": "The configured Simulator input selection is verified. Active routing is checked when the app records.", "diagnostics": diagnostics]
    }
}

// Each handle owns one operation. Cancel may run concurrently with Execute;
// Destroy must run only after Execute and all cancellation calls have returned.
@_cdecl("AnsightAudioInjectionCreate")
public func audioInjectionCreate() -> UnsafeMutableRawPointer {
    Unmanaged.passRetained(PlaybackState()).toOpaque()
}

@_cdecl("AnsightAudioInjectionCancel")
public func audioInjectionCancel(_ handle: UnsafeMutableRawPointer) {
    Unmanaged<PlaybackState>.fromOpaque(handle).takeUnretainedValue().interrupt()
}

@_cdecl("AnsightAudioInjectionDestroy")
public func audioInjectionDestroy(_ handle: UnsafeMutableRawPointer) {
    Unmanaged<PlaybackState>.fromOpaque(handle).release()
}

// Returns an owned UTF-8 JSON string; the caller must use FreeString exactly once.
@_cdecl("AnsightAudioInjectionExecute")
public func audioInjectionExecute(_ handle: UnsafeMutableRawPointer, _ argumentsJSON: UnsafePointer<CChar>) -> UnsafeMutablePointer<CChar>? {
    autoreleasepool {
        let state = Unmanaged<PlaybackState>.fromOpaque(handle).takeUnretainedValue()
        let result: [String: Any]
        do {
            let arguments = try JSONDecoder().decode([String].self, from: Data(String(cString: argumentsJSON).utf8))
            result = try execute(arguments, state: state)
        } catch {
            let failure = error as? InjectionError ?? InjectionError(code: "audio-helper-error", message: error.localizedDescription)
            var diagnostics = failure.diagnostics
            diagnostics["deliveryStarted"] = state.deliveryStarted
            diagnostics["captureVerified"] = false
            result = ["success": false, "code": failure.code, "message": failure.message,
                      "diagnostics": diagnostics]
        }
        guard let data = try? JSONSerialization.data(withJSONObject: result, options: [.sortedKeys]),
              let json = String(data: data, encoding: .utf8) else { return nil }
        return strdup(json)
    }
}

@_cdecl("AnsightAudioInjectionFreeString")
public func audioInjectionFreeString(_ value: UnsafeMutablePointer<CChar>?) {
    free(value)
}
