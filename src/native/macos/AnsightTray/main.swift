import AppKit
import Darwin
import Foundation
import UserNotifications

private let commandSchema = "ansight.desktop-command/v1"
private let actionSchema = "ansight.desktop-action/v1"
private let notificationActionIdentifierKey = "ansightNotificationActionIdentifier"
private let notificationActionDeferralSecondsKey = "ansightNotificationActionDeferralSeconds"
private let notificationRepeatIntervalSecondsKey = "ansightNotificationRepeatIntervalSeconds"

private struct DesktopConnectionConfiguration {
    let port: UInt16
    let token: String

    static func fromProcessArguments() -> DesktopConnectionConfiguration? {
        let arguments = CommandLine.arguments
        guard let portIndex = arguments.firstIndex(of: "--ansight-desktop-port"),
              arguments.indices.contains(portIndex + 1),
              let port = UInt16(arguments[portIndex + 1]),
              let tokenIndex = arguments.firstIndex(of: "--ansight-desktop-token"),
              arguments.indices.contains(tokenIndex + 1) else {
            return nil
        }

        return DesktopConnectionConfiguration(
            port: port,
            token: arguments[tokenIndex + 1])
    }
}

private final class DesktopConnection: @unchecked Sendable {
    private let socketHandle: FileHandle

    init(configuration: DesktopConnectionConfiguration) throws {
        let socketDescriptor = socket(AF_INET, SOCK_STREAM, 0)
        guard socketDescriptor >= 0 else {
            throw POSIXError(.ENOTSOCK)
        }

        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = configuration.port.bigEndian
        address.sin_addr = in_addr(s_addr: inet_addr("127.0.0.1"))

        let result = withUnsafePointer(to: &address) { pointer in
            pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) { socketAddress in
                Darwin.connect(
                    socketDescriptor,
                    socketAddress,
                    socklen_t(MemoryLayout<sockaddr_in>.size))
            }
        }
        guard result == 0 else {
            let errorCode = POSIXErrorCode(rawValue: errno) ?? .ECONNREFUSED
            Darwin.close(socketDescriptor)
            throw POSIXError(errorCode)
        }

        socketHandle = FileHandle(fileDescriptor: socketDescriptor, closeOnDealloc: true)
        try writeAction([
            "schema": actionSchema,
            "action": "ready",
            "token": configuration.token
        ])
    }

    func readCommands(
        onCommand: @escaping @Sendable (DesktopCommand) -> Void,
        onEnd: @escaping @Sendable () -> Void) {
        DispatchQueue.global(qos: .utility).async { [self] in
            readCommandsLoop(onCommand: onCommand, onEnd: onEnd)
        }
    }

    private func readCommandsLoop(
        onCommand: @escaping @Sendable (DesktopCommand) -> Void,
        onEnd: @escaping @Sendable () -> Void) {
        let readDescriptor = dup(socketHandle.fileDescriptor)
        guard readDescriptor >= 0,
              let stream = fdopen(readDescriptor, "r") else {
            onEnd()
            return
        }

        defer {
            fclose(stream)
            onEnd()
        }

        let decoder = JSONDecoder()
        var linePointer: UnsafeMutablePointer<CChar>?
        var capacity = 0
        defer { free(linePointer) }

        while getline(&linePointer, &capacity, stream) >= 0 {
            guard let linePointer,
                  let line = String(validatingUTF8: linePointer),
                  let data = line.data(using: .utf8),
                  let command = try? decoder.decode(DesktopCommand.self, from: data),
                  command.schema == commandSchema else {
                continue
            }

            onCommand(command)
        }
    }

    func writeAction(_ action: [String: String]) throws {
        let data = try JSONSerialization.data(withJSONObject: action)
        socketHandle.write(data)
        socketHandle.write(Data([0x0A]))
    }
}

private struct DesktopCommand: Decodable, Sendable {
    let schema: String
    let kind: String
    let url: String?
    let identifier: String?
    let title: String?
    let body: String?
    let sessionId: String?
    let deliveryDelaySeconds: TimeInterval?
    let repeatIntervalSeconds: TimeInterval?
    let preserveExisting: Bool?
    let notificationActionIdentifier: String?
    let notificationActionTitle: String?
    let notificationActionDeferralSeconds: TimeInterval?
    let updateAvailable: Bool?
    let runnerState: String?
    let runnerDetail: String?
}

private final class TrayApplicationDelegate: NSObject,
    NSApplicationDelegate,
    UNUserNotificationCenterDelegate,
    @unchecked Sendable {
    private var statusItem: NSStatusItem?
    private var explorerURL: URL?
    private var openItem: NSMenuItem?
    private var copyItem: NSMenuItem?
    private var runnerStatusItem: NSMenuItem?
    private var updateItem: NSMenuItem?
    private var connection: DesktopConnection?

    func applicationDidFinishLaunching(_ notification: Notification) {
        configureStatusItem()
        configureNotifications()
        startCommandReader()
    }

    private func configureStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button,
           let iconURL = Bundle.main.url(forResource: "ansight-icon", withExtension: "png"),
           let image = NSImage(contentsOf: iconURL) {
            image.size = NSSize(width: 18, height: 18)
            image.isTemplate = true
            button.image = image
            button.imagePosition = .imageOnly
            button.toolTip = "Ansight"
        }

        let menu = NSMenu()
        let openItem = NSMenuItem(
            title: "Open Ansight",
            action: #selector(openAnsight),
            keyEquivalent: "")
        openItem.target = self
        openItem.isEnabled = false
        menu.addItem(openItem)

        let copyItem = NSMenuItem(
            title: "Copy Local URL",
            action: #selector(copyLocalURL),
            keyEquivalent: "")
        copyItem.target = self
        copyItem.isEnabled = false
        menu.addItem(copyItem)
        menu.addItem(.separator())

        let runnerStatusItem = NSMenuItem(
            title: "Remote runner: Disabled",
            action: nil,
            keyEquivalent: "")
        runnerStatusItem.isEnabled = false
        menu.addItem(runnerStatusItem)
        menu.addItem(.separator())

        let updateItem = NSMenuItem(
            title: "Update Ansight",
            action: #selector(updateAnsight),
            keyEquivalent: "")
        updateItem.target = self
        updateItem.isHidden = true
        menu.addItem(updateItem)

        let restartItem = NSMenuItem(
            title: "Restart Ansight",
            action: #selector(restartAnsight),
            keyEquivalent: "")
        restartItem.target = self
        menu.addItem(restartItem)

        let stopItem = NSMenuItem(
            title: "Stop Ansight",
            action: #selector(stopAnsight),
            keyEquivalent: "")
        stopItem.target = self
        menu.addItem(stopItem)

        item.menu = menu
        statusItem = item
        self.openItem = openItem
        self.copyItem = copyItem
        self.runnerStatusItem = runnerStatusItem
        self.updateItem = updateItem
    }

    private func configureNotifications() {
        UNUserNotificationCenter.current().delegate = self
    }

    private func startCommandReader() {
        if let configuration = DesktopConnectionConfiguration.fromProcessArguments() {
            do {
                let connection = try DesktopConnection(configuration: configuration)
                self.connection = connection
                connection.readCommands(
                    onCommand: { [weak self] command in
                        DispatchQueue.main.async {
                            self?.handle(command)
                        }
                    },
                    onEnd: {
                        DispatchQueue.main.async {
                            NSApp.terminate(nil)
                        }
                    })
            } catch {
                Self.writeError("Desktop connection failed: \(error.localizedDescription)")
                NSApp.terminate(nil)
            }
            return
        }

        readStandardInputCommands()
    }

    private func readStandardInputCommands() {
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let decoder = JSONDecoder()
            while let line = readLine() {
                guard let data = line.data(using: .utf8),
                      let command = try? decoder.decode(DesktopCommand.self, from: data),
                      command.schema == commandSchema else {
                    continue
                }

                DispatchQueue.main.async {
                    self?.handle(command)
                }
            }

            DispatchQueue.main.async {
                NSApp.terminate(nil)
            }
        }
    }

    private func handle(_ command: DesktopCommand) {
        switch command.kind {
        case "initialize":
            explorerURL = command.url.flatMap(URL.init(string:))
            openItem?.isEnabled = explorerURL != nil
            copyItem?.isEnabled = explorerURL != nil
            updateItem?.isHidden = command.updateAvailable != true
            updateItem?.isEnabled = true
        case "update-availability":
            updateItem?.isHidden = command.updateAvailable != true
            updateItem?.isEnabled = true
        case "runner-status":
            let state = command.runnerState ?? "disabled"
            runnerStatusItem?.title = "Remote runner: \(Self.displayRunnerState(state))"
            runnerStatusItem?.toolTip = command.runnerDetail
        case "notify":
            showNotification(command)
        case "remove-notification":
            removeNotification(command)
        case "shutdown":
            NSApp.terminate(nil)
        default:
            break
        }
    }

    private static func displayRunnerState(_ state: String) -> String {
        switch state.lowercased() {
        case "starting": return "Starting"
        case "idle": return "Ready"
        case "busy": return "Running a job"
        case "error": return "Needs attention"
        default: return "Disabled"
        }
    }

    private func showNotification(_ command: DesktopCommand) {
        guard let identifier = command.identifier,
              let title = command.title,
              let body = command.body else {
            return
        }

        let center = UNUserNotificationCenter.current()
        center.getNotificationSettings { settings in
            switch settings.authorizationStatus {
            case .authorized, .provisional, .ephemeral:
                Self.deliverNotification(
                    center: center,
                    identifier: identifier,
                    title: title,
                    body: body,
                    sessionId: command.sessionId,
                    deliveryDelaySeconds: command.deliveryDelaySeconds,
                    repeatIntervalSeconds: command.repeatIntervalSeconds,
                    preserveExisting: command.preserveExisting == true,
                    notificationActionIdentifier: command.notificationActionIdentifier,
                    notificationActionTitle: command.notificationActionTitle,
                    notificationActionDeferralSeconds: command.notificationActionDeferralSeconds)
            case .notDetermined:
                center.requestAuthorization(options: [.alert, .sound]) { granted, error in
                    if let error {
                        Self.writeError("Notification authorization failed: \(error.localizedDescription)")
                        return
                    }

                    if granted {
                        Self.deliverNotification(
                            center: center,
                            identifier: identifier,
                            title: title,
                            body: body,
                            sessionId: command.sessionId,
                            deliveryDelaySeconds: command.deliveryDelaySeconds,
                            repeatIntervalSeconds: command.repeatIntervalSeconds,
                            preserveExisting: command.preserveExisting == true,
                            notificationActionIdentifier: command.notificationActionIdentifier,
                            notificationActionTitle: command.notificationActionTitle,
                            notificationActionDeferralSeconds: command.notificationActionDeferralSeconds)
                    }
                }
            case .denied:
                break
            @unknown default:
                break
            }
        }
    }

    private func removeNotification(_ command: DesktopCommand) {
        guard let identifier = command.identifier else {
            return
        }

        let center = UNUserNotificationCenter.current()
        center.removePendingNotificationRequests(withIdentifiers: [identifier])
        center.removeDeliveredNotifications(withIdentifiers: [identifier])
    }

    private static func deliverNotification(
        center: UNUserNotificationCenter,
        identifier: String,
        title: String,
        body: String,
        sessionId: String?,
        deliveryDelaySeconds: TimeInterval? = nil,
        repeatIntervalSeconds: TimeInterval? = nil,
        preserveExisting: Bool = false,
        notificationActionIdentifier: String? = nil,
        notificationActionTitle: String? = nil,
        notificationActionDeferralSeconds: TimeInterval? = nil) {
        _ = registerNotificationAction(
            center: center,
            identifier: notificationActionIdentifier,
            title: notificationActionTitle)
        if preserveExisting {
            center.getPendingNotificationRequests { requests in
                if requests.contains(where: { $0.identifier == identifier }) {
                    return
                }

                deliverNotification(
                    center: center,
                    identifier: identifier,
                    title: title,
                    body: body,
                    sessionId: sessionId,
                    deliveryDelaySeconds: deliveryDelaySeconds,
                    repeatIntervalSeconds: repeatIntervalSeconds,
                    notificationActionIdentifier: notificationActionIdentifier,
                    notificationActionTitle: notificationActionTitle,
                    notificationActionDeferralSeconds: notificationActionDeferralSeconds)
            }
            return
        }

        deliverNotification(
            center: center,
            identifier: identifier,
            title: title,
            body: body,
            sessionId: sessionId,
            deliveryDelaySeconds: deliveryDelaySeconds,
            repeatIntervalSeconds: repeatIntervalSeconds,
            notificationActionIdentifier: notificationActionIdentifier,
            notificationActionTitle: notificationActionTitle,
            notificationActionDeferralSeconds: notificationActionDeferralSeconds)
    }

    private static func deliverNotification(
        center: UNUserNotificationCenter,
        identifier: String,
        title: String,
        body: String,
        sessionId: String?,
        deliveryDelaySeconds: TimeInterval?,
        repeatIntervalSeconds: TimeInterval?,
        notificationActionIdentifier: String?,
        notificationActionTitle: String?,
        notificationActionDeferralSeconds: TimeInterval?) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        content.sound = .default
        var userInfo: [AnyHashable: Any] = [:]
        if let sessionId {
            userInfo["sessionId"] = sessionId
        }
        if let actionIdentifier = notificationActionIdentifier,
           let actionDeferralSeconds = notificationActionDeferralSeconds,
           let categoryIdentifier = registerNotificationAction(
               center: center,
               identifier: actionIdentifier,
               title: notificationActionTitle) {
            content.categoryIdentifier = categoryIdentifier
            userInfo[notificationActionIdentifierKey] = actionIdentifier
            userInfo[notificationActionDeferralSecondsKey] = actionDeferralSeconds
            if let repeatIntervalSeconds {
                userInfo[notificationRepeatIntervalSecondsKey] = repeatIntervalSeconds
            }
        }
        content.userInfo = userInfo

        let trigger = deliveryDelaySeconds.map {
            UNTimeIntervalNotificationTrigger(
                timeInterval: max($0, 1),
                repeats: repeatIntervalSeconds != nil)
        }
        center.removePendingNotificationRequests(withIdentifiers: [identifier])
        center.add(UNNotificationRequest(identifier: identifier, content: content, trigger: trigger)) { error in
            if let error {
                Self.writeError("Notification delivery failed: \(error.localizedDescription)")
            }
        }
    }

    private static func registerNotificationAction(
        center: UNUserNotificationCenter,
        identifier: String?,
        title: String?) -> String? {
        guard let identifier,
              let title else {
            return nil
        }

        let categoryIdentifier = "ansight.notification.\(identifier)"
        let action = UNNotificationAction(
            identifier: identifier,
            title: title,
            options: [])
        let category = UNNotificationCategory(
            identifier: categoryIdentifier,
            actions: [action],
            intentIdentifiers: [],
            options: [])
        center.setNotificationCategories([category])
        return categoryIdentifier
    }

    @objc private func openAnsight() {
        guard let explorerURL else {
            return
        }

        NSWorkspace.shared.open(explorerURL)
    }

    @objc private func copyLocalURL() {
        guard let explorerURL else {
            return
        }

        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(explorerURL.absoluteString, forType: .string)
    }

    @objc private func stopAnsight() {
        sendDesktopAction("stop")
    }

    @objc private func restartAnsight() {
        sendDesktopAction("restart")
    }

    @objc private func updateAnsight() {
        updateItem?.isEnabled = false
        sendDesktopAction("update")
    }

    private func sendDesktopAction(_ actionName: String) {
        let action = ["schema": actionSchema, "action": actionName]
        if let connection {
            do {
                try connection.writeAction(action)
            } catch {
                Self.writeError("Desktop action failed: \(error.localizedDescription)")
            }
            return
        }

        guard let data = try? JSONSerialization.data(withJSONObject: action),
              let line = String(data: data, encoding: .utf8) else {
            return
        }
        print(line)
        fflush(stdout)
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        willPresent notification: UNNotification,
        withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list, .sound])
    }

    func userNotificationCenter(
        _ center: UNUserNotificationCenter,
        didReceive response: UNNotificationResponse,
        withCompletionHandler completionHandler: @escaping () -> Void) {
        let request = response.notification.request
        let userInfo = request.content.userInfo
        if let actionIdentifier = userInfo[notificationActionIdentifierKey] as? String,
           response.actionIdentifier == actionIdentifier,
           let deferralSeconds = userInfo[notificationActionDeferralSecondsKey] as? TimeInterval {
            let existingContent = request.content
            let content = UNMutableNotificationContent()
            content.title = existingContent.title
            content.subtitle = existingContent.subtitle
            content.body = existingContent.body
            content.sound = existingContent.sound
            content.categoryIdentifier = existingContent.categoryIdentifier
            content.userInfo = userInfo
            let repeats = userInfo[notificationRepeatIntervalSecondsKey] is TimeInterval
            let trigger = UNTimeIntervalNotificationTrigger(
                timeInterval: max(deferralSeconds, 1),
                repeats: repeats)
            center.removePendingNotificationRequests(withIdentifiers: [request.identifier])
            center.add(UNNotificationRequest(
                identifier: request.identifier,
                content: content,
                trigger: trigger)) { error in
                if let error {
                    Self.writeError("Notification deferral failed: \(error.localizedDescription)")
                }
            }
            completionHandler()
            return
        }

        openAnsight()
        completionHandler()
    }

    private static func writeError(_ message: String) {
        guard let data = (message + "\n").data(using: .utf8) else {
            return
        }

        FileHandle.standardError.write(data)
    }
}

@main
private struct AnsightTrayApplication {
    static func main() {
        let application = NSApplication.shared
        let applicationDelegate = TrayApplicationDelegate()
        application.setActivationPolicy(.accessory)
        application.delegate = applicationDelegate
        application.run()
    }
}
