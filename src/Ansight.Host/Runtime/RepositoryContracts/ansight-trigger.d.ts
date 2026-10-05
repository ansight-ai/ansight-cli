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
  /**
   * Return this action to have the host query which artifact providers and IDs the app offers
   * at the matched event. The trigger cannot inspect the result or return a second action.
   */
  query(
    args?: AppArtifactQueryArguments,
  ): AppToolAction<AppArtifactQueryArguments>;
  /**
   * Return this action to create one provider-owned artifact using a known provider ID and
   * artifact ID. The host dispatches it for the matched event after the handler returns.
   */
  request(
    args: AppArtifactRequestArguments,
  ): AppToolAction<AppArtifactRequestArguments>;
}

/** Feature component for trigger app UI operations. */
export interface UiContext {
  /**
   * Return this action to inspect the app-owned visual hierarchy when visible screen state
   * needs more detail than a screenshot. The host dispatches it for the matched event after the
   * handler returns.
   */
  getVisualTree(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to capture the current screen through the app SDK for evidence at this
   * point in the session. The host dispatches it for the matched event after the handler
   * returns.
   */
  getScreenshot(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read details for a node already identified in a visual tree. The host
   * dispatches it for the matched event after the handler returns.
   */
  inspectNode(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to highlight an app location or state with a diagnostic overlay. The
   * host dispatches it for the matched event after the handler returns.
   */
  showOverlay(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read one known overlay before changing or removing it. The host
   * dispatches it for the matched event after the handler returns.
   */
  getOverlay(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to discover active overlays when their IDs are not already known. The
   * host dispatches it for the matched event after the handler returns.
   */
  queryOverlays(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to change an existing diagnostic overlay without creating another. The
   * host dispatches it for the matched event after the handler returns.
   */
  updateOverlay(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to remove one diagnostic overlay after its purpose is complete. The host
   * dispatches it for the matched event after the handler returns.
   */
  removeOverlay(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to remove all diagnostic overlays to restore a clean app view. The host
   * dispatches it for the matched event after the handler returns.
   */
  clearOverlays(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app files operations. */
export interface FilesContext {
  /**
   * Return this action to discover entries in an app-sandbox directory before selecting a file.
   * The host dispatches it for the matched event after the handler returns.
   */
  listDirectory(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect the contents of a selected app-sandbox file through the SDK.
   * The host dispatches it for the matched event after the handler returns.
   */
  readFile(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to compare a file across steps without transferring its full contents.
   * The host dispatches it for the matched event after the handler returns.
   */
  getChecksum(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to retrieve a selected sandbox file through the app tool response. The
   * host dispatches it for the matched event after the handler returns.
   */
  download(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to transfer a sandbox file as binary evidence that the host can retain
   * as a session artifact. The host dispatches it for the matched event after the handler
   * returns.
   */
  beginBinaryDownload(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to write supplied content into an allowed app-sandbox location for a
   * controlled test. The host dispatches it for the matched event after the handler returns.
   */
  push(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to duplicate an allowed sandbox file while preserving the original. The
   * host dispatches it for the matched event after the handler returns.
   */
  copy(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to rename or relocate an allowed sandbox file. The host dispatches it
   * for the matched event after the handler returns.
   */
  move(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to remove a selected sandbox file after verifying the target path. The
   * host dispatches it for the matched event after the handler returns.
   */
  delete(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app file descriptors operations. */
export interface FileDescriptorsContext {
  /**
   * Return this action to identify open file descriptors while diagnosing a leak. The host
   * dispatches it for the matched event after the handler returns.
   */
  listOpen(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to compare a lightweight descriptor count before and after an app
   * action. The host dispatches it for the matched event after the handler returns.
   */
  countOpen(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to examine one descriptor or related runtime value identified by the
   * diagnostic suite. The host dispatches it for the matched event after the handler returns.
   */
  inspect(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to compare current descriptor usage with the process limits. The host
   * dispatches it for the matched event after the handler returns.
   */
  getUsage(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app JNI references operations. */
export interface JniReferencesContext {
  /**
   * Return this action to capture Android JNI references when investigating retained native
   * objects. The host dispatches it for the matched event after the handler returns.
   */
  captureGraph(args?: AppToolArguments): AppToolAction;
}

/** Factories for native system clipboard actions. */
export interface ClipboardContext {
  /**
   * Return this action to read exact plain text from the native clipboard; iOS may ask for
   * paste permission. The host dispatches it for the matched event after the handler returns.
   */
  getText(): AppToolAction;
  /**
   * Return this action to check whether plain text exists without returning its contents. The
   * host dispatches it for the matched event after the handler returns.
   */
  hasText(): AppToolAction;
  /**
   * Return this action to replace clipboard contents with controlled test text of at most
   * 65,536 UTF-8 bytes. The host dispatches it for the matched event after the handler returns.
   */
  setText(args: { text: string }): AppToolAction;
  /**
   * Return this action to remove clipboard contents during test setup or cleanup. The host
   * dispatches it for the matched event after the handler returns.
   */
  clear(): AppToolAction;
}

/** Factories for app preference-store actions. */
export interface PreferencesContext {
  /**
   * Return this action to discover preference keys exposed by the connected app. The host
   * dispatches it for the matched event after the handler returns.
   */
  listKeys(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read selected preferences before asserting or changing app behavior.
   * The host dispatches it for the matched event after the handler returns.
   */
  get(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to set a selected preference for a controlled development test. The host
   * dispatches it for the matched event after the handler returns.
   */
  set(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to remove a selected preference to exercise its default behavior. The
   * host dispatches it for the matched event after the handler returns.
   */
  remove(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app secure storage operations. */
export interface SecureStorageContext {
  /**
   * Return this action to inspect a selected secure-storage value when the app explicitly
   * permits this suite. The host dispatches it for the matched event after the handler returns.
   */
  get(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to write a controlled secure-storage value for a development test. The
   * host dispatches it for the matched event after the handler returns.
   */
  set(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to remove a selected secure-storage value during test cleanup. The host
   * dispatches it for the matched event after the handler returns.
   */
  remove(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app data operations. */
export interface DataContext {
  /**
   * Return this action to discover which app databases are exposed before selecting one. The
   * host dispatches it for the matched event after the handler returns.
   */
  listDatabases(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect tables and columns before constructing a query. The host
   * dispatches it for the matched event after the handler returns.
   */
  describeSchema(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect domain state through the app database tool after choosing a
   * database and schema. The host dispatches it for the matched event after the handler
   * returns.
   */
  query(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app reflection operations. */
export interface ReflectionContext {
  /**
   * Return this action to discover the runtime objects the app has exposed for reflection. The
   * host dispatches it for the matched event after the handler returns.
   */
  listRoots(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read members of an exposed object before choosing a member. The host
   * dispatches it for the matched event after the handler returns.
   */
  inspectObject(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect type members and signatures before an invocation or mutation.
   * The host dispatches it for the matched event after the handler returns.
   */
  describeType(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to change an exposed member in a controlled development workflow. The
   * host dispatches it for the matched event after the handler returns.
   */
  setMemberValue(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to call an exposed runtime method when a narrower app-specific tool is
   * unavailable. The host dispatches it for the matched event after the handler returns.
   */
  invokeMethod(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app .NET MAUI operations. */
export interface MauiContext {
  /**
   * Return this action to identify the active .NET MAUI page before inspecting its elements.
   * The host dispatches it for the matched event after the handler returns.
   */
  getCurrentPage(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect the current MAUI element hierarchy and locate stable element
   * IDs. The host dispatches it for the matched event after the handler returns.
   */
  getVisualTree(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to search MAUI elements before reading or acting on one. The host
   * dispatches it for the matched event after the handler returns.
   */
  findElements(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect one MAUI element selected from a tree or search result. The
   * host dispatches it for the matched event after the handler returns.
   */
  getElement(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read a selected bindable property and its current value. The host
   * dispatches it for the matched event after the handler returns.
   */
  getBindableProperty(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to set a bindable property to reproduce a controlled UI state. The host
   * dispatches it for the matched event after the handler returns.
   */
  setBindableProperty(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to clear a local bindable value so its normal source can apply. The host
   * dispatches it for the matched event after the handler returns.
   */
  clearBindableProperty(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to create a diagnostic MAUI element from a XAML fragment. The host
   * dispatches it for the matched event after the handler returns.
   */
  inflateXaml(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to insert a diagnostic element into a selected MAUI container. The host
   * dispatches it for the matched event after the handler returns.
   */
  addElement(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to remove a selected diagnostic element from the MAUI tree. The host
   * dispatches it for the matched event after the handler returns.
   */
  removeElement(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to switch the app theme while reproducing a theme-specific issue. The
   * host dispatches it for the matched event after the handler returns.
   */
  setAppTheme(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect the view model bound to a selected MAUI element. The host
   * dispatches it for the matched event after the handler returns.
   */
  getBindingContext(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect binding expressions and sources for a selected element. The
   * host dispatches it for the matched event after the handler returns.
   */
  getBindings(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect resolved resources and styles affecting the current UI. The
   * host dispatches it for the matched event after the handler returns.
   */
  getResourceState(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read the MAUI navigation stack when visible page state is
   * insufficient. The host dispatches it for the matched event after the handler returns.
   */
  getNavigationState(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to invoke an action exposed by a selected MAUI element. The host
   * dispatches it for the matched event after the handler returns.
   */
  invokeElementAction(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to wait for a MAUI-specific UI condition before a subsequent action. The
   * host dispatches it for the matched event after the handler returns.
   */
  waitForUi(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect layout measurements when an element is misplaced or clipped.
   * The host dispatches it for the matched event after the handler returns.
   */
  getLayoutDiagnostics(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect the native handler backing a MAUI view. The host dispatches
   * it for the matched event after the handler returns.
   */
  getHandlerDiagnostics(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to invoke an exposed view-model command for a controlled test. The host
   * dispatches it for the matched event after the handler returns.
   */
  invokeBindingContextCommand(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to change an exposed view-model property to reproduce a state. The host
   * dispatches it for the matched event after the handler returns.
   */
  setBindingContextProperty(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app react operations. */
export interface ReactContext {
  /**
   * Return this action to inspect the React component hierarchy behind the current screen. The
   * host dispatches it for the matched event after the handler returns.
   */
  getComponentTree(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect React Native layout and shadow nodes when geometry matters.
   * The host dispatches it for the matched event after the handler returns.
   */
  getShadowTree(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to search components before inspecting or acting on one. The host
   * dispatches it for the matched event after the handler returns.
   */
  findComponents(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read details for a component found in the React tree. The host
   * dispatches it for the matched event after the handler returns.
   */
  getComponent(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to inspect React navigation state when screen breadcrumbs are
   * insufficient. The host dispatches it for the matched event after the handler returns.
   */
  getNavigationState(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to invoke an action exposed by a selected React component. The host
   * dispatches it for the matched event after the handler returns.
   */
  invokeComponentAction(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app flutter operations. */
export interface FlutterContext {
  /**
   * Return this action to inspect the Flutter widget hierarchy behind the current screen. The
   * host dispatches it for the matched event after the handler returns.
   */
  getWidgetTree(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read details for a widget identified in the tree or a search result.
   * The host dispatches it for the matched event after the handler returns.
   */
  inspectWidget(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to search Flutter widgets before inspecting one. The host dispatches it
   * for the matched event after the handler returns.
   */
  findWidgets(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read Flutter navigation state when the visible route is ambiguous.
   * The host dispatches it for the matched event after the handler returns.
   */
  getNavigationState(args?: AppToolArguments): AppToolAction;
}

/** Feature component for trigger app capacitor operations. */
export interface CapacitorContext {
  /**
   * Return this action to inspect the current WebView DOM document. The host dispatches it for
   * the matched event after the handler returns.
   */
  getDocument(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to read details for a DOM node found in the document or a selector
   * query. The host dispatches it for the matched event after the handler returns.
   */
  inspectNode(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to find WebView elements using the adapter-supported DOM selector. The
   * host dispatches it for the matched event after the handler returns.
   */
  querySelector(args?: AppToolArguments): AppToolAction;
  /**
   * Return this action to perform an allowed action on a selected WebView control. The host
   * dispatches it for the matched event after the handler returns.
   */
  invokeAction(args?: AppToolArguments): AppToolAction;
}

/**
 * Action factories for tools published by the connected app SDK.
 *
 * These methods return descriptions of one action; they do not call the app in
 * the trigger function. Return one action to the host. The matched session,
 * app's registered tool, argument schema, guard, and grants are checked when
 * the host dispatches it. AppContext.available only reports SDK provider
 * presence, not availability of each individual tool.
 */
export interface AppContext {
  /** False when the matched session has no connected SDK app-tool provider; returning an app action then fails. */
  readonly available: boolean;
  /**
   * Create one app-defined tool action when no standard suite method fits.
   *
   * This method does not call the app immediately. Return its result from the
   * trigger function. The connected app validates the tool ID and arguments
   * when the host dispatches the action.
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

  /**
   * Build at most one app-tool action and return it. Factory calls do not execute the app
   * inside the trigger function.
   */
  app: AppContext;
  /**
   * Host-owned evidence action factories for version 2 triggers. Return one action; these
   * methods do not execute it in the handler.
   */
  ansight: {
    /**
     * Snapshot supplied with this invocation. Check availability before choosing an optional
     * host action; unlike the task API, this is not a method.
     */
    capabilities: { executionMode?: "sdk" | "device"; appAvailable?: boolean; capabilities?: Record<string, { available: boolean; provider: string; reason?: string | null }> };
    screenshots: {
      /**
       * Return an action to capture the live screen at this event. Version 2 requires
       * ui.screenshot and a live capture provider.
       */
      capture(args?: AppToolArguments): HostEvidenceAction
    };
    files: {
      /**
       * Return an action to retain a known root-relative sandbox file. Version 2 requires
       * files.capture and an available external provider.
       */
      capture(args: { root?: string; path: string; maximumBytes?: number }): HostEvidenceAction
    };
    annotations: {
      /** Return an action to mark the event's time or screenshot with review context. */
      create(args: AppToolArguments): HostEvidenceAction
    };
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
