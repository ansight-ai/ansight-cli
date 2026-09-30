// Generated from the Ansight repository trigger v2 contract (v1 SDK modules remain supported).

/**
 * # Purpose and usage
 *
 * This file defines the resident host's authoring contract; it is not an executable
 * trigger. Executable trigger modules are owned by each app repository and live
 * under that repository's `ansight/triggers` directory. The host discovers them
 * only from the repository registered for the selected App ID.
 *
 * A repository trigger is an event-driven, app-specific automation. Use a
 * trigger when Ansight should react automatically to a known app or session
 * event—for example capturing Mapbox state after navigation settles or saving a
 * 3D scene snapshot after an asset finishes loading.
 *
 * The CLI matches the exact event kind, App ID, and declarative conditions
 * before starting the TypeScript module. The trigger function receives the normalized
 * event and may return one app-tool or host evidence action for the host to validate, audit, and
 * execute. Retries and timeouts remain host-owned.
 *
 * Triggers are not general schedulers or background listeners: a module cannot
 * register an event source or arbitrary predicate. Use a repository task when a
 * user or agent should explicitly start a repeatable test or navigation cycle.
 *
 * @packageDocumentation
 */

/** A JSON scalar accepted by the trigger protocol. */
export type JsonPrimitive = string | number | boolean | null;

/** A recursively JSON-serializable value accepted by the trigger protocol. */
export type JsonValue = JsonPrimitive | JsonObject | JsonValue[];

/** A JSON object with recursively serializable values. */
export interface JsonObject {
  /** A JSON property keyed by its serialized field name. */
  [key: string]: JsonValue;
}

/** JSON-compatible arguments accepted by an app-defined tool action. */
export interface AppToolArguments {
  /** A JSON-compatible app-tool argument keyed by its published name. */
  [name: string]: JsonValue | undefined;
}

/**
 * A JSON Schema object.
 *
 * Trigger event schemas are descriptive in contract v1. Declarative conditions,
 * not `eventSchema`, determine whether a trigger matches an event.
 */
export interface JsonSchema {
  /** A JSON Schema keyword or extension value. */
  [key: string]: unknown;
}

/** Operators supported by the host-owned trigger condition engine. */
export type ConditionOperator =
  | "equals"
  | "notEquals"
  | "contains"
  | "startsWith"
  | "endsWith"
  | "exists"
  | "notExists";

/** Top-level event-envelope fields supported by host-owned trigger conditions. */
export type EventEnvelopeField =
  | "eventId"
  | "kind"
  | "occurredAtUtc"
  | "appId"
  | "sessionId"
  | "correlationId"
  | "causationId";

/** Field paths accepted by the host-owned trigger condition engine. */
export type ConditionField =
  | EventEnvelopeField
  | `payload.${string}`;

/** One declarative condition evaluated by the CLI before the trigger runs. */
export interface Condition {
  /**
   * Envelope field such as `eventId`, `kind`, `appId` or `sessionId`, or a
   * nested payload path beginning with `payload.`.
   */
  field: ConditionField;

  /** Comparison performed by the host. */
  operator: ConditionOperator;

  /** Expected value. Required except for `exists` and `notExists`. */
  value?: unknown;

  /** Enables ordinal case-insensitive comparison for string values. */
  ignoreCase?: boolean;
}

/** Host-owned bounded retry policy for failed or timed-out trigger attempts. */
export interface RetryPolicy {
  /** Total attempts including the first. Defaults to 1; accepted range is 1–5. */
  maxAttempts?: number;

  /** Delay before attempt 2. Defaults to 250 ms when retries are enabled. */
  initialDelayMs?: number;

  /** Exponential delay multiplier. Defaults to 2; accepted range is 1–10. */
  backoffMultiplier?: number;

  /** Maximum retry delay. Must be at least `initialDelayMs` and at most 60 seconds. */
  maxDelayMs?: number;
}

/** Normalized event kinds currently emitted by the Ansight host. */
export type EventKind =
  | "app.event"
  | "app.lifecycle.changed"
  | "app.pairing.discoveryReceived"
  | "app.pairing.accepted"
  | "app.pairing.rejected"
  | "app.pairing.unknown"
  | "session.capture.started"
  | "session.capture.updated"
  | "session.capture.stopped"
  | "session.capture.finalized"
  | "session.capture.unknown"
  | "session.transfer.telemetry"
  | "session.transfer.log"
  | "session.transfer.appEvent"
  | "session.transfer.appProfile"
  | "session.transfer.screenshot"
  | "session.transfer.visualTree"
  | "session.transfer.touchInput"
  | "session.transfer.annotatedFeedback"
  | "session.transfer.unknown"
  | "session.log.received"
  | "trends.check.failed"
  | "trends.regression.detected"
  | "trends.regression.recovered"
  | "trends.unknown";

/**
 * Static metadata exported as `export const trigger` from an Ansight trigger module.
 *
 * Define a trigger for an automatic, event-driven reaction whose match can be
 * expressed with an exact event kind and declarative host-owned conditions.
 *
 * The descriptor must be a JSON-compatible object literal. The host extracts and
 * indexes it without importing or executing the module.
 */
export interface TriggerDefinition<
  TEventKind extends EventKind = EventKind,
> {
  /** Contract version. Omitted descriptors are interpreted as version 1. */
  schemaVersion?: 1 | 2;
  /** Version 2 requirements checked before importing the module. */
  requires?: { capabilities?: string[]; appTools?: string[] };

  /** Whether this trigger may match events and execute. Defaults to true. */
  enabled?: boolean;

  /**
   * Optional app scope. It is required for embedded-host repositories and may be
   * omitted when the host supplies the selected app ID from a connected workspace.
   */
  appId?: string;

  /** Exact normalized host event kind, for example `app.event`. */
  eventKind: TEventKind;

  /**
   * JSON Schema describing `invocation.event.payload`.
   *
   * Contract v1 exposes this schema for authoring and inspection but does not use
   * it for matching or reject events whose payload differs from it.
   */
  eventSchema?: JsonSchema;

  /** Bound around the exported TypeScript function only. Range 10–1,000 ms. */
  functionTimeoutMs?: number;

  /** Separate bound around the returned host action. Range 1–300 seconds. */
  actionTimeoutSeconds?: number;

  /** Optional host-owned retry policy. Delays require `maxAttempts` greater than 1. */
  retry?: RetryPolicy;

  /** ANDed conditions evaluated before the trigger runs. At most 16 are accepted. */
  conditions?: Condition[];
}

/** Normalized host event passed to a matched trigger function. */
export interface EventEnvelope<
  TPayload extends object = Record<string, unknown>,
  TEventKind extends EventKind = EventKind,
> {
  /** Stable source event identity. */
  eventId: string;

  /** Normalized event kind used by the trigger index. */
  kind: TEventKind;

  /** ISO-8601 UTC occurrence timestamp. */
  occurredAtUtc: string;

  /** App ID used to scope trigger matching. */
  appId: string;

  /** Live or captured session identity when the source event has one. */
  sessionId?: string | null;

  /** Correlation identity propagated into a returned app-tool action. */
  correlationId: string;

  /** Optional identity of the event or operation that caused this event. */
  causationId?: string | null;

  /** Event-kind-specific normalized data described by `eventSchema`. */
  payload: TPayload;
}

/** Host-owned identity, attempt and timeout data for one trigger execution. */
export interface TriggerRun {
  /** Stable run ID shared by every retry attempt. */
  runId: string;

  /** Repository-relative trigger ID derived from the module path. */
  triggerId: string;

  /** Absolute root of the connected local repository. */
  repositoryRootPath: string;

  /** ISO-8601 UTC time at which the matched run was queued. */
  enqueuedAtUtc: string;

  /** Effective function timeout in milliseconds. */
  functionTimeoutMs: number;

  /** Effective returned-action timeout in seconds. */
  actionTimeoutSeconds: number;

  /** One-based attempt number. */
  attemptNumber: number;

  /** Maximum attempts allowed by the host retry policy. */
  maximumAttempts: number;
}

/** Declarative action returned to the host after a trigger function matches. */
export interface AppToolAction<
  TArguments extends AppToolArguments = AppToolArguments,
> {
  /** Action discriminator. */
  type: "appTool";

  /** Exact app-tool ID published by the connected app. */
  toolId: string;

  /** Arguments matching the live app tool's published `argumentsSchema`. */
  arguments: TArguments;
}

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

/** Feature component for trigger app artifacts operations. */
export interface ArtifactsContext {
  /** Creates the declarative app-tool action that will query available data. */
  query(
    args?: AppArtifactQueryArguments,
  ): AppToolAction<AppArtifactQueryArguments>;
  /** Creates the declarative app-tool action that will request an artifact capture. */
  request(
    args: AppArtifactRequestArguments,
  ): AppToolAction<AppArtifactRequestArguments>;
}

/** Feature component for trigger app UI operations. */
export interface UiContext {
  /** Creates the declarative app-tool action that will get the current visual tree. */
  getVisualTree(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will capture the current screen. */
  getScreenshot(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will inspect one UI node. */
  inspectNode(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will show a diagnostic overlay. */
  showOverlay(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get one diagnostic overlay. */
  getOverlay(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will query diagnostic overlays. */
  queryOverlays(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will update a diagnostic overlay. */
  updateOverlay(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will remove a diagnostic overlay. */
  removeOverlay(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will clear diagnostic overlays. */
  clearOverlays(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app files operations. */
export interface FilesContext {
  /** Creates the declarative app-tool action that will list a directory. */
  listDirectory(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will read a file from the app sandbox. */
  readFile(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will calculate a file checksum. */
  getChecksum(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will download a file. */
  download(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will start a binary file download. */
  beginBinaryDownload(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will push a file to the app sandbox. */
  push(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will copy a file in the app sandbox. */
  copy(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will move a file in the app sandbox. */
  move(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will delete a file in the app sandbox. */
  delete(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app file descriptors operations. */
export interface FileDescriptorsContext {
  /** Creates the declarative app-tool action that will list open file descriptors. */
  listOpen(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will count open file descriptors. */
  countOpen(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will inspect a runtime value. */
  inspect(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get file-descriptor usage. */
  getUsage(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app JNI references operations. */
export interface JniReferencesContext {
  /** Creates the declarative app-tool action that will capture the JNI reference graph. */
  captureGraph(args?: AppToolArguments): AppToolAction;
}

/** Factories for native system clipboard actions. */
export interface ClipboardContext {
  /** Creates an action to read clipboard text (up to 65536 UTF-8 bytes). */
  getText(): AppToolAction;
  /** Creates an action to check for clipboard text. */
  hasText(): AppToolAction;
  /** Creates an action to replace clipboard contents with exact plain text. */
  setText(args: { text: string }): AppToolAction;
  /** Creates an action to clear all clipboard contents. */
  clear(): AppToolAction;
}

/** Factories for app preference-store actions. */
export interface PreferencesContext {
  /** Creates the declarative app-tool action that will list stored keys. */
  listKeys(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get the requested values. */
  get(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will set a stored value. */
  set(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will remove a stored value. */
  remove(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app secure storage operations. */
export interface SecureStorageContext {
  /** Creates the declarative app-tool action that will get the requested values. */
  get(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will set a stored value. */
  set(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will remove a stored value. */
  remove(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app data operations. */
export interface DataContext {
  /** Creates the declarative app-tool action that will list app databases. */
  listDatabases(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will describe a database schema. */
  describeSchema(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will query available data. */
  query(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app reflection operations. */
export interface ReflectionContext {
  /** Creates the declarative app-tool action that will list reflection roots. */
  listRoots(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will inspect an object through reflection. */
  inspectObject(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will describe a reflected type. */
  describeType(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will set a reflected member value. */
  setMemberValue(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will invoke a reflected method. */
  invokeMethod(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app .NET MAUI operations. */
export interface MauiContext {
  /** Creates the declarative app-tool action that will get the current .NET MAUI page. */
  getCurrentPage(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get the current visual tree. */
  getVisualTree(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will find .NET MAUI elements. */
  findElements(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get a .NET MAUI element. */
  getElement(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get a .NET MAUI bindable property. */
  getBindableProperty(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will set a .NET MAUI bindable property. */
  setBindableProperty(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will clear a .NET MAUI bindable property. */
  clearBindableProperty(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will inflate a .NET MAUI XAML fragment. */
  inflateXaml(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will add a .NET MAUI element. */
  addElement(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will remove a .NET MAUI element. */
  removeElement(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will set the .NET MAUI app theme. */
  setAppTheme(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get a .NET MAUI binding context. */
  getBindingContext(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get .NET MAUI bindings. */
  getBindings(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get .NET MAUI resource state. */
  getResourceState(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get framework navigation state. */
  getNavigationState(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will invoke a .NET MAUI element action. */
  invokeElementAction(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will wait for .NET MAUI UI state. */
  waitForUi(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get .NET MAUI layout diagnostics. */
  getLayoutDiagnostics(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get .NET MAUI handler diagnostics. */
  getHandlerDiagnostics(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will invoke a .NET MAUI binding-context command. */
  invokeBindingContextCommand(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will set a .NET MAUI binding-context property. */
  setBindingContextProperty(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app react operations. */
export interface ReactContext {
  /** Creates the declarative app-tool action that will get the React component tree. */
  getComponentTree(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get the React Native shadow tree. */
  getShadowTree(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will find React components. */
  findComponents(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get a React component. */
  getComponent(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get framework navigation state. */
  getNavigationState(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will invoke a React component action. */
  invokeComponentAction(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app flutter operations. */
export interface FlutterContext {
  /** Creates the declarative app-tool action that will get the Flutter widget tree. */
  getWidgetTree(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will inspect a Flutter widget. */
  inspectWidget(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will find Flutter widgets. */
  findWidgets(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will get framework navigation state. */
  getNavigationState(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app capacitor operations. */
export interface CapacitorContext {
  /** Creates the declarative app-tool action that will get the Capacitor document. */
  getDocument(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will inspect one UI node. */
  inspectNode(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will query a Capacitor DOM selector. */
  querySelector(args?: AppToolArguments): AppToolAction;
  /** Creates the declarative app-tool action that will invoke a framework UI action. */
  invokeAction(args?: AppToolArguments): AppToolAction;
}

/** Action factories available to the trigger function. */
export interface AppContext {
  readonly available: boolean;
  /**
   * Creates one app-tool action for the CLI to validate and execute.
   *
   * This method does not call the app immediately. Return its result from the
   * trigger function. The connected app validates the tool ID and arguments.
   */
  callTool(toolId: string, args?: AppToolArguments): AppToolAction;

  /** Factories for artifact app-tool actions. */
  readonly artifacts: ArtifactsContext;
  /** Factories for general UI app-tool actions. */
  readonly ui: UiContext;
  /** Factories for app-sandbox file actions. */
  readonly files: FilesContext;
  /** Factories for open file-descriptor diagnostic actions. */
  readonly fileDescriptors: FileDescriptorsContext;
  /** Factories for Android JNI-reference diagnostic actions. */
  readonly jniReferences: JniReferencesContext;
  /** Factories for app preference-store actions. */
  readonly preferences: PreferencesContext;
  /** Factories for native system clipboard actions. */
  readonly clipboard: ClipboardContext;
  /** Factories for app secure-storage actions. */
  readonly secureStorage: SecureStorageContext;
  /** Factories for app database inspection actions. */
  readonly data: DataContext;
  /** Factories for runtime reflection actions. */
  readonly reflection: ReflectionContext;
  /** Factories for .NET MAUI inspection and interaction actions. */
  readonly maui: MauiContext;
  /** Factories for React Native inspection and interaction actions. */
  readonly react: ReactContext;
  /** Factories for Flutter inspection and interaction actions. */
  readonly flutter: FlutterContext;
  /** Factories for Capacitor DOM inspection and interaction actions. */
  readonly capacitor: CapacitorContext;
}

/** Argument passed to the module's default-exported trigger function. */
export interface TriggerContext<
  TPayload extends object = Record<string, unknown>,
  TEventKind extends EventKind = EventKind,
> {
  /** Host-owned immutable run and attempt data. */
  run: Readonly<TriggerRun>;

  /** Immutable normalized event that matched the trigger. */
  event: Readonly<EventEnvelope<TPayload, TEventKind>>;

  /** Factory for the one optional host-dispatched app-tool action. */
  app: AppContext;
  ansight: {
    capabilities: { executionMode?: "sdk" | "device"; appAvailable?: boolean; capabilities?: Record<string, { available: boolean; provider: string; reason?: string | null }> };
    screenshots: { capture(args?: AppToolArguments): HostEvidenceAction };
    files: { capture(args: { root?: string; path: string; maximumBytes?: number }): HostEvidenceAction };
    annotations: { create(args: AppToolArguments): HostEvidenceAction };
  };
}

/**
 * Default export implemented by an Ansight trigger module.
 *
 * Return one standardized `app` method or `app.callTool(...)` action, or return
 * nothing when the match only needs local computation. The host owns action
 * execution and retries.
 */
export type TriggerFunction<
  TPayload extends object = Record<string, unknown>,
  TEventKind extends EventKind = EventKind,
> = (
  invocation: TriggerContext<TPayload, TEventKind>,
) => AppToolAction | HostEvidenceAction | null | void | Promise<AppToolAction | HostEvidenceAction | null | void>;

/** One bounded host evidence action, available in descriptor version 2. */
export interface HostEvidenceAction {
  type: "hostTool";
  toolName: "ansight_take_screenshot" | "ansight_capture_sandbox_file" | "ansight_create_annotation";
  arguments: AppToolArguments;
}
