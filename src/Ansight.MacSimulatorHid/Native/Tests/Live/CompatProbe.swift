import UIKit
import WebKit

@MainActor
enum Evidence {
    static var events: [[String: Any]] = []
    static var changed: ((String) -> Void)?

    static func append(_ event: [String: Any]) {
        var timestamped = event
        timestamped["capturedAtUtc"] = ISO8601DateFormatter().string(from: Date())
        events.append(timestamped)
        let path = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("events.json")
        let data = try! JSONSerialization.data(withJSONObject: events, options: [.prettyPrinted, .sortedKeys])
        try! data.write(to: path, options: .atomic)
        let lines = events.suffix(8).map { item in
            String(data: try! JSONSerialization.data(withJSONObject: item, options: [.sortedKeys]), encoding: .utf8)!
        }
        changed?(lines.joined(separator: "\n\n"))
    }
}

final class TouchView: UIView {
    override var canBecomeFirstResponder: Bool {
        true
    }

    override func pressesBegan(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        for press in presses {
            if let key = press.key {
                Evidence.append(["kind": "key", "phase": "down", "code": key.keyCode.rawValue,
                                 "characters": key.characters])
            }
        }
    }

    override func pressesEnded(_ presses: Set<UIPress>, with event: UIPressesEvent?) {
        for press in presses {
            if let key = press.key {
                Evidence.append(["kind": "key", "phase": "up", "code": key.keyCode.rawValue,
                                 "characters": key.characters])
            }
        }
    }

    func record(_ touches: Set<UITouch>, _ event: UIEvent?) {
        for touch in touches {
            Evidence.append([
                "kind": "native",
                "type": touch.type.rawValue,
                "phase": touch.phase.rawValue,
                "force": touch.force,
                "coalesced": event?.coalescedTouches(for: touch)?.count ?? 0
            ])
        }
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        record(touches, event)
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        record(touches, event)
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        record(touches, event)
    }
}

final class ProbeController: UIViewController, WKScriptMessageHandler {
    override func viewDidLoad() {
        super.viewDidLoad()
        let evidence = UILabel()
        evidence.translatesAutoresizingMaskIntoConstraints = false
        evidence.numberOfLines = 0
        evidence.font = .monospacedSystemFont(ofSize: 13, weight: .medium)
        evidence.textColor = .black
        evidence.backgroundColor = UIColor.white.withAlphaComponent(0.92)
        evidence.isUserInteractionEnabled = false
        view.addSubview(evidence)
        NSLayoutConstraint.activate([
            evidence.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 16),
            evidence.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -16),
            evidence.bottomAnchor.constraint(equalTo: view.safeAreaLayoutGuide.bottomAnchor, constant: -16)
        ])
        Evidence.changed = { [weak evidence] text in
            evidence?.text = text
        }
        Evidence.append(["kind": "display", "surface": CommandLine.arguments.contains("--web") ? "web" : "native"])
    }

    override func loadView() {
        if CommandLine.arguments.contains("--web") {
            let config = WKWebViewConfiguration()
            config.userContentController.add(self, name: "evidence")
            let web = WKWebView(frame: .zero, configuration: config)
            view = web
            web.loadHTMLString("""
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <style>
            html,body,canvas{margin:0;width:100%;height:100%;touch-action:none;background:#eef}
            input{position:absolute;left:10%;top:6%;width:80%;height:8%;font-size:24px}
            </style>
            <canvas id="canvas" tabindex="0"></canvas><input id="keyboard" aria-label="Keyboard probe"><script>
            canvas.addEventListener("pointerdown", () => canvas.focus());
            keyboard.addEventListener("focus", () => window.webkit.messageHandlers.evidence.postMessage({
              kind:"keyboard-ready", surface:"web"
            }));
            keyboard.addEventListener("input", e => window.webkit.messageHandlers.evidence.postMessage({
              kind:"text", value:keyboard.value, isTrusted:e.isTrusted
            }));
            for (const name of ["pointerdown", "pointermove", "pointerup"]) {
              canvas.addEventListener(name, e => window.webkit.messageHandlers.evidence.postMessage({
                kind:"web", phase:e.type, pointerType:e.pointerType, isTrusted:e.isTrusted,
                pressure:e.pressure, coalesced:e.getCoalescedEvents?.().length ?? 0,
                focused:document.activeElement === canvas
              }));
            }
            for (const name of ["keydown", "keyup"]) {
              document.addEventListener(name, e => window.webkit.messageHandlers.evidence.postMessage({
                kind:"key", phase:e.type, key:e.key, isTrusted:e.isTrusted
              }));
            }
            requestAnimationFrame(() => requestAnimationFrame(() => {
              window.webkit.messageHandlers.evidence.postMessage({
                kind:"ready", surface:"web", width:innerWidth, height:innerHeight
              });
            }));
            </script>
            """, baseURL: nil)
        } else {
            view = TouchView()
            view.backgroundColor = .systemTeal
            Evidence.append(["kind": "ready", "surface": "native"])
        }
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        view.becomeFirstResponder()
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        if let event = message.body as? [String: Any] {
            Evidence.append(event)
        }
    }
}

final class SceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options: UIScene.ConnectionOptions) {
        guard let scene = scene as? UIWindowScene else {
            return
        }

        let window = UIWindow(windowScene: scene)
        window.rootViewController = ProbeController()
        window.makeKeyAndVisible()
        self.window = window
    }

    func sceneDidBecomeActive(_ scene: UIScene) {
        Evidence.append(["kind": "active"])
    }

    func sceneDidEnterBackground(_ scene: UIScene) {
        Evidence.append(["kind": "background"])
    }
}

@main
final class AppDelegate: UIResponder, UIApplicationDelegate {
    func application(_ application: UIApplication, configurationForConnecting session: UISceneSession,
                     options: UIScene.ConnectionOptions) -> UISceneConfiguration {
        let config = UISceneConfiguration(name: "Probe", sessionRole: session.role)
        config.delegateClass = SceneDelegate.self
        return config
    }
}
