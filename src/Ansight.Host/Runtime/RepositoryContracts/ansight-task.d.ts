// Generated from the Ansight repository task v2 contract (v1 SDK modules remain supported).

/**
 * # Purpose and usage
 *
 * This file defines the resident host's authoring contract; it is not an executable
 * task. Executable task modules are owned by each app repository and live under
 * that repository's `ansight/tasks` directory. The host discovers them only from
 * the repository registered for the selected App ID.
 *
 * A repository task is an explicitly invoked, app-specific automation for a
 * known repeatable workflow. Use a task when a user or agent should run a named
 * cycle on demand—for example navigating to a feature, performing gestures,
 * querying custom app state, and validating the result.
 *
 * Tasks replace repeated model-driven UI decisions with deterministic local
 * code. An agent or parent task first discovers a focused task, optionally
 * inspects its input and schemas, then invokes its exact ID against the selected
 * live Ansight session. Standard host, task-composition, and app APIs are
 * available without descriptor boilerplate.
 *
 * A task's named `expect` assertions are its authoritative success evidence. A
 * normal return without an assertion is inconclusive. Tasks do not subscribe to
 * events or run in the background; use a repository trigger for that purpose.
 *
 * @packageDocumentation
 */

/** A JSON scalar accepted by the task protocol. */
export type JsonPrimitive = string | number | boolean | null;

/** A recursively JSON-serializable value accepted by the task protocol. */
export type JsonValue = JsonPrimitive | JsonObject | JsonValue[];

/** A JSON object with recursively serializable values. */
export interface JsonObject {
  /** A JSON property keyed by its serialized field name. */
  [key: string]: JsonValue;
}

/**
 * A JSON Schema object.
 *
 * Task inputs use the subset documented by Ansight. Output schemas are
 * descriptive in contract v1 and are not yet enforced against returned values.
 */
export interface JsonSchema {
  /** A JSON Schema keyword or extension value. */
  [key: string]: unknown;
}

/** Mobile platforms that may be declared by repository task target metadata. */
export type TaskPlatform = "ios" | "android";

/** Physical-device versus simulator/emulator constraints for a repository task. */
export type TaskDeviceKind = "physical" | "virtual";

/** App frameworks recognized by Ansight task targeting and session capability discovery. */
export type TaskFramework =
  | "dotnet-maui"
  | "dotnet-ios"
  | "dotnet-android"
  | "react-native"
  | "flutter"
  | "capacitor"
  | "ios-uikit"
  | "ios-swiftui"
  | "android-views"
  | "android-compose"
  | "native-unknown";

/**
 * Static metadata exported as `export const task` from an Ansight task module.
 *
 * Define a task for an on-demand, repeatable test or navigation cycle whose
 * success can be established with named assertions.
 *
 * The descriptor must be a JSON-compatible object literal. The host extracts it
 * without importing or executing the module.
 */
export interface TaskDefinition {
  /** Contract version. Omitted descriptors are interpreted as version 1. */
  schemaVersion?: 1 | 2;

  /** Version 2 requirements are checked before importing the task module. */
  requires?: ExecutionRequirements;

  /** Whether this task may be executed. Defaults to true. */
  enabled?: boolean;

  /**
   * Optional app scope. When supplied, it must exactly match the registered App ID
   * whose Agent workspace contains this module.
   */
  appId?: string;

  /** Short user-facing name returned by `ansight_list_tasks` or `ansight.tasks.list`. */
  title: string;

  /** Description of the complete repeatable cycle and its authoritative result. */
  description: string;

  /** Optional domain used by focused discovery, such as `map` or `3d-guide`. */
  feature?: string;

  /** App-specific discovery terms and synonyms. At most 32 entries are accepted. */
  keywords?: string[];

  /** Supported target platforms. Omit when the task is platform-independent. */
  platforms?: TaskPlatform[];

  /** Supported physical or virtual device kinds. Omit when either kind is supported. */
  deviceKinds?: TaskDeviceKind[];

  /** Supported app frameworks. Omit when the task is framework-independent. */
  frameworks?: TaskFramework[];

  /**
   * JSON Schema for `invocation.input`.
   *
   * The root must be an object. Contract v1 enforces `properties`, `required`,
   * `additionalProperties`, property `default`, scalar `type`, `enum`, `minimum`
   * and `maximum`.
   */
  inputSchema?: JsonSchema;

  /**
   * JSON Schema describing the task's returned value.
   *
   * This is exposed through discovery and `ansight_describe_module`. Contract
   * v1 does not yet reject a run whose returned output differs from this schema.
   */
  outputSchema?: JsonSchema;

  /**
   * Extra registered host tools used through `ansight.callTool`.
   *
   * Standard `ansight` methods need no declaration. App-provided tools called
   * through `app.callTool` also need no declaration. At most 32 entries are
   * accepted and every entry is validated when the repository is loaded.
   */
  hostTools?: string[];

  /** Whole-task timeout in seconds. Defaults to 120; accepted range is 1–300. */
  timeoutSeconds?: number;

  /** Maximum number of host, app, discovery, and child-task calls. Defaults to 64; maximum is 100. */
  maximumActions?: number;
}

/** Host-owned identity and limits for one task execution. */
export interface TaskRun {
  readonly executionMode: "sdk" | "device";
  /** Unique task run ID. */
  runId: string;

  /** Repository-relative task ID derived from the module path. */
  taskId: string;

  /** registered App ID enforced for the run. */
  appId: string;

  /** Exact live Ansight session enforced for every tool call. */
  sessionId: string;

  /** Absolute root of the connected local repository. */
  repositoryRootPath: string;

  /** Effective whole-task timeout in seconds. */
  timeoutSeconds: number;

  /** Effective host, app, discovery, and child-task call limit for the run. */
  maximumActions: number;
}

/** Common identity returned by session-bound host operations. */
export interface SessionResultIdentity {
  /** Stable identifier of the selected Ansight session. */
  sessionId: string;
  /** Application identifier associated with the session. */
  appId: string;
}

/** Selector shared by live UI queries, assertions, waits, and targeted actions. */
export interface UiSelector {
  /** Stable visual-tree node identifier. */
  nodeId?: string;
  /** Accessibility or automation identifier exposed by the app. */
  automationId?: string;
  /** Visible or accessibility text associated with the value. */
  text?: string;
  /** Normalized accessibility or UI role. */
  role?: string;
  /** Normalized type or category name. */
  type?: string;
  /** Automation identifier required on an ancestor node. */
  ancestorAutomationId?: string;
  /** Supported action required on the selected node. */
  action?: string;
  /** Whether the node is visible. */
  visible?: boolean;
  /** Whether the node is enabled for interaction. */
  enabled?: boolean;
  /** Whether string selectors require an exact match. */
  exact?: boolean;
  /** Whether string selectors use case-sensitive comparison. */
  caseSensitive?: boolean;
  /** Zero-based index used to select one matching item. */
  index?: number;
}

/** Screen-space bounds for a visual-tree node. */
export interface UiBounds {
  /** Horizontal screen coordinate. */
  x: number;
  /** Vertical screen coordinate. */
  y: number;
  /** Width in screen or image pixels. */
  width: number;
  /** Height in screen or image pixels. */
  height: number;
}

/** A normalized node returned from an Ansight visual-tree query. */
export interface UiNode {
  /** Stable identifier of the visual-tree node. */
  id?: string;
  /** Accessibility or automation identifier exposed by the app. */
  automationId?: string;
  /** Visible or accessibility text associated with the value. */
  text?: string;
  /** Value reported by the source. */
  value?: string;
  /** Normalized type or category name. */
  type?: string;
  /** Normalized accessibility or UI role. */
  role?: string;
  /** Whether the node is visible. */
  visible: boolean;
  /** Whether the node is enabled for interaction. */
  enabled: boolean;
  /** Actions reported as available for the node. */
  supportedActions: string[];
  /** Evidence source that represented this node. */
  source?: "accessibility" | "ocr" | "visualTree";
  /** Node bounds relative to the captured viewport. */
  viewportRelation?: "inside" | "partial" | "above" | "below" | "left" | "right" | "outside" | "unknown";
  /** Whether the node is effectively visible and intersects the captured viewport. */
  onScreen?: boolean;
  /** Screen-space bounds when the source exposes layout information. */
  bounds?: UiBounds | null;
  /** Depth below the visual-tree root. */
  depth: number;
  /** Nearest-first metadata for the node's ancestor path. */
  ancestorPath: Array<
    Pick<UiNode, "id" | "automationId" | "text" | "role" | "type">
  >;
}

/** Arguments accepted by the UI find operation. */
export interface UiFindArguments extends UiSelector {
  /** Maximum number of matching items to return. */
  limit?: number;
}

/** Structured result returned by the UI find operation. */
export interface UiFindResult extends SessionResultIdentity {
  /** Stable capability identifier for the operation. */
  capability: "ui.find";
  /** Exact tool identifier used for the operation. */
  toolId: string;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Normalized selector applied by the operation. */
  selector: UiSelector;
  /** How the best available evidence represented the requested target. */
  resolution: "visible" | "hidden" | "offscreen" | "notRepresented";
  /** Whether at least one result matched every requested selector constraint. */
  selectorSatisfied: boolean;
  /** Whether hidden nodes were returned separately after the requested visible selector had no matches. */
  visibilityRelaxed: boolean;
  /** Human-readable guidance for recovering from hidden, off-screen, or unrepresented evidence. */
  recoveryHint?: string | null;
  /** Values that matched every requested selector constraint. */
  matches: UiNode[];
  /** Visibility-relaxed evidence that did not satisfy the requested selector. */
  diagnosticMatches: UiNode[];
  /** Number of values represented by this entry. */
  count: number;
  /** Total matches before the result limit was applied. */
  totalMatches: number;
  /** Whether additional matching data was omitted. */
  truncated: boolean;
  /** Number of visibility-relaxed diagnostic values represented by this entry. */
  diagnosticCount: number;
  /** Total diagnostic matches before the result limit was applied. */
  totalDiagnosticMatches: number;
  /** Whether additional diagnostic matching data was omitted. */
  diagnosticTruncated: boolean;
}

/** Arguments accepted by the UI wait operation. */
export interface UiWaitArguments extends UiSelector {
  /** Condition evaluated while waiting for UI state. */
  condition?: "visible" | "hidden" | "stable";
  /** Maximum time to wait, in milliseconds. */
  timeoutMs?: number;
  /** Delay between assertion attempts, in milliseconds. */
  pollIntervalMs?: number;
  /** Consecutive equal samples required for a stable result. */
  stableSamples?: number;
}

/** Structured result returned by the UI wait operation. */
export interface UiWaitResult extends SessionResultIdentity {
  /** Stable capability identifier for the operation. */
  capability: "ui.wait_for";
  /** Condition evaluated while waiting for UI state. */
  condition: "visible" | "hidden" | "stable";
  /** Whether the requested condition was satisfied. */
  satisfied: boolean;
  /** Normalized selector applied by the operation. */
  selector: UiSelector;
  /** Number of attempts made before completing the operation. */
  attempts: number;
  /** Elapsed operation time in milliseconds. */
  elapsedMs: number;
  /** Number of values that matched the request. */
  matchCount: number;
  /** Stable hash of the associated visual tree. */
  treeHash?: string | null;
  /** App tool used to obtain the visual tree. */
  visualTreeToolId?: VisualTreeToolId;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc?: string;
  /** Matching values returned by the operation. */
  matches: UiNode[];
  /** Human-readable operation result or diagnostic message. */
  message?: string;
}

/** Arguments accepted by the UI assert operation. */
export interface UiAssertArguments extends UiSelector {
  /** Expected existence state for matching nodes. */
  exists?: boolean;
  /** Expected number of matching nodes. */
  expectedCount?: number;
  /** Text expected on the selected node. */
  expectedText?: string;
  /** Value expected on the selected node. */
  expectedValue?: string;
  /** Visibility expected on the selected node. */
  expectedVisible?: boolean;
  /** Enabled state expected on the selected node. */
  expectedEnabled?: boolean;
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId?: string;
}

/** Structured result returned by the UI assert operation. */
export interface UiAssertResult extends SessionResultIdentity {
  /** Stable capability identifier for the operation. */
  capability: "ui.assert";
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId?: string | null;
  /** Whether the assertion satisfied every requested expectation. */
  passed: boolean;
  /** App tool used to obtain the visual tree. */
  visualTreeToolId: VisualTreeToolId;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Normalized selector applied by the operation. */
  selector: UiSelector;
  /** Number of values that matched the request. */
  matchCount: number;
  /** The selected matching node, when one was available. */
  selected?: UiNode | null;
  /** Descriptions of expectations that were not satisfied. */
  failures: string[];
}

/** Stable capability identifiers returned by host-owned UI actions. */
export type UiActionCapability =
  | "ui.tap"
  | "ui.type_text"
  | "ui.swipe"
  | "ui.scroll"
  | "ui.pinch"
  | "ui.sequence"
  | "ui.back"
  | "keyboard.open"
  | "keyboard.dismiss";

/** Position of captured evidence relative to its UI action. */
export type UiEvidencePhase = "before" | "after";

/** Persisted before-or-after evidence collected around a UI action. */
export interface UiActionEvidence {
  /** Evidence phase relative to the action. */
  phase?: UiEvidencePhase;
  /** Whether the host persisted the evidence to the session timeline. */
  persisted: boolean;
  /** Identifier of the persisted visual-tree snapshot. */
  visualTreeSnapshotId?: string | null;
  /** Stable hash of the associated visual tree. */
  treeHash?: string | null;
  /** Identifier of the persisted screenshot frame. */
  screenshotFrameId?: string | null;
  /** Stable hash of the persisted screenshot. */
  screenshotHash?: string | null;
  /** Human-readable operation result or diagnostic message. */
  message?: string;
}

/** Structured result returned by the UI action operation. */
export interface UiActionResult extends SessionResultIdentity {
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId: string;
  /** Stable capability identifier for the operation. */
  capability: UiActionCapability;
  /** Whether the input action was performed. */
  performed: boolean;
  /** Identifier of the device that received the input action. */
  deviceIdentifier?: string | null;
  /** App tool used to obtain the visual tree. */
  visualTreeToolId?: VisualTreeToolId;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc?: string;
  /** Host input backend used to perform the action. */
  inputBackend?: string | null;
  /** Human-readable operation result or diagnostic message. */
  message: string;
  /** Target resolved for the operation. */
  target?: UiNode | null;
  /** Evidence captured immediately before and after the action. */
  evidence: {
    /** Evidence captured before the UI action. */
    before: UiActionEvidence;
    /** Evidence captured after the UI action. */
    after: UiActionEvidence;
  };
}

/** Arguments accepted by the UI tap operation. */
export interface UiTapArguments extends UiSelector {
  /** Optional recorded viewport-normalized horizontal coordinate from 0 through 1. */
  normalizedX?: number;
  /** Optional recorded viewport-normalized vertical coordinate from 0 through 1. */
  normalizedY?: number;
  /** Absolute horizontal screen coordinate for a tap. */
  screenX?: number;
  /** Absolute vertical screen coordinate for a tap. */
  screenY?: number;
  /** Horizontal coordinate relative to the selected target. */
  targetX?: number;
  /** Vertical coordinate relative to the selected target. */
  targetY?: number;
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Arguments accepted by the UI type text operation. */
export interface UiTypeTextArguments extends UiSelector {
  /** Text to enter into the selected UI node. */
  value: string;
  /** Whether existing text should be cleared before typing. */
  replaceExisting?: boolean;
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Arguments accepted by the UI gesture operation. */
export interface UiGestureArguments extends UiSelector {
  /** Compass direction or path along which the gesture travels, for example up, W, or NE to SW. */
  orientation?: string;
  /** Direction in which the gesture travels. */
  direction?: "up" | "down" | "left" | "right";
  /** Gesture travel distance as a normalized viewport fraction. */
  length?: number;
  /** Legacy alias for length. */
  distance?: number;
  /** Gesture duration in milliseconds. */
  durationMs?: number;
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Arguments accepted by a swipe operation, including an exact recorded path. */
export interface UiSwipeArguments extends UiGestureArguments {
  /** Optional recorded viewport-normalized start X. Supply all four path coordinates together. */
  startNormalizedX?: number;
  /** Optional recorded viewport-normalized start Y. Supply all four path coordinates together. */
  startNormalizedY?: number;
  /** Optional recorded viewport-normalized end X. Supply all four path coordinates together. */
  endNormalizedX?: number;
  /** Optional recorded viewport-normalized end Y. Supply all four path coordinates together. */
  endNormalizedY?: number;
}

/** Arguments accepted by a two-contact pinch operation. */
export interface UiPinchArguments extends UiSelector {
  /** Final contact separation divided by starting separation. */
  scale: number;
  /** Optional viewport-normalized pinch center X. */
  centerNormalizedX?: number;
  /** Optional viewport-normalized pinch center Y. */
  centerNormalizedY?: number;
  /** Starting contact separation as a normalized viewport fraction. */
  startDistance?: number;
  /** Clockwise contact-axis angle in degrees. */
  angleDegrees?: number;
  /** Gesture duration in milliseconds. */
  durationMs?: number;
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** One coordinate tap in a batched UI sequence. */
export interface UiSequenceTapAction {
  kind: "tap";
  normalizedX: number;
  normalizedY: number;
  pauseAfterMs?: number;
}

/** One swipe in a batched UI sequence. */
export interface UiSequenceSwipeAction {
  kind: "swipe";
  startNormalizedX?: number;
  startNormalizedY?: number;
  endNormalizedX?: number;
  endNormalizedY?: number;
  orientation?: string;
  direction?: "up" | "down" | "left" | "right";
  length?: number;
  distance?: number;
  durationMs?: number;
  pauseAfterMs?: number;
}

/** One pinch in a batched UI sequence. */
export interface UiSequencePinchAction {
  kind: "pinch";
  scale: number;
  centerNormalizedX?: number;
  centerNormalizedY?: number;
  startDistance?: number;
  angleDegrees?: number;
  durationMs?: number;
  pauseAfterMs?: number;
}

/** One platform back action in a batched UI sequence. */
export interface UiSequenceBackAction {
  kind: "back";
  pauseAfterMs?: number;
}

/** One supported action in a batched UI sequence. */
export type UiSequenceAction =
  | UiSequenceTapAction
  | UiSequenceSwipeAction
  | UiSequencePinchAction
  | UiSequenceBackAction;

/** Arguments accepted by a batched UI sequence. */
export interface UiSequenceArguments {
  /** From 1 through 32 already-known coordinate actions. */
  actions: UiSequenceAction[];
  /** Continue after an input failure instead of stopping at that step. Defaults to false. */
  continueOnError?: boolean;
  /** Delay before final endpoint evidence capture, from 0 through 2000 milliseconds. */
  settleMs?: number;
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Per-step outcome returned by a batched UI sequence. */
export interface UiSequenceActionResult {
  index: number;
  kind: UiSequenceAction["kind"];
  performed: boolean;
  inputBackend?: string | null;
  message: string;
  pauseAfterMs: number;
}

/** Structured result returned by a batched UI sequence. */
export interface UiSequenceResult extends SessionResultIdentity {
  sequenceId: string;
  capability: "ui.sequence";
  performed: boolean;
  requestedActionCount: number;
  completedActionCount: number;
  failureCount: number;
  stoppedAtIndex?: number | null;
  message: string;
  actions: UiSequenceActionResult[];
  evidence: {
    mode: "sequenceEndpoints";
    before: UiActionEvidence;
    after: UiActionEvidence;
  };
}

/** Arguments accepted by the UI back operation. */
export interface UiBackArguments {
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Arguments accepted when focusing a text input to open the software keyboard. */
export interface KeyboardOpenArguments extends UiSelector {
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Arguments accepted when dismissing the software keyboard. */
export interface KeyboardDismissArguments {
  /** Whether the underlying host-operation response should include the final screenshot image. Repository tasks receive structured evidence only. */
  includeScreenshot?: boolean;
}

/** Structured result returned by a keyboard visibility query. */
export interface KeyboardStateResult extends SessionResultIdentity {
  /** Stable capability identifier for the operation. */
  capability: "keyboard.is_open";
  /** Whether the system software keyboard is currently visible. */
  isOpen: boolean;
  /** ISO-8601 UTC timestamp at which the keyboard state was captured. */
  capturedAtUtc: string;
  /** Evidence source used to determine keyboard visibility. */
  evidenceSource: "deviceAccessibility";
  /** Device accessibility tool used to observe the keyboard. */
  visualTreeToolId: "device.accessibility";
  /** Persisted visual-tree snapshot that supports the observation. */
  visualTreeSnapshotId?: string | null;
  /** Human-readable keyboard-state summary. */
  message: string;
}

/** Structured result returned by a keyboard open or dismiss action. */
export interface KeyboardActionResult extends UiActionResult {
  /** Stable keyboard capability identifier for the operation. */
  capability: "keyboard.open" | "keyboard.dismiss";
  /** Keyboard visibility observed after the action. */
  isOpen: boolean;
}

/** App tools supported by the host visual-tree capture operation. */
export type VisualTreeToolId =
  | "device.accessibility"
  | "dom.get_document"
  | "flutter.get_widget_tree"
  | "maui.get_visual_tree"
  | "react.get_component_tree"
  | "react.get_shadow_tree"
  | "ui.get_visual_tree";

/** Normalized roots used when capturing or persisting a visual tree. */
export type VisualTreeRootScope =
  | "currentPage"
  | "rootPage"
  | "window"
  | "root";

/** Arguments accepted by the visual tree operation. */
export interface VisualTreeArguments {
  /** Exact tool identifier used for the operation. */
  toolId?: VisualTreeToolId;
  /** Arguments forwarded to the underlying app tool. */
  arguments?: AppToolArguments;
  /** Visual-tree root scope requested from the app. */
  root?: Exclude<VisualTreeRootScope, "root">;
  /** Whether node bounds should be included. */
  includeBounds?: boolean;
  /** Whether general node properties should be included. */
  includeProperties?: boolean;
  /** Whether .NET MAUI bindable properties should be included. */
  includeBindableProperties?: boolean;
  /** Whether .NET MAUI binding contexts should be included. */
  includeBindingContexts?: boolean;
  /** Whether inactive navigation pages should be included. */
  includeInactivePages?: boolean;
  /** Whether computed web styles should be included. */
  includeComputedStyles?: boolean;
  /** Whether React component properties should be included. */
  includeProps?: boolean;
  /** Whether React component state should be included. */
  includeState?: boolean;
  /** Maximum traversal depth. */
  maxDepth?: number;
  /** Maximum number of nodes to return. */
  maxNodes?: number;
}

/** Framework override accepted by the live navigation-structure operation. */
export type NavigationStructureFramework =
  | "maui"
  | "react-native"
  | "flutter"
  | "ios-uikit"
  | "ios-swiftui"
  | "maccatalyst-uikit"
  | "maccatalyst-swiftui"
  | "macos-appkit"
  | "macos-swiftui"
  | "android-views"
  | "android-compose";

/** Arguments accepted when reading the current framework navigation hierarchy. */
export interface LiveNavigationStructureArguments {
  /** Optional framework override. Omit to select from the live app tool catalog. */
  framework?: NavigationStructureFramework;
}

/** Supported normalized visual-tree representations. */
export type VisualTreeKind =
  | "dom"
  | "flutter"
  | "maui"
  | "native"
  | "react-component"
  | "react-shadow"
  | "ui"
  | "unknown";

/** Supported compact visual-tree wire formats. */
export type VisualTreeFormat =
  | "ansight.dom.visual-tree.compact.v2"
  | "ansight.flutter.visual-tree.compact.v2"
  | "ansight.maui.visual-tree.compact.v2"
  | "ansight.native.visual-tree.compact.v2"
  | "ansight.react.visual-tree.compact.v2"
  | "ansight.visual-tree.compact.v2";

/** Runtime platforms that can produce a visual-tree payload. */
export type VisualTreeRuntimePlatform =
  | "android"
  | "dotnet"
  | "flutter"
  | "ios"
  | "linux"
  | "maccatalyst"
  | "macos"
  | "unknown"
  | "web"
  | "windows";

/** Compact metadata retained after a live visual tree is externalized to an artifact. */
export interface LiveVisualTreeResult {
  /** Normalized representation used by the captured visual tree. */
  visualTreeKind: VisualTreeKind;
  /** Compact wire format stored in the externalized artifact. */
  visualTreeFormat: VisualTreeFormat;
  /** Runtime platform that produced the captured visual tree. */
  runtimePlatform: VisualTreeRuntimePlatform;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Root scope used for visual-tree traversal. */
  rootScope: VisualTreeRootScope;
  /** Host-local path containing the complete visual-tree JSON. */
  artifactPath: string;
  /** Stable artifact discriminator for an externalized visual tree. */
  artifactKind: "visual_tree";
  /** Screenshot metadata returned with the visual tree, when available. */
  screenshot?: ScreenshotCaptureResult;
  /** Compact root metadata retained in the app-tool result. */
  rootSummary?: {
    /** Stable identifier of the root node. */
    id?: string;
    /** Normalized root node type. */
    type?: string;
    /** Human-readable root label. */
    label?: string;
    /** Number of direct children below the root. */
    childCount?: number;
  };
}

/** Normalized metadata for a visual tree persisted on the session timeline. */
export interface PersistedVisualTreePayload {
  /** Stable identifier of the selected Ansight session. */
  sessionId?: string;
  /** Application identifier associated with the session. */
  appId?: string;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Stable identifier of the captured snapshot. */
  snapshotId: string;
  /** Stable hash of the associated visual tree. */
  treeHash?: string | null;
  /** Normalized representation used by the captured visual tree. */
  visualTreeKind: VisualTreeKind;
  /** Compact wire format used by the captured visual tree. */
  visualTreeFormat: VisualTreeFormat;
  /** Runtime platform that produced the captured visual tree. */
  runtimePlatform: VisualTreeRuntimePlatform;
  /** Source that produced the value. */
  source: string;
  /** Root scope used for visual-tree traversal. */
  rootScope: VisualTreeRootScope;
  /** Maximum traversal depth used for the capture. */
  maxDepth: number;
  /** Whether general node properties were requested. */
  includeProperties: boolean;
  /** Whether .NET MAUI bindable properties were requested. */
  includeBindableProperties: boolean;
  /** Runtime-specific serialized visual-tree body. */
  payload?: JsonObject;
  /** Number of nodes represented by the payload. */
  nodeCount: number;
  /** Whether additional matching data was omitted. */
  truncated: boolean;
  /** Identifier of the screenshot frame captured with the tree. */
  screenshotFrameId?: string | null;
  /** ISO-8601 UTC timestamp of the associated screenshot. */
  screenshotCapturedAtUtc?: string | null;
  /** Identifier of the UI action associated with this evidence. */
  actionId?: string | null;
  /** UI action capability associated with this evidence. */
  actionCapability?: UiActionCapability | null;
  /** Position of this evidence relative to its UI action. */
  evidencePhase?: UiEvidencePhase | null;
  /** Stable hash of the associated screenshot. */
  screenshotHash?: string | null;
  /** Resource URI for reading the complete persisted payload. */
  resourceUri?: string;
  /** Resource URI for reading the complete persisted payload from a summary. */
  payloadResourceUri?: string;
  /** Compact summary of the persisted root node. */
  rootSummary?: JsonValue;
}

/** Arguments accepted by the visual tree snapshot operation. */
export interface VisualTreeSnapshotArguments {
  /** Stable identifier of the captured snapshot. */
  snapshotId?: string;
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc?: string;
  /** Maximum traversal depth. */
  maxDepth?: number;
}
/** Structured result returned by the visual tree snapshot operation. */
export interface VisualTreeSnapshotResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Selector field used to resolve the requested target. */
  targetSelector: "snapshotId" | "timestampUtc" | "latest";
  /** Resolved visual-tree payload. */
  visualTree: PersistedVisualTreePayload;
}
/** Arguments accepted by the visual tree search operation. */
export interface VisualTreeSearchArguments extends UiSelector {
  /** Stable identifier of the captured snapshot. */
  snapshotId?: string;
  /** Text query applied by the operation. */
  query?: string;
  /** Human-readable label. */
  label?: string;
  /** Horizontal coordinate normalized to the captured viewport. */
  normalizedX?: number;
  /** Vertical coordinate normalized to the captured viewport. */
  normalizedY?: number;
  /** Maximum number of matching items to return. */
  limit?: number;
}
/** Structured result returned by the visual tree search operation. */
export interface VisualTreeSearchResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Number of visual-tree snapshots available to search. */
  visualTreeSnapshotCount: number;
  /** Number of matches included in this result. */
  returnedMatchCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Matching values returned by the operation. */
  matches: JsonValue[];
}

/** Author metadata associated with an imported or shared session. */
export interface SessionAuthor {
  /** Author email address. */
  email?: string | null;
  /** Human-readable name. */
  name?: string | null;
  /** Author company or organization. */
  company?: string | null;
}

/** Normalized form factors reported by supported Ansight SDKs. */
export type DeviceFormFactor =
  | "phone"
  | "tablet"
  | "desktop"
  | "tv"
  | "watch"
  | "car"
  | "vr"
  | "unknown";

/** Normalized platform keys used by session review APIs. */
export type SessionPlatformKey =
  | "android"
  | "ios"
  | "macos"
  | "windows"
  | "other";

/** Structured result returned by the session app state operation. */
export interface SessionAppStateResult extends SessionResultIdentity {
  /** Name reported by the connected client. */
  clientName: string;
  /** Network address from which the session connected. */
  remoteAddress: string;
  /** Current or terminal session status. */
  status: string;
  /** Latest reported app lifecycle state. */
  appState: "unknown" | "foreground" | "background";
  /** ISO-8601 UTC time of the latest app-state change. */
  appStateChangedUtc?: string | null;
  /** Whether the session is currently connected. */
  isLive: boolean;
  /** Whether the session is retained historical capture data. */
  isHistorical: boolean;
  /** Author metadata associated with the session. */
  author?: SessionAuthor | null;
  /** ISO-8601 UTC time at which the session was created. */
  createdUtc: string;
  /** ISO-8601 UTC time of the most recent captured activity. */
  lastUpdatedUtc: string;
  /** Configuration identifier reported by the app. */
  configId?: string | null;
  /** Ansight SDK version reported by the app. */
  sdkVersion?: string | null;
  /** Human-readable application name. */
  appName?: string | null;
  /** Application version reported by the app. */
  appVersion?: string | null;
  /** Human-readable device name. */
  deviceName?: string | null;
  /** Normalized device form factor. */
  deviceFormFactor?: DeviceFormFactor | null;
  /** Operating-system name reported by the device. */
  osName?: string | null;
  /** Whether the device is virtual. */
  isVirtual?: boolean | null;
  /** Whether the device is an emulator or simulator. */
  isEmulator?: boolean | null;
}

/** Common session metadata included by inspection results. */
export interface SessionHeader extends SessionAppStateResult {
  /** Normalized platform key used by Ansight. */
  platformKey: SessionPlatformKey;
}

/** A repository may specialize this with its known session-property groups. */
export interface SessionPropertyBag {
  /** A named custom-property group reported by the app. */
  [group: string]: JsonValue;
}

/** Structured result returned by the session properties operation. */
export interface SessionPropertiesResult<
  TProperties extends object = SessionPropertyBag,
> extends SessionResultIdentity {
  /** Whether the session is currently connected. */
  isLive: boolean;
  /** Whether the session is retained historical capture data. */
  isHistorical: boolean;
  /** The app-reported property groups, typed by the repository-supplied contract. */
  customProperties: TProperties;
}

/** Arguments accepted by the time window operation. */
export interface TimeWindowArguments {
  /** Inclusive ISO-8601 UTC start of the requested time window. */
  startUtc?: string;
  /** Inclusive ISO-8601 UTC end of the requested time window. */
  endUtc?: string;
}
/** Categories emitted by the normalized session timeline. */
export type SessionTimelineCategory =
  | "session"
  | "appState"
  | "applicationEvent"
  | "motion"
  | "networkRequest"
  | "log"
  | "screenshot"
  | "touch"
  | "annotation"
  | "visualTree"
  | "uiAction"
  | "telemetry"
  | "artifactSnapshot";

/** Arguments accepted by the session timeline operation. */
export interface SessionTimelineArguments extends TimeWindowArguments {
  /** Timeline event categories to include. */
  categories?: SessionTimelineCategory[];
  /** Maximum number of matching items to return. */
  limit?: number;
  /** Opaque cursor returned by the preceding page. */
  cursor?: string;
  /** Zero-based result offset. */
  offset?: number;
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId?: string;
}
/** One normalized event on a captured session timeline. */
export interface SessionTimelineEvent {
  /** Stable identity derived from the category, timestamp, and sequence. */
  eventId: string;
  /** ISO-8601 UTC timestamp of the timeline event. */
  timestampUtc: string;
  /** Normalized timeline category. */
  category: SessionTimelineCategory;
  /** Normalized event or artifact kind. */
  kind: string;
  /** Category-specific structured data. */
  details: JsonValue;
}
/** Structured result returned by the session timeline operation. */
export interface SessionTimelineResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId?: string | null;
  /** Number of timeline events matching the request. */
  matchedEventCount: number;
  /** Number of timeline events included in this page. */
  returnedEventCount: number;
  /** Zero-based offset represented by this page. */
  pageOffset: number;
  /** Whether another page of matching results is available. */
  hasMore: boolean;
  /** Opaque cursor for the next page. */
  nextCursor?: string | null;
  /** Timeline events included in this page. */
  events: SessionTimelineEvent[];
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
}

/** Exact HTTP status or status class accepted by network filters. */
export type NetworkStatusFilter = number | "1xx" | "2xx" | "3xx" | "4xx" | "5xx";

/** Which retained HTTP body to inspect. */
export type NetworkBodySide = "request" | "response";

/** Filters over received network capture records in the enforced session. */
export interface NetworkFilterArguments extends TimeWindowArguments {
  /** Case-insensitive exact methods; values combine with OR. Empty means all. */
  methods?: string[];
  /** Exact integer codes in 100–599 or status classes; values combine with OR. */
  statuses?: NetworkStatusFilter[];
  /** Case-insensitive substring of the URL hostname. */
  host?: string;
  /** Case-insensitive substring of URL, method, or error message; excludes bodies and headers. */
  query?: string;
  /** Keep transport errors and HTTP statuses of at least 400. False means all. */
  failedOnly?: boolean;
  /** Maximum complete summaries per page: 1–1,000; default 200. Output budgets may reduce this. */
  limit?: number;
  /** Opaque cursor for the initial query's stable snapshot; conflicts, five-minute expiry, or cache eviction reject. */
  cursor?: string;
}

/** Normalized matching criteria, excluding transport pagination controls. */
export interface NetworkFilters {
  /** Inclusive lower bound on request start time. */
  startUtc: string | null;
  /** Inclusive upper bound on request start time. */
  endUtc: string | null;
  /** Exact method filters, normalized by the host. */
  methods: string[];
  /** Exact status codes or status classes. */
  statuses: NetworkStatusFilter[];
  /** Hostname substring, or null when unrestricted. */
  host: string | null;
  /** Search text, or null when unrestricted. */
  query: string | null;
  /** Whether only transport and HTTP errors are included. */
  failedOnly: boolean;
}

/** Exact retained request identity within the enforced session. */
export interface NetworkRequestArguments {
  /** The id returned by network.get; unknown or foreign-session IDs reject. */
  requestId: string;
}

/** Arguments for a bounded preview of retained HTTP body content. */
export interface NetworkBodyReadArguments extends NetworkRequestArguments {
  /** Request or response content to inspect. */
  side: NetworkBodySide;
  /** Maximum decoded bytes: 1–65,536; default 16,384. UTF-8 boundaries are preserved. */
  maxBytes?: number;
}

/** Retained body metadata without content. */
export interface NetworkBodyMetadata {
  /** Captured MIME type, when available. */
  contentType: string | null;
  /** Encoding used for retained content and body-read data. */
  encoding: "utf8" | "base64";
  /** Number of bytes reported as retained by capture. */
  capturedBytes: number;
  /** Full body length reported by capture, or null when unknown. */
  totalBytes: number | null;
  /** Whether capture omitted content before this read. */
  truncated: boolean;
}

/** One received network capture record, excluding header values and body content. */
export interface NetworkRequestSummary {
  /** Stable retained request identifier. */
  id: string;
  /** Capture source reported by the app. */
  source: string;
  /** ISO-8601 UTC request start time. */
  startedAtUtc: string;
  /** ISO-8601 UTC request completion time. */
  completedAtUtc: string;
  /** Reported request duration, in milliseconds. */
  durationMilliseconds: number;
  /** HTTP method. */
  method: string;
  /** Captured request URL, preserving capture sanitization. */
  url: string;
  /** Reported request-body size, or null when unknown. */
  requestBodySizeBytes: number | null;
  /** Request-body metadata, or null when no body was retained. */
  requestBody: NetworkBodyMetadata | null;
  /** HTTP response status, or null when unavailable. */
  statusCode: number | null;
  /** Reported response-body size, or null when unknown. */
  responseBodySizeBytes: number | null;
  /** Response-body metadata, or null when no body was retained. */
  responseBody: NetworkBodyMetadata | null;
  /** Captured transport error type, when available. */
  errorType: string | null;
  /** Captured error message, when available. */
  errorMessage: string | null;
}

/** One captured header entry; arrays preserve repeated header names. */
export interface NetworkHeader {
  /** Captured header name. */
  name: string;
  /** Captured header value, preserving capture sanitization. */
  value: string;
}

/** Request details with bounded complete header entries and no body content. */
export interface NetworkRequest extends NetworkRequestSummary {
  /** Captured protocol, when available. */
  protocol: string | null;
  /** Captured HTTP reason phrase, when available. */
  reasonPhrase: string | null;
  /** Captured request headers, subject to the detail output budget. */
  requestHeaders: NetworkHeader[];
  /** Captured response headers, subject to the detail output budget. */
  responseHeaders: NetworkHeader[];
}

/** A page of newest-first requests from a stable matching snapshot. */
export interface NetworkRequestsResult extends SessionResultIdentity {
  /** Normalized criteria used for every page of this snapshot. */
  filters: NetworkFilters;
  /** Complete request summaries returned in this page. */
  requests: NetworkRequestSummary[];
  /** Number of matching records in the initial snapshot. */
  matchedRequestCount: number;
  /** Number of request summaries returned in this page. */
  returnedRequestCount: number;
  /** Whether matching records remain after this page. */
  isTruncated: boolean;
  /** Whether another page of this snapshot is available. */
  hasMore: boolean;
  /** Cursor for the next page, or null after the final page. */
  nextCursor: string | null;
}

/** Exact request lookup result; missing IDs reject the call. */
export interface NetworkRequestResult extends SessionResultIdentity {
  /** Retained request detail. */
  request: NetworkRequest;
  /** Whether complete header entries were omitted to satisfy output limits. */
  headersTruncated: boolean;
}

/** A bounded prefix of retained body content, with capture and read limits distinguished. */
export interface NetworkBodyRead extends NetworkBodyMetadata {
  /** Text or Base64 according to encoding; never automatically parsed as JSON. */
  data: string;
  /** Actual decoded bytes returned by this read. */
  returnedByteCount: number;
  /** Whether this read omitted any retained content, independently of capture truncation. */
  isTruncated: boolean;
}

/** Body lookup result; unavailable content is distinct from a captured empty body. */
export interface NetworkBodyReadResult extends SessionResultIdentity {
  /** Retained request identifier. */
  requestId: string;
  /** Selected side of the HTTP exchange. */
  side: NetworkBodySide;
  /** Null means unavailable; a captured empty body has data "" and zero returned bytes. */
  body: NetworkBodyRead | null;
}

/** Normalized minimum verbosity accepted by task log filters. */
export type LogVerbosity =
  | "verbose"
  | "debug"
  | "information"
  | "warning"
  | "error"
  | "fatal";

/** Normalized priority serialized on returned log records. */
export type LogPriority =
  | "Verbose"
  | "Debug"
  | "Information"
  | "Warning"
  | "Error"
  | "Fatal"
  | "Unknown";
/** Arguments accepted by the log filter operation. */
export interface LogFilterArguments extends TimeWindowArguments {
  /** Text query applied by the operation. */
  query?: string;
  /** Lowest log verbosity to include. */
  minimumVerbosity?: LogVerbosity;
  /** Log stream identifiers to include. */
  streamIds?: string[];
  /** Log tags to include. */
  tags?: string[];
  /** Log sources to include. */
  sources?: string[];
  /** Maximum number of matching items to return. */
  limit?: number;
}
/** One normalized log entry captured from an app session. */
export interface LogEntry {
  /** Identifier of the log stream that produced the entry. */
  streamId?: string | null;
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc: string;
  /** Reported log priority or verbosity. */
  priority: LogPriority;
  /** Source that produced the value. */
  source?: string | null;
  /** Tag associated with the log entry. */
  tag?: string | null;
  /** Stable identity of the source event. */
  eventId?: string | null;
  /** Operating-system process identifier. */
  processId?: number | null;
  /** Operating-system thread identifier. */
  threadId?: number | null;
  /** Human-readable operation result or diagnostic message. */
  message: string;
}
/** Structured result returned by the logs operation. */
export interface LogsResult extends SessionResultIdentity {
  /** Name reported by the connected client. */
  clientName: string;
  /** Current or terminal session status. */
  status: string;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Log entries included in the result. */
  logs: LogEntry[];
  /** Number of log entries matching the filters. */
  matchedLogCount: number;
  /** Number of log entries included in the result. */
  returnedLogCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
}
/** A log entry enriched with the session metadata needed by cross-session search. */
export interface LogSearchEntry extends LogEntry, SessionResultIdentity {
  /** Name reported by the connected client. */
  clientName: string;
  /** Human-readable application name. */
  appName?: string | null;
  /** Normalized platform key used by Ansight. */
  platformKey: SessionPlatformKey;
  /** Operating-system name reported by the device. */
  osName?: string | null;
  /** Whether the session is currently connected. */
  isLive: boolean;
}
/** Structured result returned by the log search operation. */
export interface LogSearchResult {
  /** Text query applied by the operation. */
  query: string;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of sessions searched. */
  searchedSessionCount: number;
  /** Number of sessions containing matching data. */
  matchedSessionCount: number;
  /** Number of log entries matching the filters. */
  matchedLogCount: number;
  /** Number of log entries included in the result. */
  returnedLogCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Log entries included in the result. */
  logs: LogSearchEntry[];
}
/** Arguments accepted by the log context operation. */
export interface LogContextArguments extends LogFilterArguments {
  /** Zero-based index of a target log entry. */
  logIndex?: number;
  /** Stable identity of the source event. */
  eventId?: string;
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc?: string;
  /** Number of matching items requested before the target. */
  before?: number;
  /** Number of matching items requested after the target. */
  after?: number;
}
/** Session metadata included in log review results. */
export interface LogReviewSession extends SessionResultIdentity {
  /** Name reported by the connected client. */
  clientName: string;
  /** Human-readable application name. */
  appName?: string | null;
  /** Current or terminal session status. */
  status: string;
  /** Normalized platform key used by Ansight. */
  platformKey: SessionPlatformKey;
  /** Operating-system name reported by the device. */
  osName?: string | null;
  /** Whether the session is currently connected. */
  isLive: boolean;
  /** Whether the session is retained historical capture data. */
  isHistorical: boolean;
  /** ISO-8601 UTC time at which the session was created. */
  createdUtc: string;
  /** ISO-8601 UTC time of the most recent captured activity. */
  lastUpdatedUtc: string;
  /** Number of captured log entries in the session. */
  logCount: number;
}
/** Structured result returned by the log context operation. */
export interface LogContextResult {
  /** Metadata for the session that produced the result. */
  session: LogReviewSession;
  /** Selector field used to resolve the requested target. */
  targetSelector: "logIndex" | "eventId" | "timestampUtc";
  /** Zero-based index of the resolved target log. */
  targetLogIndex: number;
  /** Number of matching items requested before the target. */
  before: number;
  /** Number of matching items requested after the target. */
  after: number;
  /** Total log entries available in the selected session. */
  totalLogCount: number;
  /** Number of log entries included in the result. */
  returnedLogCount: number;
  /** Whether matching logs exist before this context window. */
  hasEarlierLogs: boolean;
  /** Whether matching logs exist after this context window. */
  hasLaterLogs: boolean;
  /** Log entries included in the result. */
  logs: LogEntry[];
}
/** A value and occurrence count returned by log faceting. */
export interface LogFacet {
  /** Normalized facet value. */
  value: string;
  /** Number of values represented by this entry. */
  count: number;
}
/** Structured result returned by the log facets operation. */
export interface LogFacetsResult {
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of sessions searched. */
  searchedSessionCount: number;
  /** Number of sessions containing matching data. */
  matchedSessionCount: number;
  /** Number of log entries matching the filters. */
  matchedLogCount: number;
  /** Application facets represented by matching logs. */
  apps: JsonValue[];
  /** Platform facets represented by matching logs. */
  platforms: JsonValue[];
  /** Priority facets represented by matching logs. */
  priorities: JsonValue[];
  /** Log tags to include. */
  tags: LogFacet[];
  /** Log sources to include. */
  sources: LogFacet[];
  /** Sessions represented by the result. */
  sessions: LogReviewSession[];
}
/** Structured result returned by the log timeline operation. */
export interface LogTimelineResult {
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of sessions searched. */
  searchedSessionCount: number;
  /** Number of sessions containing matching data. */
  matchedSessionCount: number;
  /** Number of log entries matching the filters. */
  matchedLogCount: number;
  /** Inclusive ISO-8601 UTC start of the requested time window. */
  startUtc: string;
  /** Inclusive ISO-8601 UTC end of the requested time window. */
  endUtc: string;
  /** Number of time buckets requested or returned. */
  bucketCount: number;
  /** Time buckets included in the result. */
  buckets: JsonValue[];
}
/** Structured result returned by the log summary operation. */
export interface LogSummaryResult {
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of sessions searched. */
  searchedSessionCount: number;
  /** Number of sessions containing matching data. */
  matchedSessionCount: number;
  /** Number of log entries matching the filters. */
  matchedLogCount: number;
  /** ISO-8601 UTC timestamp of the earliest matching log. */
  firstLogUtc?: string | null;
  /** ISO-8601 UTC timestamp of the latest matching log. */
  lastLogUtc?: string | null;
  /** Counts grouped by log priority. */
  priorityCounts: JsonValue[];
  /** Counts grouped by log tag. */
  tagCounts: LogFacet[];
  /** Counts grouped by log source. */
  sourceCounts: LogFacet[];
  /** Repeated-message groups detected in the window. */
  repeatedMessages: JsonValue[];
  /** Representative or high-priority logs from the window. */
  notableLogs: LogEntry[];
  /** Sessions represented by the result. */
  sessions: LogReviewSession[];
}
/** A group of related exception-like log entries. */
export interface ExceptionGroup {
  /** Detected exception type, when one could be extracted. */
  type?: string;
  /** Normalized exception message, when one could be extracted. */
  message?: string;
  /** Number of values represented by this entry. */
  count: number;
  /** Representative samples included for this group. */
  samples?: LogEntry[];
}
/** Structured result returned by the exception extraction operation. */
export interface ExceptionExtractionResult {
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of sessions searched. */
  searchedSessionCount: number;
  /** Number of logs evaluated as exception candidates. */
  candidateLogCount: number;
  /** Maximum exception groups returned. */
  returnedGroupLimit: number;
  /** Grouped exceptions extracted from the matching logs. */
  exceptionGroups: ExceptionGroup[];
}

/** Categories accepted by the session artifact manifest operation. */
export type SessionArtifactCategory =
  | "logs"
  | "screenshots"
  | "visualTrees"
  | "annotations"
  | "telemetry"
  | "artifactSnapshots"
  | "analyses"
  | "all";

/** Arguments accepted by the session artifacts operation. */
export interface SessionArtifactsArguments extends TimeWindowArguments {
  /** Kinds or channel types to include. */
  types?: SessionArtifactCategory[];
  /** Maximum number of matching items to return. */
  limit?: number;
}
/** Arguments accepted by the nearest artifact operation. */
export interface NearestArtifactArguments {
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc?: string;
  /** Zero-based index of a target log entry. */
  logIndex?: number;
  /** Stable identity of the source event. */
  eventId?: string;
  /** Evidence window radius in seconds. */
  windowSeconds?: number;
  /** Maximum matching artifacts returned for each type. */
  limitPerType?: number;
}
/** Metadata identifying a captured artifact or artifact file. */
export interface ArtifactDescriptor {
  /** Identifier of the artifact within its provider. */
  artifactId?: string;
  /** Stable identifier of the captured snapshot. */
  snapshotId?: string;
  /** Normalized event or artifact kind. */
  kind?: string;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc?: string;
  /** Repository, database, or artifact path addressed by the operation. */
  path?: string;
  /** MIME type of the artifact content. */
  contentType?: string;
  /** Artifact size in bytes. */
  sizeBytes?: number;
}
/** Structured result returned by the session artifacts operation. */
export interface SessionArtifactsResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Counts grouped by artifact type. */
  counts: JsonValue;
  /** Log entries included in the result. */
  logs?: LogEntry[];
  /** Screenshot artifacts included in the result. */
  screenshots?: ArtifactDescriptor[];
  /** Visual-tree artifacts included in the result. */
  visualTrees?: PersistedVisualTreePayload[];
  /** Annotations included in the result. */
  annotations?: AnnotationRecord[];
  /** Telemetry data included in the result. */
  telemetry?: JsonValue[];
  /** App-provided artifact snapshots included in the result. */
  artifactSnapshots?: ArtifactDescriptor[];
  /** Analysis records included in the result. */
  analyses?: JsonValue[];
}
/** Structured result returned by the nearest artifacts operation. */
export interface NearestArtifactsResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Target resolved for the operation. */
  target: JsonValue;
  /** ISO-8601 UTC timestamp used as the evidence target. */
  targetUtc: string;
  /** Evidence window radius in seconds. */
  windowSeconds: number;
  /** Maximum matching artifacts returned for each type. */
  limitPerType: number;
  /** Log entries included in the result. */
  logs: LogEntry[];
  /** Screenshot artifacts included in the result. */
  screenshots: ArtifactDescriptor[];
  /** Visual-tree artifacts included in the result. */
  visualTrees: PersistedVisualTreePayload[];
  /** Annotations included in the result. */
  annotations: AnnotationRecord[];
  /** Telemetry samples nearest the target. */
  telemetrySamples: TelemetrySample[];
  /** App-provided artifact snapshots included in the result. */
  artifactSnapshots: ArtifactDescriptor[];
}
/** Arguments accepted by the artifact file list operation. */
export interface ArtifactFileListArguments {
  /** Stable identifier of the captured snapshot. */
  snapshotId?: string;
  /** Optional artifact path prefix used to filter entries. */
  pathPrefix?: string;
  /** Maximum number of matching items to return. */
  limit?: number;
}
/** Structured result returned by the artifact file list operation. */
export interface ArtifactFileListResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Number of artifact snapshots searched. */
  artifactSnapshotCount: number;
  /** Number of artifact entries included in the result. */
  returnedEntryCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Artifact entries included in the result. */
  entries: ArtifactDescriptor[];
}
/** Arguments accepted by the artifact file read operation. */
export interface ArtifactFileReadArguments {
  /** Stable identifier of the captured snapshot. */
  snapshotId?: string;
  /** Repository, database, or artifact path addressed by the operation. */
  path: string;
  /** Maximum number of content bytes to return. */
  maxBytes?: number;
  /** Whether content should be returned as Base64 even when it is valid text. */
  forceBase64?: boolean;
}
/** Structured result returned by the artifact file read operation. */
export interface ArtifactFileReadResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Artifact snapshot containing the selected entry. */
  snapshot: ArtifactDescriptor;
  /** Selected artifact file entry. */
  entry: ArtifactDescriptor;
  /** Complete content size in bytes. */
  byteCount: number;
  /** Number of content bytes included in the result. */
  returnedByteCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Encoding used for the returned content. */
  encoding: "utf-8" | "base64";
  /** UTF-8 decoded content when text encoding is used. */
  text?: string;
  /** Base64-encoded content when binary encoding is used. */
  base64?: string;
}

/** Image formats supported for a new screenshot capture. */
export type ScreenshotCaptureFormat = "jpeg" | "png";

/** Image formats retained by persisted screenshot frames. */
export type ScreenshotFrameFormat = ScreenshotCaptureFormat | "webp";

/** MIME types returned for persisted screenshot frames. */
export type ScreenshotMimeType =
  | "image/jpeg"
  | "image/png"
  | "image/webp";

/** Arguments accepted by the screenshot take operation. */
export interface ScreenshotTakeArguments {
  /** Arguments forwarded to the underlying app tool. */
  arguments?: AppToolArguments;
  /** Serialization or image format. */
  format?: ScreenshotCaptureFormat;
  /** Maximum image width after host-side scaling. */
  maxWidth?: number;
  /** Requested image quality. */
  quality?: number;
  /** Whether capture waits for pending screen updates. */
  afterScreenUpdates?: boolean;
  /** Whether visual-tree node identifiers are overlaid on the image. */
  annotateNodeIds?: boolean;
}
/** Structured result returned by the screenshot capture operation. */
export interface ScreenshotCaptureResult {
  /** Serialization or image format. */
  format?: ScreenshotCaptureFormat;
  /** Width in screen or image pixels. */
  width?: number;
  /** Height in screen or image pixels. */
  height?: number;
  /** Base64-encoded image content. */
  imageBase64?: string;
}
/** Arguments accepted by the screenshot frame operation. */
export interface ScreenshotFrameArguments {
  /** Stable identifier of a screenshot frame. */
  frameId?: string;
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc?: string;
  /** Whether the result should include image content. */
  includeImageContent?: boolean;
  /** Whether image content should be encoded as Base64. */
  includeImageBase64?: boolean;
}
/** Metadata for one screenshot frame captured on a session timeline. */
export interface ScreenshotFrame {
  /** Stable identifier of a screenshot frame. */
  frameId: string;
  /** Stable identifier of the selected Ansight session. */
  sessionId: string;
  /** Application identifier associated with the session. */
  appId: string;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Serialization or image format. */
  format: ScreenshotFrameFormat;
  /** MIME type of the screenshot image. */
  mimeType: ScreenshotMimeType;
  /** Width in screen or image pixels. */
  width?: number;
  /** Height in screen or image pixels. */
  height?: number;
  /** Requested image quality. */
  quality?: number;
  /** Complete content size in bytes. */
  byteCount?: number;
  /** Host-local path of the persisted screenshot file. */
  localPath?: string;
  /** Persisted screenshot file size in bytes. */
  fileSizeBytes?: number;
}
/** Structured result returned by the screenshot frame operation. */
export interface ScreenshotFrameResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Selector field used to resolve the requested target. */
  targetSelector: "frameId" | "timestampUtc" | "latest";
  /** Resolved screenshot frame metadata. */
  frame: ScreenshotFrame;
  /** Base64-encoded image content. */
  imageBase64?: string;
}
/** Arguments accepted by the screenshot assert operation. */
export interface ScreenshotAssertArguments {
  /** Screenshot frame used as the comparison baseline. */
  baselineFrameId?: string;
  /** Maximum allowed ratio of differing pixels. */
  maxDifferenceRatio?: number;
  /** Per-channel difference threshold used to classify a pixel as different. */
  pixelThreshold?: number;
  /** Maximum image width after host-side scaling. */
  maxWidth?: number;
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId?: string;
}
/** Structured result returned by the screenshot assert operation. */
export interface ScreenshotAssertResult extends SessionResultIdentity {
  /** Stable capability identifier for the operation. */
  capability: "screenshot.assert";
  /** Whether the assertion satisfied every requested expectation. */
  passed: boolean;
  /** Screenshot frame used as the comparison baseline. */
  baselineFrameId: string;
  /** Width in screen or image pixels. */
  width: number;
  /** Height in screen or image pixels. */
  height: number;
  /** Number of pixels exceeding the difference threshold. */
  differentPixelCount: number;
  /** Total number of pixels compared. */
  totalPixelCount: number;
  /** Ratio of different pixels to total compared pixels. */
  differenceRatio: number;
  /** Mean absolute color-channel difference across compared pixels. */
  meanChannelDifference: number;
  /** Per-channel difference threshold used to classify a pixel as different. */
  pixelThreshold: number;
  /** Maximum allowed ratio of differing pixels. */
  maxDifferenceRatio: number;
  /** Human-readable operation result or diagnostic message. */
  message: string;
}

/** Arguments accepted by the database assert operation. */
export interface DatabaseAssertArguments {
  /** Repository, database, or artifact path addressed by the operation. */
  path: string;
  /** Read-only SQL statement evaluated by the assertion. */
  sql: string;
  /** Expected number of rows returned by the query. */
  expectedRowCount?: number;
  /** Column whose first-row value is compared as a scalar. */
  scalarColumn?: string;
  /** Expected scalar value represented as text. */
  expectedScalar?: string;
  /** Maximum time to wait, in milliseconds. */
  timeoutMs?: number;
  /** Delay between assertion attempts, in milliseconds. */
  pollIntervalMs?: number;
  /** Maximum database rows retained in diagnostic output. */
  maxRows?: number;
  /** Caller-supplied identity used to correlate an action and its evidence. */
  actionId?: string;
}
/** Structured result returned by the database assert operation. */
export interface DatabaseAssertResult extends SessionResultIdentity {
  /** Stable capability identifier for the operation. */
  capability: "database.assert";
  /** Whether the assertion satisfied every requested expectation. */
  passed: boolean;
  /** Repository, database, or artifact path addressed by the operation. */
  path: string;
  /** Read-only SQL statement evaluated by the assertion. */
  sql: string;
  /** Expected number of rows returned by the query. */
  expectedRowCount?: number | null;
  /** Actual number of rows returned by the query. */
  actualRowCount?: number | null;
  /** Expected scalar value represented as text. */
  expectedScalar?: string | null;
  /** Actual scalar value represented as text. */
  actualScalar?: string | null;
  /** Number of attempts made before completing the operation. */
  attempts: number;
  /** Elapsed operation time in milliseconds. */
  elapsedMs: number;
  /** Human-readable operation result or diagnostic message. */
  message: string;
}

/** Arguments accepted by the telemetry operation. */
export interface TelemetryArguments extends TimeWindowArguments {
  /** Kinds or channel types to include. */
  types?: string[];
  /** Telemetry channel identifiers to include. */
  channelIds?: number[];
  /** Telemetry channel names to include. */
  channelNames?: string[];
  /** Maximum number of matching items to return. */
  limit?: number;
  /** Number of time buckets requested or returned. */
  bucketCount?: number;
}
/** Metadata describing a telemetry channel. */
export interface TelemetryChannel {
  /** Numeric telemetry channel identifier. */
  channelId: number;
  /** Human-readable name. */
  name?: string;
  /** Unit reported for telemetry channel values. */
  unit?: string;
}
/** One numeric value captured on a telemetry channel. */
export interface TelemetrySample {
  /** Numeric telemetry channel identifier. */
  channelId: number;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Numeric telemetry value captured for the channel. */
  value: number;
}
/** Structured result returned by the telemetry operation. */
export interface TelemetryResult extends SessionResultIdentity {
  /** Name reported by the connected client. */
  clientName: string;
  /** Current or terminal session status. */
  status: string;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of telemetry samples matching the request. */
  matchedSampleCount: number;
  /** Number of telemetry samples included in the result. */
  returnedSampleCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Telemetry data included in the result. */
  telemetry: JsonValue[];
}
/** Structured result returned by the telemetry timeline operation. */
export interface TelemetryTimelineResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Number of telemetry samples matching the request. */
  matchedSampleCount: number;
  /** Inclusive ISO-8601 UTC start of the requested time window. */
  startUtc: string;
  /** Inclusive ISO-8601 UTC end of the requested time window. */
  endUtc: string;
  /** Number of time buckets requested or returned. */
  bucketCount: number;
  /** Time buckets included in the result. */
  buckets: JsonValue[];
}
/** Structured result returned by the telemetry summary operation. */
export interface TelemetrySummaryResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of telemetry samples matching the request. */
  matchedSampleCount: number;
  /** Number of telemetry channels represented in the result. */
  matchedChannelCount: number;
  /** Telemetry channel metadata included in the result. */
  channels: TelemetryChannel[];
  /** Per-channel telemetry summaries. */
  summaries: JsonValue[];
}

/** Normalized touch actions emitted by the host review pipeline. */
export type TouchAction = "down" | "move" | "up" | "cancel" | "unknown";

/** Fields that can select a target touch record. */
export type TouchTargetSelector = "touchId" | "touchIndex" | "timestampUtc";

/** Arguments accepted by the touch operation. */
export interface TouchArguments extends TimeWindowArguments {
  /** Touch action kinds to include. */
  actions?: TouchAction[];
  /** Pointer counts to include. */
  pointerCounts?: number[];
  /** Maximum number of matching items to return. */
  limit?: number;
  /** Number of time buckets requested or returned. */
  bucketCount?: number;
  /** Maximum gap in milliseconds used to group touches into gestures. */
  gestureGapMs?: number;
}
/** A normalized point in a captured touch path. */
export interface TouchPoint {
  /** Horizontal screen coordinate. */
  x: number;
  /** Vertical screen coordinate. */
  y: number;
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc?: string;
}
/** One normalized touch event captured on a session timeline. */
export interface TouchRecord {
  /** Stable captured touch ID. Pass this as `touchId` to `getContext` or `getArtifacts`. */
  id: string;
  /** Zero-based index in the filtered touch stream, when this record has one. */
  touchIndex: number | null;
  /** Whether this is the selected touch in a context result. */
  isTarget: boolean;
  /** Normalized touch action reported by the source. */
  action: TouchAction;
  /** ISO-8601 UTC timestamp at which the value was captured. */
  capturedAtUtc: string;
  /** Pointer identity assigned by the captured platform. */
  pointerId: number;
  /** Index of this pointer in the captured sample. */
  pointerIndex: number;
  /** Number of active pointers in the captured sample. */
  pointerCount: number;
  /** Horizontal coordinate in `coordinateUnit`. */
  x: number;
  /** Vertical coordinate in `coordinateUnit`. */
  y: number;
  /** Horizontal coordinate normalized to the captured surface, when available. */
  normalizedX: number | null;
  /** Vertical coordinate normalized to the captured surface, when available. */
  normalizedY: number | null;
  /** Captured surface width, when supplied by the app. */
  surfaceWidth: number | null;
  /** Captured surface height, when supplied by the app. */
  surfaceHeight: number | null;
  /** Display scale for the captured surface, when supplied by the app. */
  surfaceScale: number | null;
  /** Coordinate space reported by the app, such as `window`. */
  coordinateSpace: string;
  /** Unit used by `x`, `y`, and the surface dimensions. */
  coordinateUnit: string;
  /** Native stylus or pointer sample metadata, when the SDK captured it. */
  details: TouchSampleDetails | null;
}
/** Platform-specific metadata captured for one touch sample. Null fields were not reported. */
export interface TouchSampleDetails {
  /** Input tool, such as `stylus` or `finger`. */
  tool: string | null;
  /** Sample kind, such as a coalesced or predicted stylus sample. */
  sampleKind: string | null;
  /** Apple stylus force. Compare with `maximumPossibleForce` when available. */
  force: number | null;
  /** Maximum Apple stylus force reported for the device. */
  maximumPossibleForce: number | null;
  /** Apple stylus altitude above the screen plane, in radians. */
  altitudeRadians: number | null;
  /** Apple stylus azimuth direction, in radians. */
  azimuthRadians: number | null;
  /** Apple stylus roll angle, in radians. */
  rollRadians: number | null;
  /** Bitmask of Apple properties whose values were estimated. */
  estimatedProperties: number | null;
  /** Bitmask of estimated Apple properties awaiting updates. */
  estimatedPropertiesExpectingUpdates: number | null;
  /** Apple index used to correlate an estimated-property update. */
  estimationUpdateIndex: number | null;
  /** Android pointer pressure. */
  pressure: number | null;
  /** Android stylus tilt angle, in radians. */
  tiltRadians: number | null;
  /** Android pointer orientation, in radians. */
  orientationRadians: number | null;
  /** Android hover distance when reported by the device. */
  distance: number | null;
  /** Android stylus button-state bitmask. */
  buttonState: number | null;
  /** Android contact major axis. */
  touchMajor: number | null;
  /** Android contact minor axis. */
  touchMinor: number | null;
  /** Android tool major axis. */
  toolMajor: number | null;
  /** Android tool minor axis. */
  toolMinor: number | null;
}
/** Structured result returned by the touch timeline operation. */
export interface TouchTimelineResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of touch records matching the request. */
  matchedTouchCount: number;
  /** Inclusive ISO-8601 UTC start of the requested time window. */
  startUtc: string;
  /** Inclusive ISO-8601 UTC end of the requested time window. */
  endUtc: string;
  /** Number of time buckets requested or returned. */
  bucketCount: number;
  /** Time buckets included in the result. */
  buckets: JsonValue[];
}
/** Arguments accepted by the touch context operation. */
export interface TouchContextArguments extends TouchArguments {
  /** Stable identifier of a captured touch. */
  touchId?: string;
  /** Zero-based index of a target touch. */
  touchIndex?: number;
  /** ISO-8601 UTC timestamp used to select the nearest item. */
  timestampUtc?: string;
  /** Number of matching items requested before the target. */
  before?: number;
  /** Number of matching items requested after the target. */
  after?: number;
  /** Evidence window radius around the target touch, in seconds. */
  artifactWindowSeconds?: number;
}
/** Structured result returned by the touch context operation. */
export interface TouchContextResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Selector field used to resolve the requested target. */
  targetSelector: TouchTargetSelector;
  /** Zero-based index of the resolved target touch. */
  targetTouchIndex: number;
  /** Resolved target touch record. */
  targetTouch: TouchRecord;
  /** Number of matching items requested before the target. */
  before: number;
  /** Number of matching items requested after the target. */
  after: number;
  /** Total touch records matching the filters. */
  totalMatchingTouchCount: number;
  /** Number of touch records included in the context window. */
  returnedTouchCount: number;
  /** Whether matching touches exist before this context window. */
  hasEarlierTouches: boolean;
  /** Whether matching touches exist after this context window. */
  hasLaterTouches: boolean;
  /** Touch records included in the result. */
  touches: TouchRecord[];
  /** Artifacts associated with the result or app-tool response. */
  artifacts: JsonValue;
}
/** Structured result returned by the gesture segments operation. */
export interface GestureSegmentsResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of gestures matching the request. */
  matchedGestureCount: number;
  /** Number of gestures included in the result. */
  returnedGestureCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Normalized gesture segments included in the result. */
  gestures: JsonValue[];
}
/** Structured result returned by the tap targets operation. */
export interface TapTargetsResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of taps matching the request. */
  matchedTapCount: number;
  /** Number of tap targets included in the result. */
  returnedTapCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Resolved tap-target summaries. */
  tapTargets: JsonValue[];
}
/** Structured result returned by the touch heatmap operation. */
export interface TouchHeatmapResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of touch records matching the request. */
  matchedTouchCount: number;
  /** Populated touch heatmap cells. */
  cells: JsonValue[];
  /** Number of rows in the touch heatmap. */
  rows: number;
  /** Number of columns in the touch heatmap. */
  columns: number;
}
/** Structured result returned by the dead touches operation. */
export interface DeadTouchesResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of gestures evaluated for missing responses. */
  evaluatedGestureCount: number;
  /** Number of suspected dead touches included in the result. */
  returnedDeadTouchCount: number;
  /** Response window used to classify a touch as dead. */
  responseWindowMs: number;
  /** Whether screenshot evidence is attached to suspected dead touches. */
  includeScreenshotsAsEvidence: boolean;
  /** Suspected touches that produced no observable response. */
  deadTouches: JsonValue[];
}
/** Structured result returned by the touch artifacts operation. */
export interface TouchArtifactsResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Target resolved for the operation. */
  target: JsonValue;
  /** Evidence window radius in seconds. */
  windowSeconds: number;
  /** Maximum matching artifacts returned for each type. */
  limitPerType: number;
  /** Artifacts associated with the result or app-tool response. */
  artifacts: JsonValue;
}
/** Structured result returned by the touch flow summary operation. */
export interface TouchFlowSummaryResult {
  /** Metadata for the session that produced the result. */
  session: SessionHeader;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of touch records represented by the summary. */
  touchCount: number;
  /** Number of gestures represented by the summary. */
  gestureCount: number;
  /** ISO-8601 UTC timestamp of the earliest matching touch. */
  firstTouchUtc?: string | null;
  /** ISO-8601 UTC timestamp of the latest matching touch. */
  lastTouchUtc?: string | null;
  /** Elapsed time between the first and last matching touch, in milliseconds. */
  durationMs: number;
  /** Touch counts grouped by action. */
  actionCounts: JsonValue[];
  /** Pointer counts to include. */
  pointerCounts: JsonValue[];
  /** Gesture counts grouped by normalized kind. */
  gestureKindCounts: JsonValue[];
  /** Representative gestures selected for review. */
  notableGestures: JsonValue[];
}

/** Arguments accepted by the annotation operation. */
export interface AnnotationArguments extends TimeWindowArguments {
  /** ISO-8601 UTC timestamp used as the evidence target. */
  targetUtc?: string;
  /** Text query applied to annotation labels. */
  labelQuery?: string;
  /** Stable identifier of a screenshot frame. */
  frameId?: string;
  /** Whether returned annotations must contain geometry. */
  hasGeometry?: boolean;
  /** Maximum number of matching items to return. */
  limit?: number;
}
/** A point in full-screenshot normalized coordinates, each finite and in 0–1. */
export type AnnotationPoint = {
  x: number;
  y: number;
};

/** Geometry inputs reference retained frames; the host resolves their capture timestamps. */
export interface AnnotationGeometryAnchor {
  /** Generated when omitted. */
  geometryId?: string;
  /** Screenshot frame captured in the enforced session. */
  frameId: string;
  /** Optional displayed text. */
  text?: string;
  /** Optional #RRGGBB or #AARRGGBB stroke color. */
  strokeColor?: string;
  /** Optional finite positive stroke width. */
  strokeWidth?: number;
}

/** Point annotation shape. */
export interface AnnotationPointGeometry extends AnnotationPoint {
  kind: "point";
}

/** Box annotation shape with positive dimensions contained within the screenshot. */
export interface AnnotationBoxGeometry extends AnnotationPoint {
  kind: "rectangle" | "ellipse";
  width: number;
  height: number;
}

/** Free-draw shape with at least two points; the host computes its bounds. */
export interface AnnotationFreeDrawGeometry {
  kind: "freeDraw";
  points: AnnotationPoint[];
}

/** Writable shape fields; computed timestamps, bounds, and inference are output-only. */
export type AnnotationGeometryInput = AnnotationGeometryAnchor & (
  AnnotationPointGeometry | AnnotationBoxGeometry | AnnotationFreeDrawGeometry
);

/** Existing semantic UI evidence from the enforced session. */
export interface AnnotationTargetInput {
  /** Identifier of a retained visual-tree snapshot. */
  visualTreeSnapshotId: string;
  /** Exact nodeId returned by visual-tree inspection for that snapshot. */
  nodeId: string;
}

/** Timeline annotation creation, optionally with shapes and semantic UI evidence. */
export interface AnnotationCreateArguments {
  /** Generated when omitted; an existing explicit ID rejects atomically. */
  annotationId?: string;
  /** Required non-whitespace display label. */
  label: string;
  /** Optional notes; empty/whitespace values normalize to no notes. */
  notes?: string;
  /** Defaults to task:<taskId>. Preserved by subsequent updates. */
  source?: string;
  /** UTC timestamp; defaults to the session's latest captured timestamp. */
  startUtc?: string;
  /** Optional UTC range end; must not precede start. Equality normalizes to a point. */
  endUtc?: string;
  /** Shapes anchored to retained screenshots in the enforced session. */
  geometries?: AnnotationGeometryInput[];
  /** Semantic node target resolved by the host without capturing new evidence. */
  target?: AnnotationTargetInput;
}

/** Atomic patch of the latest annotation; omitted editable fields remain unchanged. */
export interface AnnotationUpdateArguments {
  /** Exact existing annotation identifier. */
  annotationId: string;
  /** Optional atomic exact-match provenance guard; a mismatch rejects without mutation. */
  expectedSource?: string;
  /** Replacement non-whitespace label. */
  label?: string;
  /** Replacement notes; null or whitespace clears them. */
  notes?: string | null;
  /** Replacement UTC start; does not change screenshot capture timestamps. */
  startUtc?: string;
  /** Replacement UTC end; null clears the range. The resulting range must be valid. */
  endUtc?: string | null;
  /** Replaces the full shape array; [] clears shapes. */
  geometries?: AnnotationGeometryInput[];
  /** Replacement semantic target; null clears it. */
  target?: AnnotationTargetInput | null;
}

/** Delete one existing annotation, optionally guarded by its provenance. */
export interface AnnotationDeleteArguments {
  annotationId: string;
  /** Exact-match guard checked atomically with deletion. */
  expectedSource?: string;
}

/** Bounds serialized for semantic targets and inferred shapes. */
export type AnnotationBounds = AnnotationPoint & {
  width: number;
  height: number;
};

/** Host-inferred interpretation of a free-draw path. */
export type AnnotationInferredShape = {
  kind: "line" | "arrow" | "rectangle" | "oval";
  bounds: AnnotationBounds;
  /** Inference confidence from 0 to 1. */
  confidence: number;
  /** Arrow focal point, when available. */
  focalPoint?: AnnotationPoint;
};

/** Normalized saved geometry, including shapes created by other host clients. */
export type AnnotationGeometry = {
  geometryId: string;
  frameId: string;
  /** Actual screenshot capture timestamp, independent of annotation timeline placement. */
  capturedAtUtc: string;
  kind: "point" | "rectangle" | "ellipse" | "freeDraw" | "line" | "arrow";
  x: number;
  y: number;
  width: number | null;
  height: number | null;
  points: AnnotationPoint[];
  text: string | null;
  strokeColor: string | null;
  strokeWidth: number | null;
  inferredShape: AnnotationInferredShape | null;
};

/** Normalized semantic UI target saved with an annotation. */
export type AnnotationTarget = {
  kind: string;
  source: string;
  /** Resolved visual-tree node identity; inputs call this nodeId. */
  targetId: string;
  visualTreeSnapshotId: string;
  type: string;
  elementKind: string;
  label: string;
  automationId: string;
  depth: number;
  childCount: number;
  absoluteBounds: AnnotationBounds | null;
  normalizedBounds: AnnotationBounds | null;
};

/** Captured evidence attached to an annotation and preserved by patches. */
export type AnnotationEvidence = {
  id: string;
  kind: string;
  status: string;
  reason: string | null;
  capturedAtUtc: string | null;
  sizeBytes: number | null;
  truncated: boolean;
};

/** A normalized annotation returned by session inspection. */
export interface AnnotationRecord {
  /** Stable identifier of the annotation. */
  annotationId: string;
  /** Human-readable label. */
  label: string;
  /** Source that produced the value. */
  source: string;
  /** Optional human-authored annotation notes. */
  notes?: string | null;
  /** Inclusive ISO-8601 UTC start of the requested time window. */
  startUtc: string;
  /** Inclusive ISO-8601 UTC end of the requested time window. */
  endUtc?: string | null;
  /** ISO-8601 UTC end used for timeline comparisons. */
  effectiveEndUtc: string;
  /** Whether the annotation covers a time range. */
  isTimespan: boolean;
  /** Number of geometry records attached to the annotation. */
  geometryCount: number;
  /** Optional annotation target metadata. */
  target?: AnnotationTarget | null;
  /** Geometry records attached to the annotation. */
  geometries: AnnotationGeometry[];
  /** Capture grouping, preserved by updates; optional for compatibility with older hosts. */
  captureGroupId?: string | null;
  /** Attached custom data, preserved by updates. */
  customData?: JsonValue | null;
  /** Capture hook failures, preserved by updates. */
  hookFailures?: string[];
  /** Attached capture evidence, preserved by updates. */
  evidence?: AnnotationEvidence[];
}
/** Saved normalized annotation returned by create or update. */
export interface AnnotationMutationResult extends SessionResultIdentity {
  annotationId: string;
  annotation: AnnotationRecord;
}

/** Deleted normalized annotation returned after successful removal. */
export interface AnnotationDeleteResult extends SessionResultIdentity {
  annotationId: string;
  deletedAnnotation: AnnotationRecord;
}

/** Structured result returned by the annotations operation. */
export interface AnnotationsResult extends SessionResultIdentity {
  /** Name reported by the connected client. */
  clientName: string;
  /** Current or terminal session status. */
  status: string;
  /** Normalized filters applied to produce the result. */
  filters: JsonValue;
  /** Number of annotations matching the request. */
  matchedAnnotationCount: number;
  /** Number of annotations included in the result. */
  returnedAnnotationCount: number;
  /** Whether additional matching data was omitted. */
  isTruncated: boolean;
  /** Annotations included in the result. */
  annotations: AnnotationRecord[];
}

/** Metadata and argument schema published by a live app tool. */
export interface AppToolDescriptor {
  /** Exact tool identifier used for the operation. */
  toolId: string;
  /** Human-readable title. */
  title?: string;
  /** Human-readable description. */
  description?: string;
  /** JSON Schema published for the tool's arguments. */
  argumentsSchema?: JsonSchema;
}
/** Structured result returned by the app tool list operation. */
export interface AppToolListResult extends SessionResultIdentity {
  /** The tool catalog currently published by the selected live app. */
  catalog: {
    /** Tools published in the live app catalog. */
    tools?: AppToolDescriptor[];
  };
}

/** Arguments used to discover repository tasks from the caller's workspace. */
export interface TaskDiscoveryArguments {
  /** Focused words matched against task metadata and declared input values. */
  query?: string;
  /** Optional feature or domain filter. */
  feature?: string;
  /** Maximum returned tasks. Defaults to 10 and is capped at 20. */
  maxResults?: number;
}

/** Match evidence returned for a focused repository task query. */
export interface TaskDiscoveryMatch {
  /** Normalized relevance score for the complete query. */
  score: number;
  /** Fraction of query terms matched by the task. */
  coverage: number;
  /** Query terms represented by task metadata or supported fuzzy matching. */
  matchedQueryTerms: string[];
  /** Query terms not represented by the task. */
  unmatchedQueryTerms: string[];
  /** Behavioral synonym mappings used by the match. */
  synonymMatches: string[];
  /** Conservative typo mappings used by the match. */
  fuzzyMatches: string[];
}

/** One task descriptor returned by workspace discovery. */
export interface DiscoveredTask {
  /** Repository-relative task ID derived from the module path. */
  taskId: string;
  /** Task contract version. */
  schemaVersion: 1;
  /** Exact App ID enforced by the workspace. */
  appId: string;
  /** Human-readable task title. */
  title: string;
  /** Complete workflow and authoritative result description. */
  description: string;
  /** Optional discovery domain. */
  feature?: string | null;
  /** Declared discovery keywords. */
  keywords: string[];
  /** JSON Schema used to validate task input. */
  inputSchema: JsonSchema;
  /** Descriptive output schema when the task declares one. */
  outputSchema?: JsonSchema | null;
  /** Extra host tools declared by the task. */
  declaredHostTools: string[];
  /** Effective whole-task timeout. */
  timeoutSeconds: number;
  /** Effective per-task action limit. */
  maximumActions: number;
  /** Whether the task may currently execute. */
  enabled: boolean;
  /** Absolute module path reported by the local host. */
  modulePath: string;
  /** Behavioral synonyms indexed for discovery. */
  behavioralSynonyms: string[];
  /** Focused match details when a query was supplied. */
  match?: TaskDiscoveryMatch;
}

/** Repository task catalog pinned to the caller's workspace and live session. */
export interface TaskDiscoveryResult extends SessionResultIdentity {
  /** Normalized query applied by discovery. */
  query?: string | null;
  /** Normalized feature filter applied by discovery. */
  feature?: string | null;
  /** Number of task descriptors returned. */
  matchCount: number;
  /** Matching repository tasks in relevance order. */
  tasks: DiscoveredTask[];
  /** Non-fatal workspace loading diagnostics. */
  warnings: string[];
}

/** Arguments used to invoke another task in the caller's workspace. */
export interface TaskCallArguments<
  TInput extends object = Record<string, unknown>,
> {
  /** Exact task ID returned by `ansight.tasks.list`. */
  taskId: string;
  /** Input validated by the called task's declared input schema. */
  input?: TInput;
}

/** Named assertion reported by a successfully called task. */
export interface TaskCallAssertion {
  /** Stable assertion identifier declared by the called task. */
  assertionId: string;
  /** Whether the assertion passed. Successful calls contain only passing assertions. */
  passed: true;
  /** Human-readable assertion result. */
  message: string;
}

/** Bounded JSON input or output retained when task tracing is enabled. */
export interface TaskCallPayload {
  /** Serialized JSON; a truncated value may be an incomplete JSON prefix. */
  content: string;
  /** Character count before truncation, after any argument redaction. */
  originalCharacterCount: number;
  /** Whether content was shortened to the trace capture limit. */
  wasTruncated: boolean;
  /** SHA-256 of the complete captured content before truncation. */
  sha256: string;
}

/** Audited host, app, or nested-task call made by a called task. */
export interface TaskCallToolCall {
  /** One-based call sequence within the called task. */
  sequence: number;
  /** Exact host/app tool or `ansight.tasks` method name. */
  toolName: string;
  /** ISO-8601 UTC start timestamp. */
  startedAtUtc: string;
  /** ISO-8601 UTC completion timestamp when retained. */
  completedAtUtc?: string;
  /** Identifier linking the call to its host operation. */
  correlationId?: string;
  /** Time spent in the call. */
  durationMilliseconds: number;
  /** Whether the call failed before task code handled its error. */
  isError: boolean;
  /** Human-readable call result. */
  message: string;
  /** Supplied API arguments, available in retained traces. */
  arguments?: TaskCallPayload;
  /** Structured API result or error, available in retained traces. */
  result?: TaskCallPayload;
  /** Calls within a composed task, stored separately from bounded result content. */
  childCalls?: TaskCallToolCall[];
}

/** Successful result returned after another repository task passes. */
export interface TaskCallResult<TOutput = unknown> extends SessionResultIdentity {
  /** Unique run ID of the called task. */
  runId: string;
  /** Exact called task ID. */
  taskId: string;
  /** Successful calls resolve only when the called task passes. */
  status: "Passed";
  /** ISO-8601 UTC start timestamp. */
  startedAtUtc: string;
  /** ISO-8601 UTC completion timestamp. */
  completedAtUtc: string;
  /** Total called-task duration. */
  durationMilliseconds: number;
  /** Human-readable task result. */
  message: string;
  /** JSON-compatible value returned by the called task. */
  output: TOutput | null;
  /** Named assertions recorded by the called task. */
  assertions: TaskCallAssertion[];
  /** Calls audited within the called task. */
  toolCalls: TaskCallToolCall[];
  /** Bounded stderr output when the called task produced diagnostics. */
  standardError?: string;
}

/** Host-device platforms currently supported by lifecycle and location operations. */
export type HostDevicePlatform = "android" | "ios";

/** Wire values for device resolution. */
export type HostDeviceTargetSource =
  | "deviceId"
  | "hostSelection"
  | "liveSession"
  | "capturedSession";

/** Application lifecycle operations exposed to repository tasks. */
export type AppLifecycleOperation =
  | "launchApplication"
  | "foregroundApplication"
  | "backgroundApplication"
  | "terminateApplication";

/** Structured result returned by the app lifecycle operation. */
export interface AppLifecycleResult {
  /** Normalized operation that produced the result. */
  operation: AppLifecycleOperation;
  /** Whether the requested host operation succeeded. */
  isSuccess: boolean;
  /** Stable device identifier when a device was resolved. */
  deviceId?: string | null;
  /** Normalized target platform. */
  platform?: HostDevicePlatform | null;
  /** Application bundle or package identifier. */
  bundleIdentifier?: string | null;
  /** How the host resolved the target device or session. */
  targetSource: HostDeviceTargetSource;
  /** Stable identifier of the selected Ansight session. */
  sessionId?: string | null;
  /** Human-readable operation result or diagnostic message. */
  message: string;
}
/** A short speech fixture delivered to the enforced session's virtual microphone. */
export interface AudioInjectionArguments {
  /** Repository-relative WAV path. Absolute paths and links outside the repository are rejected. */
  file: string;
  /** Total operation timeout, 100–60000 milliseconds; defaults to 30000 and is capped by the task deadline. */
  timeoutMs?: number;
  /** Bounded microphone readiness wait, 0–10000 milliseconds; defaults to provider preflight without waiting (0). */
  waitForMicrophoneMs?: number;
}

/** Shared host input route used by a virtual microphone provider. */
export type AudioRoutingScope = "emulator" | "shared-host-audio-route";

/** Accepted audio format and operation bounds. */
export interface AudioFormatLimits {
  maximumDurationMs: number;
  maximumFileBytes: number;
  sampleRate: number;
  channels: number;
  bitsPerSample: number;
  maximumTimeoutMs: number;
  maximumWaitForMicrophoneMs: number;
}

/** Read-only capability/readiness diagnostics; this call does not play audio or start recording. */
export interface AudioCapabilitiesResult {
  schema: "ansight.audio-capabilities/v1";
  available: boolean;
  code: string;
  message: string;
  sessionId: string;
  deviceId: string;
  platform: string;
  backend: string;
  routingScope: AudioRoutingScope;
  limits: AudioFormatLimits;
  diagnostics: JsonObject;
}

/** Identity of the exact validated WAV bytes used for delivery. */
export interface AudioFixtureIdentity {
  sha256: string;
  durationMs: number;
  sampleRate: number;
  channels: number;
  bitsPerSample: number;
  frameCount: number;
}

/** Provider-level delivery evidence; independent of app capture or transcription. */
export interface AudioDeliveryEvidence {
  completionKind: "emulator-stream-accepted" | "host-output-played";
  submittedFrames: number;
  /** Provider invocation start, including its readiness check; not the first captured sample. */
  providerStartedUtc: string;
  completedUtc: string;
  routingScope: AudioRoutingScope;
  diagnostics: JsonObject;
}

/** Successful blocking delivery. Error results reject the task call and retain partial evidence. */
export interface AudioInjectionResult {
  schema: "ansight.audio-injection/v1";
  operationId: string;
  status: "completed";
  deliveryStarted: true;
  sessionId: string;
  deviceId: string;
  platform: "android" | "ios";
  backend: "android-emulator-grpc" | "coreaudio-loopback";
  fixture: AudioFixtureIdentity;
  delivery: AudioDeliveryEvidence;
  captureVerified: false;
  transcriptionVerified: false;
  evidenceId: string;
}

/** Starts host-managed GPX or KML playback on the enforced live session's device. */
export interface DeviceLocationPlaybackArguments {
  /** XML content, not a filesystem path. */
  routeContent: string;
  /** Display filename. Defaults to track.gpx. */
  sourceFileName?: string;
  /** Defaults to recorded; tracks without timestamps use fixed speed. */
  mode?: "recorded" | "fixed-speed";
  /** Recorded-timing multiplier (0.1–100), default 1. Does not affect fixed speed. */
  speed?: number;
  /** Fixed or fallback speed in km/h (0.5–500), default 30. */
  fixedSpeedKph?: number;
  /** Repeat until stopped or replaced. Defaults to false. */
  loop?: boolean;
}
/** Successful playback start. Later playback failures are reported by CLI location status. */
export interface DeviceLocationPlaybackResult {
  isSuccess: true;
  message: string;
  runId: string;
  sessionId: string | null;
  deviceId: string;
  platform: "ios" | "android";
  sourceFileName: string;
  pointCount: number;
  /** Effective mode, including fallback when the track lacks timestamps. */
  mode: "recorded" | "fixed-speed";
  speed: number;
  fixedSpeedKph: number;
  loop: boolean;
}

/** Arguments accepted by the device location operation. */
export interface DeviceLocationArguments {
  /** Latitude in decimal degrees. */
  latitude: number;
  /** Longitude in decimal degrees. */
  longitude: number;
}
/** Structured result returned by the device location operation. */
export interface DeviceLocationResult {
  /** Normalized operation that produced the result. */
  operation: "set";
  /** Whether the requested host operation succeeded. */
  isSuccess: boolean;
  /** How the host resolved the target device or session. */
  targetSource: Exclude<HostDeviceTargetSource, "capturedSession">;
  /** Stable identifier of the selected Ansight session. */
  sessionId?: string | null;
  /** Application identifier associated with the session. */
  appId?: string | null;
  /** Latitude in decimal degrees. */
  latitude?: number;
  /** Longitude in decimal degrees. */
  longitude?: number;
  /** Device-control backend used by the operation. */
  backend: string;
  /** Stable device identifier when a device was resolved. */
  deviceId?: string | null;
  /** Human-readable device name. */
  deviceName?: string;
  /** Normalized target platform. */
  platform?: HostDevicePlatform;
  /** Human-readable operation result or diagnostic message. */
  message: string;
}
/** Structured result returned by the device location clear operation. */
export interface DeviceLocationClearResult {
  /** Normalized operation that produced the result. */
  operation: "clear";
  /** Whether the requested host operation succeeded. */
  isSuccess: boolean;
  /** How the host resolved the target device or session. */
  targetSource: Exclude<HostDeviceTargetSource, "capturedSession">;
  /** Stable identifier of the selected Ansight session. */
  sessionId?: string | null;
  /** Application identifier associated with the session. */
  appId?: string | null;
  /** Device-control backend used by the operation. */
  backend: string;
  /** Stable device identifier when a device was resolved. */
  deviceId?: string | null;
  /** Human-readable device name. */
  deviceName?: string;
  /** Normalized target platform. */
  platform?: HostDevicePlatform;
  /** Human-readable operation result or diagnostic message. */
  message: string;
}

/** One Android Emulator accelerometer value, in metres per second squared. */
export interface AccelerometerSample {
  x: number;
  y: number;
  z: number;
  /** Time to hold this value, 10–1000 milliseconds. */
  holdMs: number;
}

/** A bounded acceleration sequence. The previous sensor value is restored on completion. */
export interface AccelerometerPlaybackArguments {
  /** 1–100 samples, held for at most 10 seconds in total. Each axis must be between -100 and 100 m/s². */
  samples: AccelerometerSample[];
}

/** Android-only options for alternating X-axis acceleration pulses. iOS Simulator accepts shake() without options. */
export interface DeviceShakeArguments {
  /** Android Emulator peak acceleration in m/s², 5–50; defaults to 20. */
  intensity?: number;
  /** Positive/negative pulse pairs, 1–8; defaults to 3. */
  repetitions?: number;
  /** Hold each pulse for 40–500 milliseconds; defaults to 80. */
  intervalMs?: number;
}

/** Host delivery result. Assert the app's response separately. */
export interface DeviceMotionResult {
  isSuccess: true;
  operation: "shake" | "playAccelerometer";
  targetSource: Exclude<HostDeviceTargetSource, "capturedSession">;
  sessionId: string | null;
  deviceId: string;
  platform: "android" | "ios";
  backend: "adb-emulator-console" | "core-simulator-darwin-notification";
  /** Accelerometer samples sent; zero for an iOS Simulator UIKit shake gesture. */
  sampleCount: number;
  /** One for an iOS Simulator UIKit shake gesture; absent for Android acceleration playback. */
  gestureCount?: number;
  durationMs: number;
  message: string;
}

/** Feature component for task app session operations. */
export interface TaskSessionContext {
  /** Read the app lifecycle state observed for this task session before interacting or asserting a transition. */
  getAppState(): Promise<SessionAppStateResult>;
  /**
   * Read grouped session properties supplied by the app. Provide TProperties for local typing;
   * it does not validate the runtime payload.
   */
  getProperties<TProperties extends object = SessionPropertyBag>(): Promise<
    SessionPropertiesResult<TProperties>
  >;
  /**
   * Correlate bounded session events across evidence categories or a time window without
   * issuing separate source queries.
   */
  getTimeline(args?: SessionTimelineArguments): Promise<SessionTimelineResult>;
}
/** Feature component for host UI operations. */
export interface TaskHostUiContext {
  /**
   * Find nodes in the current visual tree. Prefer a stable automationId; inspect matchCount and
   * matches before choosing a node.
   */
  find(args?: UiFindArguments): Promise<UiFindResult>;
  /**
   * Poll for a bounded visible, hidden, or stable UI condition. Check satisfied; a completed
   * call is not itself a task assertion.
   */
  waitFor(args?: UiWaitArguments): Promise<UiWaitResult>;
  /**
   * Evaluate a UI expectation against the current tree and retain evidence. Check passed with a
   * named expect to determine task status.
   */
  assert(args?: UiAssertArguments): Promise<UiAssertResult>;
  /**
   * Tap a selected node or coordinate and retain before-and-after evidence. Prefer a stable
   * selector over coordinates.
   */
  tap(args?: UiTapArguments): Promise<UiActionResult>;
  /** Enter text into a selected input and retain action evidence. Select the intended input explicitly. */
  typeText(args: UiTypeTextArguments): Promise<UiActionResult>;
  /** Perform a directional gesture when the drag path matters, then inspect the resulting screen. */
  swipe(args?: UiSwipeArguments): Promise<UiActionResult>;
  /** Move a scrollable view to reveal content, then find or wait for the target element. */
  scroll(args?: UiGestureArguments): Promise<UiActionResult>;
  /** Perform a two-contact zoom gesture on a selected region and inspect the resulting scale. */
  pinch(args: UiPinchArguments): Promise<UiActionResult>;
  /** Send the platform back action, then verify the destination state. */
  back(args?: UiBackArguments): Promise<UiActionResult>;
  /**
   * Batch already-known coordinate gestures when no intermediate decision is needed. Use
   * separate calls for per-step checkpoints.
   */
  runSequence(args: UiSequenceArguments): Promise<UiSequenceResult>;
  /**
   * Ask the connected app for its current visual hierarchy. Requires a live registered SDK
   * visual-tree tool; inspect the tool result envelope.
   */
  getLiveVisualTree<TResult = LiveVisualTreeResult>(
    args?: VisualTreeArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Read framework navigation state when visible nodes do not explain the route stack. Requires
   * a live compatible app tool.
   */
  getLiveNavigationStructure<TResult = Record<string, unknown>>(
    args?: LiveNavigationStructureArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /** Read a previously retained visual tree when investigating captured evidence rather than current UI. */
  getVisualTreeSnapshot(
    args?: VisualTreeSnapshotArguments,
  ): Promise<VisualTreeSnapshotResult>;
  /** Search persisted visual-tree snapshots for a node across captured evidence. */
  searchVisualTree(
    args?: VisualTreeSearchArguments,
  ): Promise<VisualTreeSearchResult>;
}
/** Feature component for host software-keyboard operations. */
export interface TaskHostKeyboardContext {
  /**
   * Focus a selected text input and verify from fresh device accessibility evidence that the
   * software keyboard opened.
   */
  open(args: KeyboardOpenArguments): Promise<KeyboardActionResult>;
  /**
   * Read current software-keyboard visibility without sending input. Requires device
   * accessibility evidence that reports keyboard presence.
   */
  isOpen(): Promise<KeyboardStateResult>;
  /** Close an open software keyboard safely. If it is already closed, no back or Escape input is sent. */
  dismiss(args?: KeyboardDismissArguments): Promise<KeyboardActionResult>;
}
/** Session-bound inspection of received network capture records. */
export interface TaskHostNetworkContext {
  /**
   * Page through retained HTTP request summaries with filters. A cursor keeps the initial
   * matching snapshot while new traffic arrives.
   */
  get(args?: NetworkFilterArguments): Promise<NetworkRequestsResult>;
  /** Read metadata and bounded headers for a request ID returned by get. Unknown IDs reject. */
  getRequest(args: NetworkRequestArguments): Promise<NetworkRequestResult>;
  /** Read a bounded request or response body prefix by ID and side. body: null means no body was retained. */
  readBody(args: NetworkBodyReadArguments): Promise<NetworkBodyReadResult>;
}
/** Feature component for host logs operations. */
export interface TaskHostLogsContext {
  /** Read bounded captured log entries for the enforced session and optional filters. */
  get(args?: LogFilterArguments): Promise<LogsResult>;
  /** Find captured log entries containing a known term, error, or correlation ID. */
  search(
    args: LogFilterArguments & { query: string },
  ): Promise<LogSearchResult>;
  /** Read entries surrounding a selected log to see what happened immediately before and after it. */
  getContext(args?: LogContextArguments): Promise<LogContextResult>;
  /** Count matching logs by available dimensions before narrowing a query. */
  getFacets(args?: LogFilterArguments): Promise<LogFacetsResult>;
  /** Aggregate matching log activity over time to locate spikes or quiet periods. */
  getTimeline(args?: LogFilterArguments): Promise<LogTimelineResult>;
  /** Summarize a selected log window instead of reading every entry. */
  summarizeWindow(args?: LogFilterArguments): Promise<LogSummaryResult>;
  /** Group exception-like logs to identify distinct failures in a window. */
  extractExceptions(
    args?: LogFilterArguments,
  ): Promise<ExceptionExtractionResult>;
}
/** Feature component for host artifacts operations. */
export interface TaskHostArtifactsContext {
  /** Find captured evidence closest to a timestamp or event when investigating a particular moment. */
  getNearest(args?: NearestArtifactArguments): Promise<NearestArtifactsResult>;
  /** List artifacts retained for the enforced session before choosing one to inspect. */
  getSession(args?: SessionArtifactsArguments): Promise<SessionArtifactsResult>;
  /** Browse entries inside a captured artifact snapshot. */
  listFiles(args?: ArtifactFileListArguments): Promise<ArtifactFileListResult>;
  /** Read a selected file from a retained artifact snapshot, not from the current app sandbox. */
  readFile(args: ArtifactFileReadArguments): Promise<ArtifactFileReadResult>;
}
/** Feature component for host screenshots operations. */
export interface TaskHostScreenshotsContext {
  /**
   * Capture the current live screen for immediate evidence. Inspect the app-tool result
   * envelope for capture failure.
   */
  take<TResult = ScreenshotCaptureResult>(
    args?: ScreenshotTakeArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /** Read a previously persisted screenshot frame after the live moment has passed. */
  getFrame(args?: ScreenshotFrameArguments): Promise<ScreenshotFrameResult>;
  /** Compare the current screen with a baseline frame and check the comparison result with a named expect. */
  assert(args?: ScreenshotAssertArguments): Promise<ScreenshotAssertResult>;
}
/** Feature component for host database operations. */
export interface TaskHostDatabaseContext {
  /** Poll a read-only database query for an expected condition when UI state alone is insufficient. */
  assert(args: DatabaseAssertArguments): Promise<DatabaseAssertResult>;
}
/** Feature component for host telemetry operations. */
export interface TaskHostTelemetryContext {
  /** Read raw captured performance samples for a selected interval or channel. */
  get(args?: TelemetryArguments): Promise<TelemetryResult>;
  /** Aggregate telemetry samples over time to compare performance around app actions. */
  getTimeline(args?: TelemetryArguments): Promise<TelemetryTimelineResult>;
  /** Get statistics for a selected telemetry window instead of processing every sample. */
  summarizeWindow(args?: TelemetryArguments): Promise<TelemetrySummaryResult>;
}
/** Feature component for host touches operations. */
export interface TaskHostTouchesContext {
  /** Read when captured touches occurred relative to other session evidence. */
  getTimeline(args?: TouchArguments): Promise<TouchTimelineResult>;
  /**
   * Inspect one touch and nearby evidence. Select it by `touchId`, `touchIndex`, or
   * `timestampUtc`; read native stylus values from `targetTouch.details` when present.
   */
  getContext(args?: TouchContextArguments): Promise<TouchContextResult>;
  /** Group captured touch points into gestures before diagnosing a swipe or drag. */
  getGestureSegments(args?: TouchArguments): Promise<GestureSegmentsResult>;
  /** Identify likely UI targets for recorded taps. */
  findTapTargets(args?: TouchArguments): Promise<TapTargetsResult>;
  /** Find screen regions with concentrated touch activity. */
  getHeatmap(args?: TouchArguments): Promise<TouchHeatmapResult>;
  /** Find touches with no apparent subsequent UI response. */
  findDead(args?: TouchArguments): Promise<DeadTouchesResult>;
  /** Retrieve screenshots or other evidence associated with captured touches. */
  getArtifacts(args?: TouchArguments): Promise<TouchArtifactsResult>;
  /** Summarize the order of captured touches and gestures for a user flow. */
  summarizeFlow(args?: TouchArguments): Promise<TouchFlowSummaryResult>;
}
/**
 * Session annotation inspection and mutations.
 *
 * Mutations reject before changing state when the full saved or deleted record
 * exceeds 480,000 serialized characters. Existing host/CLI annotation operations
 * remain available for large legacy records.
 */
export interface TaskHostAnnotationsContext {
  /** Read existing timeline and screenshot annotations before creating or changing one. */
  get(args?: AnnotationArguments): Promise<AnnotationsResult>;
  /** Add a timeline point, range, or screenshot annotation. An explicit duplicate ID rejects without changing state. */
  create(args: AnnotationCreateArguments): Promise<AnnotationMutationResult>;
  /**
   * Patch only supplied fields on an existing annotation while preserving its source and
   * attached evidence.
   */
  update(args: AnnotationUpdateArguments): Promise<AnnotationMutationResult>;
  /** Remove a known annotation; expectedSource can guard against deleting a record from another source. */
  delete(args: AnnotationDeleteArguments): Promise<AnnotationDeleteResult>;
}
/** Feature component for host app tools operations. */
export interface TaskHostAppToolsContext {
  /**
   * Discover tool IDs and availability published by the connected app before calling a standard
   * suite or custom tool.
   */
  list(): Promise<AppToolListResult>;
}
/** Feature component for workspace task discovery and composition. */
export interface TaskHostTasksContext {
  /** Discover tasks in this exact workspace and app scope by query or feature before selecting a child task ID. */
  list(args?: TaskDiscoveryArguments): Promise<TaskDiscoveryResult>;
  /**
   * Run a discovered child task on this same live session. Non-passing child status rejects;
   * the parent still needs its own named assertion.
   */
  run<TOutput = unknown, TInput extends object = Record<string, unknown>>(
    args: TaskCallArguments<TInput>,
  ): Promise<TaskCallResult<TOutput>>;
}
/** Feature component for host lifecycle operations. */
export interface TaskHostLifecycleContext {
  /**
   * Launch the selected application before a cold-start check or after a
   * permission mutation has ended its process. Verify the resulting app state.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   */
  launch(): Promise<AppLifecycleResult>;
  /**
   * Bring the selected application to the foreground before UI interaction or
   * a resume-state assertion.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   */
  foreground(): Promise<AppLifecycleResult>;
  /**
   * Press Home on a simulator or emulator to test background behavior. Use
   * foreground() to resume and assert the app's recovery.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  background(): Promise<AppLifecycleResult>;
  /**
   * Terminate the selected process when testing exit handling or a subsequent
   * cold launch; the captured session identity remains enforced.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   */
  terminate(): Promise<AppLifecycleResult>;
}
/** Feature component for host device operations. */
export interface TaskHostDeviceContext {
  /**
   * Simulate a shake on the enforced iOS Simulator or Android Emulator.
   * iOS sends one fixed UIKit shake gesture; omit Android-only intensity, repetitions, and intervalMs.
   * Android sends acceleration pulses and restores the prior sensor value.
   * Delivery is not proof that the app observed a shake; assert app behavior separately.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  shake(args?: DeviceShakeArguments): Promise<DeviceMotionResult>;
  /**
   * Play timed accelerometer samples on the enforced Android Emulator and restore its prior value.
   * The call resolves after the complete sequence and restoration.
   * This host API does not inject accelerometer samples into iOS Simulator.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  playAccelerometer(args: AccelerometerPlaybackArguments): Promise<DeviceMotionResult>;
  /**
   * Check the selected virtual device's audio provider and readiness before
   * starting a microphone test. This does not play or inject audio.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  audioCapabilities(): Promise<AudioCapabilitiesResult>;
  /**
   * Delivers a PCM16 mono 16kHz WAV, at most 15 seconds, to the already-recording virtual device. Await completion; assert app capture/transcription separately.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  injectMicrophoneAudio(args: AudioInjectionArguments): Promise<AudioInjectionResult>;
  /**
   * Set one simulated coordinate on the selected virtual device before
   * asserting location-dependent app behavior.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  setLocation(args: DeviceLocationArguments): Promise<DeviceLocationResult>;
  /**
   * Starts GPX/KML replay on the enforced live session's booted simulator/emulator.
   * Resolves after the first location is set, not when the route finishes; rejects on start failure.
   * Replaces the host's active playback and continues after the task ends, until completion,
   * replacement, host shutdown, or `ansight device location stop`. Inspect with CLI location status.
   * The host supports one active route, so coordinate tasks that use location playback.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  playLocation(args: DeviceLocationPlaybackArguments): Promise<DeviceLocationPlaybackResult>;
  /**
   * Clear a simulated coordinate on iOS Simulator during cleanup. This does
   * not stop an active location route playback.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   */
  clearLocation(): Promise<DeviceLocationClearResult>;
}

/** Shared resource names available at ansight.permissions.names. */
export interface AppPermissionNames {
  /**
   * Camera authorization.
   * Availability: Android emulators and authorized physical devices; iOS Simulator queries TCC. iOS mutations require camera in installed simctl privacy services.
   * iOS: camera. Android: android.permission.CAMERA.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   * @see https://developer.android.com/reference/android/Manifest.permission#CAMERA
   */
  readonly camera: "camera";
  /**
   * Microphone authorization.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: microphone. Android: android.permission.RECORD_AUDIO.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   * @see https://developer.android.com/reference/android/Manifest.permission#RECORD_AUDIO
   */
  readonly microphone: "microphone";
  /**
   * Contact access; the shared Android mapping grants read access only.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: contacts. Android: android.permission.READ_CONTACTS.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/contacts/accessing-the-contact-store
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CONTACTS
   */
  readonly contacts: "contacts";
  /**
   * Calendar access; only manifest-declared Android read/write permissions are used.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. EventKit full/write-only access is not separately modeled.
   * iOS: calendar. Android: android.permission.READ_CALENDAR / android.permission.WRITE_CALENDAR.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALENDAR
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALENDAR
   */
  readonly calendar: "calendar";
  /**
   * Photo-library access; limited native selections remain limited.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. Android 14+ selected access is queried and revoked, but grant does not select particular media.
   * iOS: photos. Android: android.permission.READ_MEDIA_IMAGES / android.permission.READ_MEDIA_VIDEO on API 33+ with target SDK 33+; android.permission.READ_EXTERNAL_STORAGE otherwise.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_IMAGES
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VIDEO
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  readonly photos: "photos";
  /**
   * Foreground location; only manifest-declared Android coarse/fine permissions are used.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: location. Android: android.permission.ACCESS_COARSE_LOCATION / android.permission.ACCESS_FINE_LOCATION.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestwheninuseauthorization%28%29
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  readonly location: "location";
  /**
   * Background location authorization.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. Android grants declared foreground dependencies first. On API 29+, revoke removes background access only; iOS revoke denies location entirely.
   * iOS: location-always. Android: android.permission.ACCESS_BACKGROUND_LOCATION on API 29+; android.permission.ACCESS_COARSE_LOCATION / android.permission.ACCESS_FINE_LOCATION on older Android.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestalwaysauthorization%28%29
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_BACKGROUND_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  readonly locationAlways: "locationAlways";
  /**
   * Media-library access; the Android equivalent covers shared audio only.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: media-library. Android: android.permission.READ_MEDIA_AUDIO on API 33+ with target SDK 33+; android.permission.READ_EXTERNAL_STORAGE otherwise.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/mediaplayer/mpmedialibrary/requestauthorization%28_%3A%29
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_AUDIO
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  readonly mediaLibrary: "mediaLibrary";
  /**
   * Motion/activity authorization; the Android mapping is activity recognition.
   * Availability: iOS Simulator; Android API 29+ on emulators and authorized physical devices. Unsupported on older Android.
   * iOS: motion. Android: android.permission.ACTIVITY_RECOGNITION.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/coremotion/cmmotionactivitymanager
   * @see https://developer.android.com/reference/android/Manifest.permission#ACTIVITY_RECOGNITION
   */
  readonly motion: "motion";
  /**
   * Notification authorization.
   * Availability: Android API 33+ on emulators and authorized physical devices. Unsupported on iOS and older Android.
   * iOS: unsupported (no simctl provider for notification authorization). Android: android.permission.POST_NOTIFICATIONS.
   * There is no iOS notification mutation or query backend in this provider.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications
   * @see https://developer.android.com/reference/android/Manifest.permission#POST_NOTIFICATIONS
   */
  readonly notifications: "notifications";
}

/** Frozen named constants. Import Permission from "./ansight-task.js". */
export const Permission: {
  /**
   * Camera authorization.
   * Availability: Android emulators and authorized physical devices; iOS Simulator queries TCC. iOS mutations require camera in installed simctl privacy services.
   * iOS: camera. Android: android.permission.CAMERA.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   * @see https://developer.android.com/reference/android/Manifest.permission#CAMERA
   */
  readonly Camera: "camera";
  /**
   * Microphone authorization.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: microphone. Android: android.permission.RECORD_AUDIO.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   * @see https://developer.android.com/reference/android/Manifest.permission#RECORD_AUDIO
   */
  readonly Microphone: "microphone";
  /**
   * Contact access; the shared Android mapping grants read access only.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: contacts. Android: android.permission.READ_CONTACTS.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/contacts/accessing-the-contact-store
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CONTACTS
   */
  readonly Contacts: "contacts";
  /**
   * Calendar access; only manifest-declared Android read/write permissions are used.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. EventKit full/write-only access is not separately modeled.
   * iOS: calendar. Android: android.permission.READ_CALENDAR / android.permission.WRITE_CALENDAR.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALENDAR
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALENDAR
   */
  readonly Calendar: "calendar";
  /**
   * Photo-library access; limited native selections remain limited.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. Android 14+ selected access is queried and revoked, but grant does not select particular media.
   * iOS: photos. Android: android.permission.READ_MEDIA_IMAGES / android.permission.READ_MEDIA_VIDEO on API 33+ with target SDK 33+; android.permission.READ_EXTERNAL_STORAGE otherwise.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_IMAGES
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VIDEO
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  readonly Photos: "photos";
  /**
   * Foreground location; only manifest-declared Android coarse/fine permissions are used.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: location. Android: android.permission.ACCESS_COARSE_LOCATION / android.permission.ACCESS_FINE_LOCATION.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestwheninuseauthorization%28%29
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  readonly Location: "location";
  /**
   * Background location authorization.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. Android grants declared foreground dependencies first. On API 29+, revoke removes background access only; iOS revoke denies location entirely.
   * iOS: location-always. Android: android.permission.ACCESS_BACKGROUND_LOCATION on API 29+; android.permission.ACCESS_COARSE_LOCATION / android.permission.ACCESS_FINE_LOCATION on older Android.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestalwaysauthorization%28%29
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_BACKGROUND_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  readonly LocationAlways: "locationAlways";
  /**
   * Media-library access; the Android equivalent covers shared audio only.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: media-library. Android: android.permission.READ_MEDIA_AUDIO on API 33+ with target SDK 33+; android.permission.READ_EXTERNAL_STORAGE otherwise.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/mediaplayer/mpmedialibrary/requestauthorization%28_%3A%29
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_AUDIO
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  readonly MediaLibrary: "mediaLibrary";
  /**
   * Motion/activity authorization; the Android mapping is activity recognition.
   * Availability: iOS Simulator; Android API 29+ on emulators and authorized physical devices. Unsupported on older Android.
   * iOS: motion. Android: android.permission.ACTIVITY_RECOGNITION.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/coremotion/cmmotionactivitymanager
   * @see https://developer.android.com/reference/android/Manifest.permission#ACTIVITY_RECOGNITION
   */
  readonly Motion: "motion";
  /**
   * Notification authorization.
   * Availability: Android API 33+ on emulators and authorized physical devices. Unsupported on iOS and older Android.
   * iOS: unsupported (no simctl provider for notification authorization). Android: android.permission.POST_NOTIFICATIONS.
   * There is no iOS notification mutation or query backend in this provider.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications
   * @see https://developer.android.com/reference/android/Manifest.permission#POST_NOTIFICATIONS
   */
  readonly Notifications: "notifications";
};

/** Closest native resource equivalents. Exact mappings are reported in each result. */
export type AppPermission = AppPermissionNames[keyof AppPermissionNames];

/** Shared permission value type, usable alongside the Permission constant. */
export type Permission = AppPermission;

/** Native service names available at ansight.permissions.ios.names. */
export interface IosPermissionNames {
  /**
   * Native iOS camera authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "camera". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries TCC. Mutations require camera in installed simctl services; the provider does not supply camera hardware.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   */
  readonly camera: "camera";
  /**
   * Native iOS microphone authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "microphone". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   */
  readonly microphone: "microphone";
  /**
   * Native iOS contacts authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "contacts". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/contacts/accessing-the-contact-store
   */
  readonly contacts: "contacts";
  /**
   * Native iOS contacts-limited authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "contacts-limited". Android: no native mapping; use shared Permission names for resource equivalents.
   * Requires a Simulator runtime and simctl service with limited-contact support. Does not choose specific contacts.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/contacts/cnauthorizationstatus/limited
   */
  readonly contactsLimited: "contacts-limited";
  /**
   * Native iOS calendar authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "calendar". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls the simctl calendar service; EventKit full/write-only access is not separately modeled.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   */
  readonly calendar: "calendar";
  /**
   * Native iOS photos authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "photos". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls photo-library authorization; limited user selections can remain limited.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   */
  readonly photos: "photos";
  /**
   * Native iOS photos-add authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "photos-add". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls add-only photo authorization; grants no photo-library read access.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   */
  readonly photosAdd: "photos-add";
  /**
   * Native iOS location authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "location". Android: no native mapping; use shared Permission names for resource equivalents.
   * When-in-use location. Authorization is distinct from simulated device coordinates.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestwheninuseauthorization%28%29
   */
  readonly location: "location";
  /**
   * Native iOS location-always authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "location-always". Android: no native mapping; use shared Permission names for resource equivalents.
   * Always location. Revoking this simctl service denies all location access.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestalwaysauthorization%28%29
   */
  readonly locationAlways: "location-always";
  /**
   * Native iOS media-library authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "media-library". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls media-library authorization; does not populate the Simulator with music.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/mediaplayer/mpmedialibrary/requestauthorization%28_%3A%29
   */
  readonly mediaLibrary: "media-library";
  /**
   * Native iOS motion authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "motion". Android: no native mapping; use shared Permission names for resource equivalents.
   * Authorization is distinct from hardware or motion-data availability.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/coremotion/cmmotionactivitymanager
   */
  readonly motion: "motion";
  /**
   * Native iOS reminders authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "reminders". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   */
  readonly reminders: "reminders";
  /**
   * Native iOS siri authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "siri". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls SiriKit authorization; Siri capability and app configuration are still required.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/sirikit/requesting-authorization-to-use-siri
   */
  readonly siri: "siri";
}

/** Frozen named constants. Import IosPermission from "./ansight-task.js". */
export const IosPermission: {
  /**
   * Native iOS camera authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "camera". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries TCC. Mutations require camera in installed simctl services; the provider does not supply camera hardware.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   */
  readonly Camera: "camera";
  /**
   * Native iOS microphone authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "microphone". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   */
  readonly Microphone: "microphone";
  /**
   * Native iOS contacts authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "contacts". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/contacts/accessing-the-contact-store
   */
  readonly Contacts: "contacts";
  /**
   * Native iOS contacts-limited authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "contacts-limited". Android: no native mapping; use shared Permission names for resource equivalents.
   * Requires a Simulator runtime and simctl service with limited-contact support. Does not choose specific contacts.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/contacts/cnauthorizationstatus/limited
   */
  readonly ContactsLimited: "contacts-limited";
  /**
   * Native iOS calendar authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "calendar". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls the simctl calendar service; EventKit full/write-only access is not separately modeled.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   */
  readonly Calendar: "calendar";
  /**
   * Native iOS photos authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "photos". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls photo-library authorization; limited user selections can remain limited.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   */
  readonly Photos: "photos";
  /**
   * Native iOS photos-add authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "photos-add". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls add-only photo authorization; grants no photo-library read access.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   */
  readonly PhotosAdd: "photos-add";
  /**
   * Native iOS location authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "location". Android: no native mapping; use shared Permission names for resource equivalents.
   * When-in-use location. Authorization is distinct from simulated device coordinates.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestwheninuseauthorization%28%29
   */
  readonly Location: "location";
  /**
   * Native iOS location-always authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "location-always". Android: no native mapping; use shared Permission names for resource equivalents.
   * Always location. Revoking this simctl service denies all location access.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestalwaysauthorization%28%29
   */
  readonly LocationAlways: "location-always";
  /**
   * Native iOS media-library authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "media-library". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls media-library authorization; does not populate the Simulator with music.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/mediaplayer/mpmedialibrary/requestauthorization%28_%3A%29
   */
  readonly MediaLibrary: "media-library";
  /**
   * Native iOS motion authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "motion". Android: no native mapping; use shared Permission names for resource equivalents.
   * Authorization is distinct from hardware or motion-data availability.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/coremotion/cmmotionactivitymanager
   */
  readonly Motion: "motion";
  /**
   * Native iOS reminders authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "reminders". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   */
  readonly Reminders: "reminders";
  /**
   * Native iOS siri authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "siri". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls SiriKit authorization; Siri capability and app configuration are still required.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/sirikit/requesting-authorization-to-use-siri
   */
  readonly Siri: "siri";
};

/** iOS services known to this provider. Installed simctl versions may support fewer mutations. */
export type IosPermission = IosPermissionNames[keyof IosPermissionNames];

/** Common native identifiers at ansight.permissions.android.names. Availability depends on OS and manifest. */
export interface AndroidPermissionNames {
  /**
   * Camera access.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.CAMERA. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#CAMERA
   */
  readonly camera: "android.permission.CAMERA";
  /**
   * Microphone recording.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECORD_AUDIO. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECORD_AUDIO
   */
  readonly recordAudio: "android.permission.RECORD_AUDIO";
  /**
   * Read contact records.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CONTACTS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CONTACTS
   */
  readonly readContacts: "android.permission.READ_CONTACTS";
  /**
   * Modify contact records.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CONTACTS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CONTACTS
   */
  readonly writeContacts: "android.permission.WRITE_CONTACTS";
  /**
   * Access account listings.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.GET_ACCOUNTS. iOS: no native mapping.
   * Does not sign in to accounts or grant account-provider access; signature and account-visibility rules still apply.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#GET_ACCOUNTS
   */
  readonly getAccounts: "android.permission.GET_ACCOUNTS";
  /**
   * Read calendar entries.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CALENDAR. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALENDAR
   */
  readonly readCalendar: "android.permission.READ_CALENDAR";
  /**
   * Modify calendar entries.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CALENDAR. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALENDAR
   */
  readonly writeCalendar: "android.permission.WRITE_CALENDAR";
  /**
   * Approximate foreground location.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_COARSE_LOCATION. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   */
  readonly accessCoarseLocation: "android.permission.ACCESS_COARSE_LOCATION";
  /**
   * Precise foreground location.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_FINE_LOCATION. iOS: no native mapping.
   * Approximate user choices can limit effective precision; authorization is distinct from device location settings.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  readonly accessFineLocation: "android.permission.ACCESS_FINE_LOCATION";
  /**
   * Background location.
   * Availability: Android API 29+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_BACKGROUND_LOCATION. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions. Requires declared and granted foreground coarse/fine location. Native calls do not automatically grant dependencies.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_BACKGROUND_LOCATION
   */
  readonly accessBackgroundLocation: "android.permission.ACCESS_BACKGROUND_LOCATION";
  /**
   * Read images in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_IMAGES. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_IMAGES
   */
  readonly readMediaImages: "android.permission.READ_MEDIA_IMAGES";
  /**
   * Read videos in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_VIDEO. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VIDEO
   */
  readonly readMediaVideo: "android.permission.READ_MEDIA_VIDEO";
  /**
   * Read audio in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_AUDIO. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_AUDIO
   */
  readonly readMediaAudio: "android.permission.READ_MEDIA_AUDIO";
  /**
   * Access visual media selected by the user.
   * Availability: Android API 34+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_VISUAL_USER_SELECTED. iOS: no native mapping.
   * Does not choose particular media. Use with declared READ_MEDIA_IMAGES/READ_MEDIA_VIDEO as appropriate; shared Photos revoke also clears this permission.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VISUAL_USER_SELECTED
   */
  readonly readMediaVisualUserSelected: "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
  /**
   * Legacy external-storage read access.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_EXTERNAL_STORAGE. iOS: no native mapping.
   * Legacy storage access is constrained by OS, target SDK, scoped storage, and installer policy. Use granular media permissions for target SDK 33+.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  readonly readExternalStorage: "android.permission.READ_EXTERNAL_STORAGE";
  /**
   * Legacy external-storage write access.
   * Availability: Android API 4+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_EXTERNAL_STORAGE. iOS: no native mapping.
   * Has no storage-access effect for apps targeting API 30+.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_EXTERNAL_STORAGE
   */
  readonly writeExternalStorage: "android.permission.WRITE_EXTERNAL_STORAGE";
  /**
   * Recognize physical activity.
   * Availability: Android API 29+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACTIVITY_RECOGNITION. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACTIVITY_RECOGNITION
   */
  readonly activityRecognition: "android.permission.ACTIVITY_RECOGNITION";
  /**
   * Body-sensor measurements.
   * Availability: Android API 20+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BODY_SENSORS. iOS: no native mapping.
   * Apps targeting API 36+ use granular android.permission.health identifiers for affected sensor APIs.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BODY_SENSORS
   * @see https://developer.android.com/about/versions/16/behavior-changes-16
   */
  readonly bodySensors: "android.permission.BODY_SENSORS";
  /**
   * Background body-sensor measurements.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BODY_SENSORS_BACKGROUND. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions. Requires BODY_SENSORS first. Apps targeting API 36+ use READ_HEALTH_DATA_IN_BACKGROUND for affected APIs.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BODY_SENSORS_BACKGROUND
   * @see https://developer.android.com/about/versions/16/behavior-changes-16
   */
  readonly bodySensorsBackground: "android.permission.BODY_SENSORS_BACKGROUND";
  /**
   * Post app notifications.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.POST_NOTIFICATIONS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#POST_NOTIFICATIONS
   */
  readonly postNotifications: "android.permission.POST_NOTIFICATIONS";
  /**
   * Discover nearby Bluetooth devices.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_SCAN. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_SCAN
   */
  readonly bluetoothScan: "android.permission.BLUETOOTH_SCAN";
  /**
   * Communicate with Bluetooth devices.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_CONNECT. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_CONNECT
   */
  readonly bluetoothConnect: "android.permission.BLUETOOTH_CONNECT";
  /**
   * Advertise over Bluetooth.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_ADVERTISE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_ADVERTISE
   */
  readonly bluetoothAdvertise: "android.permission.BLUETOOTH_ADVERTISE";
  /**
   * Nearby Wi-Fi device operations.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.NEARBY_WIFI_DEVICES. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#NEARBY_WIFI_DEVICES
   */
  readonly nearbyWifiDevices: "android.permission.NEARBY_WIFI_DEVICES";
  /**
   * Read telephony state.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_PHONE_STATE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_PHONE_STATE
   */
  readonly readPhoneState: "android.permission.READ_PHONE_STATE";
  /**
   * Read device phone numbers.
   * Availability: Android API 26+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_PHONE_NUMBERS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_PHONE_NUMBERS
   */
  readonly readPhoneNumbers: "android.permission.READ_PHONE_NUMBERS";
  /**
   * Place phone calls.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.CALL_PHONE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#CALL_PHONE
   */
  readonly callPhone: "android.permission.CALL_PHONE";
  /**
   * Answer incoming phone calls.
   * Availability: Android API 26+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ANSWER_PHONE_CALLS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ANSWER_PHONE_CALLS
   */
  readonly answerPhoneCalls: "android.permission.ANSWER_PHONE_CALLS";
  /**
   * Read call history.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CALL_LOG. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALL_LOG
   */
  readonly readCallLog: "android.permission.READ_CALL_LOG";
  /**
   * Modify call history.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CALL_LOG. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALL_LOG
   */
  readonly writeCallLog: "android.permission.WRITE_CALL_LOG";
  /**
   * Send SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.SEND_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#SEND_SMS
   */
  readonly sendSms: "android.permission.SEND_SMS";
  /**
   * Read SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_SMS
   */
  readonly readSms: "android.permission.READ_SMS";
  /**
   * Receive SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_SMS
   */
  readonly receiveSms: "android.permission.RECEIVE_SMS";
  /**
   * Receive MMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_MMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_MMS
   */
  readonly receiveMms: "android.permission.RECEIVE_MMS";
  /**
   * Receive WAP push messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_WAP_PUSH. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_WAP_PUSH
   */
  readonly receiveWapPush: "android.permission.RECEIVE_WAP_PUSH";
}

/** Frozen named constants. Import AndroidPermission from "./ansight-task.js". */
export const AndroidPermission: {
  /**
   * Camera access.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.CAMERA. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#CAMERA
   */
  readonly Camera: "android.permission.CAMERA";
  /**
   * Microphone recording.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECORD_AUDIO. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECORD_AUDIO
   */
  readonly RecordAudio: "android.permission.RECORD_AUDIO";
  /**
   * Read contact records.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CONTACTS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CONTACTS
   */
  readonly ReadContacts: "android.permission.READ_CONTACTS";
  /**
   * Modify contact records.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CONTACTS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CONTACTS
   */
  readonly WriteContacts: "android.permission.WRITE_CONTACTS";
  /**
   * Access account listings.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.GET_ACCOUNTS. iOS: no native mapping.
   * Does not sign in to accounts or grant account-provider access; signature and account-visibility rules still apply.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#GET_ACCOUNTS
   */
  readonly GetAccounts: "android.permission.GET_ACCOUNTS";
  /**
   * Read calendar entries.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CALENDAR. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALENDAR
   */
  readonly ReadCalendar: "android.permission.READ_CALENDAR";
  /**
   * Modify calendar entries.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CALENDAR. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALENDAR
   */
  readonly WriteCalendar: "android.permission.WRITE_CALENDAR";
  /**
   * Approximate foreground location.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_COARSE_LOCATION. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   */
  readonly AccessCoarseLocation: "android.permission.ACCESS_COARSE_LOCATION";
  /**
   * Precise foreground location.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_FINE_LOCATION. iOS: no native mapping.
   * Approximate user choices can limit effective precision; authorization is distinct from device location settings.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  readonly AccessFineLocation: "android.permission.ACCESS_FINE_LOCATION";
  /**
   * Background location.
   * Availability: Android API 29+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_BACKGROUND_LOCATION. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions. Requires declared and granted foreground coarse/fine location. Native calls do not automatically grant dependencies.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_BACKGROUND_LOCATION
   */
  readonly AccessBackgroundLocation: "android.permission.ACCESS_BACKGROUND_LOCATION";
  /**
   * Read images in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_IMAGES. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_IMAGES
   */
  readonly ReadMediaImages: "android.permission.READ_MEDIA_IMAGES";
  /**
   * Read videos in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_VIDEO. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VIDEO
   */
  readonly ReadMediaVideo: "android.permission.READ_MEDIA_VIDEO";
  /**
   * Read audio in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_AUDIO. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_AUDIO
   */
  readonly ReadMediaAudio: "android.permission.READ_MEDIA_AUDIO";
  /**
   * Access visual media selected by the user.
   * Availability: Android API 34+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_VISUAL_USER_SELECTED. iOS: no native mapping.
   * Does not choose particular media. Use with declared READ_MEDIA_IMAGES/READ_MEDIA_VIDEO as appropriate; shared Photos revoke also clears this permission.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VISUAL_USER_SELECTED
   */
  readonly ReadMediaVisualUserSelected: "android.permission.READ_MEDIA_VISUAL_USER_SELECTED";
  /**
   * Legacy external-storage read access.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_EXTERNAL_STORAGE. iOS: no native mapping.
   * Legacy storage access is constrained by OS, target SDK, scoped storage, and installer policy. Use granular media permissions for target SDK 33+.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  readonly ReadExternalStorage: "android.permission.READ_EXTERNAL_STORAGE";
  /**
   * Legacy external-storage write access.
   * Availability: Android API 4+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_EXTERNAL_STORAGE. iOS: no native mapping.
   * Has no storage-access effect for apps targeting API 30+.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_EXTERNAL_STORAGE
   */
  readonly WriteExternalStorage: "android.permission.WRITE_EXTERNAL_STORAGE";
  /**
   * Recognize physical activity.
   * Availability: Android API 29+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACTIVITY_RECOGNITION. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACTIVITY_RECOGNITION
   */
  readonly ActivityRecognition: "android.permission.ACTIVITY_RECOGNITION";
  /**
   * Body-sensor measurements.
   * Availability: Android API 20+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BODY_SENSORS. iOS: no native mapping.
   * Apps targeting API 36+ use granular android.permission.health identifiers for affected sensor APIs.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BODY_SENSORS
   * @see https://developer.android.com/about/versions/16/behavior-changes-16
   */
  readonly BodySensors: "android.permission.BODY_SENSORS";
  /**
   * Background body-sensor measurements.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BODY_SENSORS_BACKGROUND. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions. Requires BODY_SENSORS first. Apps targeting API 36+ use READ_HEALTH_DATA_IN_BACKGROUND for affected APIs.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BODY_SENSORS_BACKGROUND
   * @see https://developer.android.com/about/versions/16/behavior-changes-16
   */
  readonly BodySensorsBackground: "android.permission.BODY_SENSORS_BACKGROUND";
  /**
   * Post app notifications.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.POST_NOTIFICATIONS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#POST_NOTIFICATIONS
   */
  readonly PostNotifications: "android.permission.POST_NOTIFICATIONS";
  /**
   * Discover nearby Bluetooth devices.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_SCAN. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_SCAN
   */
  readonly BluetoothScan: "android.permission.BLUETOOTH_SCAN";
  /**
   * Communicate with Bluetooth devices.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_CONNECT. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_CONNECT
   */
  readonly BluetoothConnect: "android.permission.BLUETOOTH_CONNECT";
  /**
   * Advertise over Bluetooth.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_ADVERTISE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_ADVERTISE
   */
  readonly BluetoothAdvertise: "android.permission.BLUETOOTH_ADVERTISE";
  /**
   * Nearby Wi-Fi device operations.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.NEARBY_WIFI_DEVICES. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#NEARBY_WIFI_DEVICES
   */
  readonly NearbyWifiDevices: "android.permission.NEARBY_WIFI_DEVICES";
  /**
   * Read telephony state.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_PHONE_STATE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_PHONE_STATE
   */
  readonly ReadPhoneState: "android.permission.READ_PHONE_STATE";
  /**
   * Read device phone numbers.
   * Availability: Android API 26+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_PHONE_NUMBERS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_PHONE_NUMBERS
   */
  readonly ReadPhoneNumbers: "android.permission.READ_PHONE_NUMBERS";
  /**
   * Place phone calls.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.CALL_PHONE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#CALL_PHONE
   */
  readonly CallPhone: "android.permission.CALL_PHONE";
  /**
   * Answer incoming phone calls.
   * Availability: Android API 26+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ANSWER_PHONE_CALLS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ANSWER_PHONE_CALLS
   */
  readonly AnswerPhoneCalls: "android.permission.ANSWER_PHONE_CALLS";
  /**
   * Read call history.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CALL_LOG. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALL_LOG
   */
  readonly ReadCallLog: "android.permission.READ_CALL_LOG";
  /**
   * Modify call history.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CALL_LOG. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALL_LOG
   */
  readonly WriteCallLog: "android.permission.WRITE_CALL_LOG";
  /**
   * Send SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.SEND_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#SEND_SMS
   */
  readonly SendSms: "android.permission.SEND_SMS";
  /**
   * Read SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_SMS
   */
  readonly ReadSms: "android.permission.READ_SMS";
  /**
   * Receive SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_SMS
   */
  readonly ReceiveSms: "android.permission.RECEIVE_SMS";
  /**
   * Receive MMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_MMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_MMS
   */
  readonly ReceiveMms: "android.permission.RECEIVE_MMS";
  /**
   * Receive WAP push messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_WAP_PUSH. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_WAP_PUSH
   */
  readonly ReceiveWapPush: "android.permission.RECEIVE_WAP_PUSH";
};

/** Exact string literals for the common Android permission catalog. */
export type CommonAndroidPermission = AndroidPermissionNames[keyof AndroidPermissionNames];

/** Supports common, other system, and app-defined Android permissions declared in the manifest. */
export type AndroidPermission = CommonAndroidPermission | `${string}.${string}`;

export type PermissionStatus = "granted" | "denied" | "notDetermined" | "limited" | "unsupported" | "unknown";

export interface PermissionArguments<TPermission extends string = AppPermission> {
  /** The host enforces the task's session, device, and app; target overrides are unavailable. */
  permission: TPermission;
}

export interface NativePermissionState {
  permission: string;
  status: PermissionStatus;
}

export interface PermissionResult {
  operation: "grant" | "revoke" | "query" | "reset";
  permission: string;
  platform: "ios" | "android";
  sessionId: string;
  deviceId: string;
  bundleIdentifier: string;
  /** Whether this operation is available on the selected platform and OS/tool version. */
  supported: boolean;
  /** Mutations throw on native command failure; query can return unsupported or unknown. */
  isSuccess: boolean;
  /** Observed native state. A successful command can have unknown state if the store is unreadable. */
  status: PermissionStatus;
  backend: string;
  message: string | null;
  /** Native identifiers and their individual observed states, including partial Android grants. */
  nativePermissions: NativePermissionState[];
}

export interface NativePermissionsContext<TPermission extends string> {
  /**
   * Grant a supported app permission without prompting before testing an authorized flow.
   * Android permissions must be declared and changeable through pm.
   */
  grant(args: PermissionArguments<TPermission>): Promise<PermissionResult>;
  /**
   * Revoke a supported app permission to test denied behavior. Native changes can terminate the
   * running app; relaunch if needed.
   */
  revoke(args: PermissionArguments<TPermission>): Promise<PermissionResult>;
  /**
   * Inspect observed authorization without changing it. Check status, supported, and
   * nativePermissions; unsupported and unknown are explicit results.
   */
  query(args: PermissionArguments<TPermission>): Promise<PermissionResult>;
}

export interface TaskHostPermissionsContext extends NativePermissionsContext<AppPermission> {
  /** Readonly constants with literal types for shared resource names. */
  readonly names: AppPermissionNames;
  /**
   * Native iOS service names. iOS Simulator only; physical iOS is unsupported.
   * @supportedPlatforms ios
   * @supportedDeviceKinds virtual
   */
  readonly ios: NativePermissionsContext<IosPermission> & {
    /** Readonly constants for native iOS service names. */
    readonly names: IosPermissionNames;
    /**
     * Reset an iOS Simulator service after a prior grant or denial so the app can test its own
     * permission prompt again.
     */
    reset(args: PermissionArguments<IosPermission>): Promise<PermissionResult>;
  };
  /**
   * Exact manifest permission identifiers. Android emulators and authorized physical devices.
   * @supportedPlatforms android
   * @supportedDeviceKinds virtual physical
   */
  readonly android: NativePermissionsContext<AndroidPermission> & {
    /** Readonly constants for common Android manifest permission identifiers. */
    readonly names: AndroidPermissionNames;
  };
}

/** Feature-sliced, current-session host APIs available to task scripts. */
export interface AnsightHost {
  /** Native OS permission control for the session application. Works with SDK and device sessions. */
  readonly permissions: TaskHostPermissionsContext;
  readonly spans: {
    /**
     * Wrap an awaited operation with host-observed start, completion, and failure events for
     * Trends. It uses two task actions and returns the operation's result.
     */
    measure<T>(name: string, operation: () => Promise<T>, options?: { group?: string }): Promise<T>;
  };
  readonly capabilities: {
    /**
     * Inspect the selected session's execution mode, provider availability, and executable app
     * tools before taking an optional path.
     */
    snapshot(): Promise<ExecutionCapabilitySnapshot>;
    /**
     * Require capabilities or app-tool IDs for a branch at runtime. Unavailable requirements
     * reject instead of silently skipping the branch.
     */
    require(requirements: ExecutionRequirements): Promise<ExecutionCapabilitySnapshot>;
  };
  readonly files: {
    /**
     * List entries under a host-accessible app sandbox root. Use a root-relative path; this is
     * distinct from SDK-backed app.files.
     */
    list(arguments_: SandboxFileArguments): Promise<{ root: string; path: string; entries: string[] }>;
    /** Read a host-accessible sandbox file as Base64, up to 256 KiB, when inspecting its current contents. */
    read(arguments_: SandboxFileArguments): Promise<SandboxFileResult & { base64: string }>;
    /**
     * Retain a host-accessible sandbox file as session evidence and use the returned snapshotId
     * to find the copy later.
     */
    capture(arguments_: SandboxFileArguments): Promise<SandboxFileResult & { snapshotId: string }>;
  };
  /** Session annotation inspection and mutation operations. */
  readonly annotations: TaskHostAnnotationsContext;
  /** Live app-tool catalog inspection operations. */
  readonly appTools: TaskHostAppToolsContext;
  /** Captured session artifact inspection operations. */
  readonly artifacts: TaskHostArtifactsContext;
  /** Database assertion operations. */
  readonly database: TaskHostDatabaseContext;
  /** Selected-device control operations. */
  readonly device: TaskHostDeviceContext;
  /** Selected-application lifecycle operations. */
  readonly lifecycle: TaskHostLifecycleContext;
  /** System software-keyboard inspection and control operations. */
  readonly keyboard: TaskHostKeyboardContext;
  /** Captured session log inspection operations. */
  readonly logs: TaskHostLogsContext;
  /** Captured HTTP request, header, and bounded body inspection operations. */
  readonly network: TaskHostNetworkContext;
  /** Live and persisted screenshot operations. */
  readonly screenshots: TaskHostScreenshotsContext;
  /** Session state, properties, and timeline operations. */
  readonly session: TaskSessionContext;
  /** Repository task discovery and composition operations. */
  readonly tasks: TaskHostTasksContext;
  /** Captured session telemetry inspection operations. */
  readonly telemetry: TaskHostTelemetryContext;
  /** Captured touch and gesture inspection operations. */
  readonly touches: TaskHostTouchesContext;
  /** Live and persisted UI inspection and interaction operations. */
  readonly ui: TaskHostUiContext;

  /**
   * Calls one extra, explicitly declared host-owned tool against the enforced session.
   * Calls must be awaited serially; starting a second call while one is active fails.
   *
   * @param name Exact tool name listed in `task.hostTools`.
   * @param args Tool arguments. Target fields are stripped and the task session is injected.
   * @returns The tool's structured content.
   */
  callTool<TResult = unknown>(
    name: string,
    args?: AppToolArguments,
  ): Promise<TResult>;
}

/** JSON-compatible arguments accepted by an app-defined tool. */
export interface AppToolArguments {
  /** A JSON-compatible app-tool argument keyed by its published name. */
  [name: string]: JsonValue | undefined;
}

/** Error contract published by the app tool protocol. */
export interface AppToolError {
  /** Error-state discriminator. The host error envelope may omit this field. */
  success?: false;
  /** Successful app-tool results are never present on an error payload. */
  result?: never;
  /** Machine-readable app-tool error code. */
  code: string;
  /** Human-readable operation result or diagnostic message. */
  message: string;
  /** Whether retrying the same tool request may succeed. */
  retryable: boolean;
  /** Additional structured error details. */
  details?: JsonValue | null;
}

/** Successful payload returned by a resolved app-tool task call. */
export interface AppToolSuccessPayload<TResult> {
  /** Exact tool identifier executed by the live app. */
  toolId: string;
  /** Successful app-tool execution discriminator. */
  success: true;
  /** Optional human-readable result supplied by the app tool. */
  message?: string | null;
  /** The tool-specific result, typed by the caller's generic argument. */
  result: TResult;
}

/** Metadata shared by successful and failed app-tool calls. */
export interface AppToolCallMetadata extends SessionResultIdentity {
  /** Exact tool identifier used for the operation. */
  toolId: string;
  /** Arguments that the host supplied from host or workspace defaults. */
  appliedHostDefaults?: AppToolArguments;
  /** Artifacts associated with the result or app-tool response. */
  artifacts?: ArtifactDescriptor[];
  /** Persistence metadata when the response promoted a visual tree to session evidence. */
  persistedVisualTree?: {
    /** Whether the host persisted the visual-tree response. */
    persisted: boolean;
    /** Identifier of the persisted visual-tree snapshot. */
    snapshotId?: string | null;
    /** Stable hash of the persisted visual tree. */
    treeHash?: string | null;
    /** Additional visual-tree persistence detail. */
    message?: string;
  };
}

/** Successful host envelope around a standard or custom app-tool result. */
export interface AppToolCallSuccess<TResult> extends AppToolCallMetadata {
  /** Successful tool-protocol response discriminator. */
  responseType: "tool.result";
  /** Typed payload returned by the live app. */
  payload: AppToolSuccessPayload<TResult>;
}

/** Failed host envelope returned by the app-tool protocol. */
export interface AppToolCallFailure extends AppToolCallMetadata {
  /** Failed tool-protocol response discriminator. */
  responseType: "tool.error";
  /** Structured protocol or execution error. */
  payload: AppToolError;
}

/** Discriminated host envelope around every standard or custom app-tool result. */
export type AppToolCallResult<TResult> =
  | AppToolCallSuccess<TResult>
  | AppToolCallFailure;

/** Arguments accepted by the app artifact query operation. */
export interface AppArtifactQueryArguments extends AppToolArguments {
  /** Identifier of the app artifact provider. */
  providerId?: string;
  /** Identifier of the artifact within its provider. */
  artifactId?: string;
}

/** Arguments accepted by the app artifact request operation. */
export interface AppArtifactRequestArguments extends AppToolArguments {
  /** Identifier of the app artifact provider. */
  providerId: string;
  /** Identifier of the artifact within its provider. */
  artifactId: string;
  /** Provider-specific string arguments forwarded with the artifact request. */
  arguments?: Record<string, string>;
}

/** Feature component for task app artifacts operations. */
export interface TaskArtifactsContext {
  /**
   * Use this to discover which artifact providers and artifact IDs the app currently offers
   * before requesting an export. Check the AppToolCallResult envelope for success or a tool
   * error.
   */
  query<TResult = unknown>(
    args?: AppArtifactQueryArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to create one provider-owned artifact for the current session, supplying a known
   * provider ID and artifact ID. Check the AppToolCallResult envelope for success or a tool
   * error.
   */
  request<TResult = unknown>(
    args: AppArtifactRequestArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app UI operations. */
export interface TaskUiContext {
  /**
   * Use this to inspect the app-owned visual hierarchy when visible screen state needs more
   * detail than a screenshot. Check the AppToolCallResult envelope for success or a tool error.
   */
  getVisualTree<TResult = LiveVisualTreeResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to capture the current screen through the app SDK for evidence at this point in
   * the session. Check the AppToolCallResult envelope for success or a tool error.
   */
  getScreenshot<TResult = ScreenshotCaptureResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read details for a node already identified in a visual tree. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  inspectNode<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to highlight an app location or state with a diagnostic overlay. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  showOverlay<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read one known overlay before changing or removing it. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getOverlay<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to discover active overlays when their IDs are not already known. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  queryOverlays<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to change an existing diagnostic overlay without creating another. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  updateOverlay<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to remove one diagnostic overlay after its purpose is complete. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  removeOverlay<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to remove all diagnostic overlays to restore a clean app view. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  clearOverlays<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app files operations. */
export interface TaskFilesContext {
  /**
   * Use this to discover entries in an app-sandbox directory before selecting a file. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  listDirectory<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect the contents of a selected app-sandbox file through the SDK. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  readFile<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to compare a file across steps without transferring its full contents. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getChecksum<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to retrieve a selected sandbox file through the app tool response. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  download<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to transfer a sandbox file as binary evidence that the host can retain as a
   * session artifact. Check the AppToolCallResult envelope for success or a tool error.
   */
  beginBinaryDownload<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to write supplied content into an allowed app-sandbox location for a controlled
   * test. Check the AppToolCallResult envelope for success or a tool error.
   */
  push<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to duplicate an allowed sandbox file while preserving the original. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  copy<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to rename or relocate an allowed sandbox file. Check the AppToolCallResult
   * envelope for success or a tool error.
   */
  move<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to remove a selected sandbox file after verifying the target path. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  delete<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app file descriptors operations. */
export interface TaskFileDescriptorsContext {
  /**
   * Use this to identify open file descriptors while diagnosing a leak. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  listOpen<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to compare a lightweight descriptor count before and after an app action. Check
   * the AppToolCallResult envelope for success or a tool error.
   */
  countOpen<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to examine one descriptor or related runtime value identified by the diagnostic
   * suite. Check the AppToolCallResult envelope for success or a tool error.
   */
  inspect<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to compare current descriptor usage with the process limits. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getUsage<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app JNI references operations. */
export interface TaskJniReferencesContext {
  /**
   * Use this to capture Android JNI references when investigating retained native objects.
   * Check the AppToolCallResult envelope for success or a tool error.
   */
  captureGraph<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Plain-text clipboard contents returned by the native app. */
export interface ClipboardTextResult {
  /** Exact text, including whitespace and empty strings; null means no plain text. */
  text: string | null;
  hasText: boolean;
}

export interface ClipboardHasTextResult {
  hasText: boolean;
}
export interface ClipboardWriteResult {
  updated: boolean;
}
export interface ClipboardClearResult {
  cleared: boolean;
}
export interface ClipboardSetTextArguments {
  text: string;
}

/** Device system clipboard accessed by the connected app's native SDK. */
export interface TaskClipboardContext {
  /**
   * Use this to read exact plain text from the native clipboard; iOS may ask for paste
   * permission. Check the AppToolCallResult envelope for success or a tool error.
   */
  getText(): Promise<AppToolCallResult<ClipboardTextResult>>;
  /**
   * Use this to check whether plain text exists without returning its contents. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  hasText(): Promise<AppToolCallResult<ClipboardHasTextResult>>;
  /**
   * Use this to replace clipboard contents with controlled test text of at most 65,536 UTF-8
   * bytes. Check the AppToolCallResult envelope for success or a tool error.
   */
  setText(args: ClipboardSetTextArguments): Promise<AppToolCallResult<ClipboardWriteResult>>;
  /**
   * Use this to remove clipboard contents during test setup or cleanup. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  clear(): Promise<AppToolCallResult<ClipboardClearResult>>;
}

/** Feature component for app preferences. */
export interface TaskPreferencesContext {
  /**
   * Use this to discover preference keys exposed by the connected app. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  listKeys<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read selected preferences before asserting or changing app behavior. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  get<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to set a selected preference for a controlled development test. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  set<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to remove a selected preference to exercise its default behavior. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  remove<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app secure storage operations. */
export interface TaskSecureStorageContext {
  /**
   * Use this to inspect a selected secure-storage value when the app explicitly permits this
   * suite. Check the AppToolCallResult envelope for success or a tool error.
   */
  get<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to write a controlled secure-storage value for a development test. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  set<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to remove a selected secure-storage value during test cleanup. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  remove<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app data operations. */
export interface TaskDataContext {
  /**
   * Use this to discover which app databases are exposed before selecting one. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  listDatabases<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect tables and columns before constructing a query. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  describeSchema<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect domain state through the app database tool after choosing a database
   * and schema. Check the AppToolCallResult envelope for success or a tool error.
   */
  query<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/** Feature component for task app reflection operations. */
export interface TaskReflectionContext {
  /**
   * Use this to discover the runtime objects the app has exposed for reflection. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  listRoots<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read members of an exposed object before choosing a member. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  inspectObject<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect type members and signatures before an invocation or mutation. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  describeType<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to change an exposed member in a controlled development workflow. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  setMemberValue<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to call an exposed runtime method when a narrower app-specific tool is
   * unavailable. Check the AppToolCallResult envelope for success or a tool error.
   */
  invokeMethod<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/**
 * Feature component for task app .NET MAUI operations.
 * @supportedFrameworks dotnet-maui
 * @unsupportedFrameworks dotnet-ios dotnet-android react-native flutter capacitor ios-uikit ios-swiftui android-views android-compose native-unknown
 */
export interface TaskMauiContext {
  /**
   * Use this to identify the active .NET MAUI page before inspecting its elements. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getCurrentPage<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect the current MAUI element hierarchy and locate stable element IDs. Check
   * the AppToolCallResult envelope for success or a tool error.
   */
  getVisualTree<TResult = LiveVisualTreeResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to search MAUI elements before reading or acting on one. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  findElements<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect one MAUI element selected from a tree or search result. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getElement<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read a selected bindable property and its current value. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getBindableProperty<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to set a bindable property to reproduce a controlled UI state. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  setBindableProperty<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to clear a local bindable value so its normal source can apply. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  clearBindableProperty<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to create a diagnostic MAUI element from a XAML fragment. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  inflateXaml<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to insert a diagnostic element into a selected MAUI container. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  addElement<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to remove a selected diagnostic element from the MAUI tree. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  removeElement<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to switch the app theme while reproducing a theme-specific issue. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  setAppTheme<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect the view model bound to a selected MAUI element. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getBindingContext<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect binding expressions and sources for a selected element. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getBindings<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect resolved resources and styles affecting the current UI. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getResourceState<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read the MAUI navigation stack when visible page state is insufficient. Check
   * the AppToolCallResult envelope for success or a tool error.
   */
  getNavigationState<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to invoke an action exposed by a selected MAUI element. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  invokeElementAction<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to wait for a MAUI-specific UI condition before a subsequent action. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  waitForUi<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect layout measurements when an element is misplaced or clipped. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getLayoutDiagnostics<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect the native handler backing a MAUI view. Check the AppToolCallResult
   * envelope for success or a tool error.
   */
  getHandlerDiagnostics<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to invoke an exposed view-model command for a controlled test. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  invokeBindingContextCommand<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to change an exposed view-model property to reproduce a state. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  setBindingContextProperty<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/**
 * Feature component for task app React Native operations.
 * @supportedFrameworks react-native
 * @unsupportedFrameworks dotnet-maui dotnet-ios dotnet-android flutter capacitor ios-uikit ios-swiftui android-views android-compose native-unknown
 */
export interface TaskReactContext {
  /**
   * Use this to inspect the React component hierarchy behind the current screen. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getComponentTree<TResult = LiveVisualTreeResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect React Native layout and shadow nodes when geometry matters. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getShadowTree<TResult = LiveVisualTreeResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to search components before inspecting or acting on one. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  findComponents<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read details for a component found in the React tree. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getComponent<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to inspect React navigation state when screen breadcrumbs are insufficient. Check
   * the AppToolCallResult envelope for success or a tool error.
   */
  getNavigationState<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to invoke an action exposed by a selected React component. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  invokeComponentAction<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/**
 * Feature component for task app Flutter operations.
 * @supportedFrameworks flutter
 * @unsupportedFrameworks dotnet-maui dotnet-ios dotnet-android react-native capacitor ios-uikit ios-swiftui android-views android-compose native-unknown
 */
export interface TaskFlutterContext {
  /**
   * Use this to inspect the Flutter widget hierarchy behind the current screen. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getWidgetTree<TResult = LiveVisualTreeResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read details for a widget identified in the tree or a search result. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  inspectWidget<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to search Flutter widgets before inspecting one. Check the AppToolCallResult
   * envelope for success or a tool error.
   */
  findWidgets<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read Flutter navigation state when the visible route is ambiguous. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  getNavigationState<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/**
 * Feature component for task app Capacitor operations.
 * @supportedFrameworks capacitor
 * @unsupportedFrameworks dotnet-maui dotnet-ios dotnet-android react-native flutter ios-uikit ios-swiftui android-views android-compose native-unknown
 */
export interface TaskCapacitorContext {
  /**
   * Use this to inspect the current WebView DOM document. Check the AppToolCallResult envelope
   * for success or a tool error.
   */
  getDocument<TResult = LiveVisualTreeResult>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to read details for a DOM node found in the document or a selector query. Check
   * the AppToolCallResult envelope for success or a tool error.
   */
  inspectNode<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to find WebView elements using the adapter-supported DOM selector. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  querySelector<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /**
   * Use this to perform an allowed action on a selected WebView control. Check the
   * AppToolCallResult envelope for success or a tool error.
   */
  invokeAction<TResult = unknown>(
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
}

/**
 * Tool suites published by the connected app SDK.
 *
 * Calls execute immediately in a task and return an AppToolCallResult envelope.
 * Narrow on responseType before reading payload.result; a TypeScript method
 * can exist even when the app has not registered or authorized that tool.
 * Inspect ansight.appTools.list() or declare a required app tool in a version 2
 * task descriptor when availability is essential.
 */
export interface TaskAppContext {
  /** False when no SDK app-tool provider is connected. Every app call then rejects. */
  readonly available: boolean;
  /**
   * Call an app-defined tool when a standard suite does not cover the operation.
   *
   * Use the exact registered tool ID and its JSON argument schema. TResult
   * describes only payload.result; narrow on responseType before reading it.
   * @example
   * const response = await app.callTool<{ count: number }>("app.queue_depth");
   * if (response.responseType === "tool.error") throw new Error(response.payload.message);
   * expect(response.payload.result.count, { id: "queue-empty" }).toBe(0);
   */
  callTool<TResult = unknown>(
    toolId: string,
    args?: AppToolArguments,
  ): Promise<AppToolCallResult<TResult>>;
  /** Artifact tools published by the live app. */
  readonly artifacts: TaskArtifactsContext;
  /** General UI tools published by the live app. */
  readonly ui: TaskUiContext;
  /** App-sandbox file operations published by the live app. */
  readonly files: TaskFilesContext;
  /** Open file-descriptor diagnostics published by the live app. */
  readonly fileDescriptors: TaskFileDescriptorsContext;
  /** Android JNI-reference diagnostics published by the live app. */
  readonly jniReferences: TaskJniReferencesContext;
  /** App preference-store operations published by the live app. */
  readonly preferences: TaskPreferencesContext;
  /** Native clipboard tools. Requires clipboard registration in the app SDK. */
  readonly clipboard: TaskClipboardContext;
  /** App secure-storage operations published by the live app. */
  readonly secureStorage: TaskSecureStorageContext;
  /** App database inspection operations published by the live app. */
  readonly data: TaskDataContext;
  /** Runtime reflection operations published by the live app. */
  readonly reflection: TaskReflectionContext;
  /** .NET MAUI inspection and interaction operations published by the live app. */
  readonly maui: TaskMauiContext;
  /** React Native inspection and interaction operations published by the live app. */
  readonly react: TaskReactContext;
  /** Flutter inspection and interaction operations published by the live app. */
  readonly flutter: TaskFlutterContext;
  /** Capacitor DOM inspection and interaction operations published by the live app. */
  readonly capacitor: TaskCapacitorContext;
}

/** Stable identity and optional reporter text for one task expectation. */
export interface TaskExpectationMetadata {
  /** Stable, non-empty ID unique within one task run. */
  id: string;

  /** Optional human-readable context shown for both passing and failing assertions. */
  message?: string;
}

/** Playwright-style matchers that record one authoritative named assertion. */
export interface TaskMatchers<T> {
  /** Negate the next matcher when the expected outcome is absence or inequality. */
  readonly not: TaskMatchers<T>;

  /** Compare scalar values or object identity using Object.is; use toEqual for object contents. */
  toBe(expected: T): T;

  /** Compare array or object contents recursively when reference identity is irrelevant. */
  toEqual(expected: unknown): T;

  /** Assert a truthy flag or condition, such as a UI result's passed property. */
  toBeTruthy(): NonNullable<T>;

  /** Assert a falsy flag or condition, such as a dismissed error banner. */
  toBeFalsy(): T;

  /** Assert that a lookup returned a defined value before using it. */
  toBeDefined(): Exclude<T, undefined>;

  /** Assert that a value is absent specifically as undefined. */
  toBeUndefined(): T;

  /** Assert an explicit null value rather than an undefined or missing value. */
  toBeNull(): T;

  /** Check substring inclusion or direct array membership using Object.is. Use toContainEqual for object members. */
  toContain(
    expected: T extends string
      ? string
      : T extends readonly (infer TItem)[]
        ? TItem
        : never,
  ): T;

  /** Find an array member by recursive value equality, such as an expected record. */
  toContainEqual(
    expected: T extends readonly (infer TItem)[] ? TItem : never,
  ): T;
}

/** Creates Playwright-style hard or soft named expectations. */
export interface TaskExpect {
  /**
   * Create a named assertion for task pass/fail status. A hard failure stops the task function
   * and remains recorded even if caught.
   */
  <T>(actual: T, metadata: TaskExpectationMetadata): TaskMatchers<T>;

  /**
   * Record a named assertion while allowing later task steps to run after a failure. A soft
   * failure still prevents Passed status.
   */
  soft<T>(actual: T, metadata: TaskExpectationMetadata): TaskMatchers<T>;
}

/** Argument passed to the module's default-exported task function. */
export interface TaskInvocation<
  TInput extends object = Record<string, unknown>,
> {
  /** Host-owned immutable run identity and effective limits. */
  run: Readonly<TaskRun>;

  /** Immutable input after defaults and validation have been applied. */
  input: Readonly<TInput>;

  /** Secrets explicitly granted to this run. Values are available only to the task process. */
  secrets: Readonly<{
    /**
     * Read a granted secret alias, or undefined when it was not granted to this run. Do not
     * include the value in results or logs.
     */
    get(alias: string): string | undefined;
    /** Read a required granted secret alias; throw when absent so the task cannot continue with missing credentials. */
    require(alias: string): string;
  }>;

  /** Session-pinned host API. */
  ansight: AnsightHost;

  /** Standard app methods and the stringly-typed custom-tool bridge. */
  app: TaskAppContext;

  /** Playwright-style named expectation API used to establish pass/fail evidence. */
  expect: TaskExpect;
}

/**
 * Default export implemented by an Ansight task module.
 *
 * A normal return with no assertions is `Inconclusive`. At least one assertion
 * must pass and none may fail for the task run to be `Passed`.
 */
export type TaskFunction<
  TInput extends object = Record<string, unknown>,
  TOutput = unknown,
> = (invocation: TaskInvocation<TInput>) => TOutput | Promise<TOutput>;

/** Capabilities required before execution. Specific app tools must be advertised and executable. */
export interface ExecutionRequirements {
  capabilities?: string[];
  appTools?: string[];
}
export interface ExecutionCapabilitySnapshot {
  sessionId: string;
  executionMode: "sdk" | "device";
  live: boolean;
  appAvailable: boolean;
  appTools: string[];
  capabilities: Record<string, { available: boolean; provider: string; reason?: string | null }>;
}
export interface CapabilityUnavailableError extends Error {
  code: "capability_unavailable";
  capability: string;
  executionMode: "sdk" | "device";
  retryable: false;
}
export interface SandboxFileArguments {
  /** data, app, or group:<identifier>. Android currently supports data only. */
  root?: string;
  /** Root-relative path. Empty string lists the root. Symlinks and traversal are refused. */
  path: string;
  maximumBytes?: number;
}
export interface SandboxFileResult {
  root: string;
  path: string;
  sizeBytes: number;
  sha256: string;
  provider: string;
  consistency: "best-effort-live-copy";
  startedAtUtc: string;
  capturedAtUtc: string;
}
