import { fileURLToPath, pathToFileURL } from "node:url";
import { createInterface } from "node:readline";
import { isDeepStrictEqual } from "node:util";

const modulePath = process.argv[1];
console.log = (...values) => console.error(...values);
console.info = (...values) => console.error(...values);
console.debug = (...values) => console.error(...values);

// Retain the exact bytes supplied by the loader, before Node strips TypeScript.
// Emit each module immediately so failures and cancellation retain loaded source.
if (process.argv[2] === "trace") {
  const { registerHooks } = await import("node:module");
  const { relative, isAbsolute, sep } = await import("node:path");
  const { createHash } = await import("node:crypto");
  const { realpathSync } = await import("node:fs");
  // Node resolves module URLs through symlinks (including macOS /var -> /private/var).
  const repositoryRoot = realpathSync(process.argv[3]);
  const emitSource = value => process.stdout.write(`${JSON.stringify({ type: "source", ...value })}\n`);
  if (typeof registerHooks !== "function") {
    emitSource({ error: "Source capture requires Node.js 22.15 or later with module load hooks." });
  } else {
    let remainingCharacters = 4 * 1024 * 1024;
    registerHooks({
      load(url, context, nextLoad) {
        const loaded = nextLoad(url, context);
        if (!url.startsWith("file:") || loaded.source == null) return loaded;
        const path = relative(repositoryRoot, fileURLToPath(url));
        if (isAbsolute(path) || path === ".." || path.startsWith(`..${sep}`)
          || path.split(sep).includes("node_modules")) return loaded;
        const source = typeof loaded.source === "string" ? loaded.source : Buffer.from(loaded.source).toString("utf8");
        let retainedLength = Math.min(source.length, 262144, remainingCharacters);
        if (retainedLength > 0 && /[\uD800-\uDBFF]/.test(source[retainedLength - 1])) retainedLength--;
        remainingCharacters -= retainedLength;
        emitSource({ module: {
          path: path.split(sep).join("/"),
          language: /\.[cm]?tsx?$/i.test(path) ? "typescript" : /\.json$/i.test(path) ? "json" : "javascript",
          content: source.slice(0, retainedLength),
          sha256: createHash("sha256").update(source).digest("hex"),
          originalCharacterCount: source.length,
          wasTruncated: retainedLength < source.length
        } });
        return loaded;
      }
    });
  }
}

const taskModule = await import(pathToFileURL(modulePath).href);
if (typeof taskModule.default !== "function") {
  throw new TypeError("The task module must default-export a function.");
}

process.stdout.write(`${JSON.stringify({ type: "ready" })}\n`);
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity })[Symbol.asyncIterator]();
const firstLine = await lines.next();
if (firstLine.done) throw new Error("The Ansight task host did not provide an invocation.");
const invocation = JSON.parse(firstLine.value);
const assertions = [];
let sequence = 0;
let callInFlight = false;

class TaskAssertionError extends Error {}
class ToolError extends Error {}
class CapabilityUnavailableError extends ToolError {
  constructor(message, details = {}) {
    super(message);
    this.name = "CapabilityUnavailableError";
    this.code = "capability_unavailable";
    this.retryable = false;
    Object.assign(this, details);
  }
}

const requireMetadata = metadata => {
  if (metadata === null || typeof metadata !== "object" || Array.isArray(metadata)) {
    throw new TypeError("expect requires an assertion metadata object.");
  }
  if (typeof metadata.id !== "string" || metadata.id.trim().length === 0) {
    throw new TypeError("Expectations require a non-empty id.");
  }
  const normalized = metadata.id.trim();
  if (assertions.some(assertion => assertion.assertionId === normalized)) {
    throw new TypeError(`Assertion id '${normalized}' is duplicated.`);
  }
  return {
    id: normalized,
    message: typeof metadata.message === "string" && metadata.message.trim().length > 0
      ? metadata.message.trim()
      : null
  };
};

const record = (metadata, passed, defaultMessage, expected, actual, soft) => {
  const normalized = requireMetadata(metadata);
  const assertion = {
    assertionId: normalized.id,
    passed,
    message: normalized.message ?? defaultMessage,
    expected: expected ?? null,
    actual: actual ?? null
  };
  assertions.push(assertion);
  if (!passed && !soft) throw new TaskAssertionError(assertion.message);
  return actual;
};

const createMatchers = (actual, metadata, soft, negated = false) => {
  const finish = (matched, positiveMessage, negativeMessage, expected) => record(
    metadata,
    negated ? !matched : matched,
    negated ? negativeMessage : positiveMessage,
    negated ? { not: expected } : expected,
    actual,
    soft);
  const matchers = {
    toBe(expected) {
      return finish(
        Object.is(actual, expected),
        "Expected values to be identical.",
        "Expected values not to be identical.",
        expected);
    },
    toEqual(expected) {
      return finish(
        isDeepStrictEqual(actual, expected),
        "Expected values to be deeply equal.",
        "Expected values not to be deeply equal.",
        expected);
    },
    toBeTruthy() {
      return finish(
        Boolean(actual),
        "Expected a truthy value.",
        "Expected a falsy value.",
        true);
    },
    toBeFalsy() {
      return finish(
        !actual,
        "Expected a falsy value.",
        "Expected a truthy value.",
        false);
    },
    toBeDefined() {
      return finish(
        actual !== undefined,
        "Expected a defined value.",
        "Expected an undefined value.",
        { defined: true });
    },
    toBeUndefined() {
      return finish(
        actual === undefined,
        "Expected an undefined value.",
        "Expected a defined value.",
        null);
    },
    toBeNull() {
      return finish(
        actual === null,
        "Expected null.",
        "Expected a non-null value.",
        null);
    },
    toContain(expected) {
      const matched = typeof actual === "string" && typeof expected === "string"
        ? actual.includes(expected)
        : Array.isArray(actual) && actual.some(value => Object.is(value, expected));
      return finish(
        matched,
        "Expected the value to contain the item.",
        "Expected the value not to contain the item.",
        { contains: expected });
    },
    toContainEqual(expected) {
      const matched = Array.isArray(actual)
        && actual.some(value => isDeepStrictEqual(value, expected));
      return finish(
        matched,
        "Expected the collection to contain a deeply equal item.",
        "Expected the collection not to contain a deeply equal item.",
        { containsEqual: expected });
    }
  };
  Object.defineProperty(matchers, "not", {
    enumerable: true,
    get() {
      return createMatchers(actual, metadata, soft, !negated);
    }
  });
  return Object.freeze(matchers);
};

const expect = (actual, metadata) => createMatchers(actual, metadata, false);
expect.soft = (actual, metadata) => createMatchers(actual, metadata, true);
Object.freeze(expect);

const sendCall = async (kind, name, args = {}) => {
  if (callInFlight) throw new ToolError("Task scripts must await each Ansight call before starting another.");
  if (args === null || typeof args !== "object" || Array.isArray(args)) {
    throw new TypeError("Ansight task call arguments must be an object.");
  }

  callInFlight = true;
  const callId = `task-call-${++sequence}`;
  process.stdout.write(`${JSON.stringify({ type: "call", callId, kind, name, arguments: args })}\n`);
  const responseLine = await lines.next();
  callInFlight = false;
  if (responseLine.done) throw new ToolError("The Ansight task host closed during a tool call.");
  const response = JSON.parse(responseLine.value);
  if (response.type !== "callResult" || response.callId !== callId) {
    throw new ToolError("The Ansight task host returned an out-of-sequence tool response.");
  }
  if (!response.ok) {
    if (response.result?.code === "capability_unavailable")
      throw new CapabilityUnavailableError(response.message, response.result);
    const ErrorType = response.failureKind === "assertion" ? TaskAssertionError : ToolError;
    throw new ErrorType(response.message || `${name} failed.`);
  }
  return response.result ?? null;
};

const bindMethods = (methods, invoke) => Object.fromEntries(
  Object.entries(methods).map(([methodName, toolName]) => [
    methodName,
    (args = {}) => invoke(toolName, args)
  ])
);
const bindSuites = (suites, invoke, result = {}) => {
  for (const [suiteName, methods] of Object.entries(suites)) {
    const parts = suiteName.split(".");
    let target = result;
    for (const part of parts) target = target[part] ??= {};
    Object.assign(target, bindMethods(methods, invoke));
  }
  const freeze = value => {
    for (const child of Object.values(value)) if (typeof child === "object") freeze(child);
    return Object.freeze(value);
  };
  return freeze(result);
};

const callHostMethod = (name, args = {}) => sendCall("hostMethod", name, args);
const callTaskMethod = (name, args = {}) => sendCall("taskMethod", name, args);
const callHostTool = (name, args = {}) => {
  if (typeof name !== "string" || name.trim().length === 0) {
    throw new TypeError("ansight.callTool requires a tool name.");
  }
  return sendCall("hostTool", name.trim(), args);
};
const requireApp = toolId => {
  if (invocation.capabilities?.appAvailable === false)
    throw new CapabilityUnavailableError(`App tool '${toolId}' requires a connected SDK tool provider. Add the Ansight SDK and the relevant tools to the app, then start an SDK session. Setup: https://www.ansight.ai/docs/sdk/`,
      { capability: `app.tool:${toolId}`, executionMode: invocation.run.executionMode });
};
const callAppMethod = (toolId, args = {}) => {
  requireApp(toolId);
  return sendCall("appMethod", toolId, args);
};
const callAppTool = (toolId, args = {}) => {
  if (typeof toolId !== "string" || toolId.trim().length === 0) {
    throw new TypeError("app.callTool requires a tool id.");
  }
  requireApp(toolId);
  return sendCall("appTool", toolId.trim(), args);
};
const permissionNames = constants => Object.fromEntries(
  Object.entries(constants).map(([name, value]) => [name[0].toLowerCase() + name.slice(1), value])
);
const permissionSuites = {
  permissions: {
    names: permissionNames(Permission),
    ios: { names: permissionNames(IosPermission) },
    android: { names: permissionNames(AndroidPermission) }
  }
};

const ansight = Object.freeze({
  ...bindSuites(invocation.hostSuites, callHostMethod, permissionSuites),
  ...bindSuites(invocation.taskSuites, callTaskMethod),
  spans: Object.freeze({
    async measure(name, operation, options = {}) {
      if (typeof operation !== "function") throw new TypeError("spans.measure requires an operation function.");
      const { spanId } = await callHostMethod("ansight_record_host_span", { phase: "start", name, group: options.group });
      try {
        const result = await operation();
        await callHostMethod("ansight_record_host_span", { phase: "completed", spanId });
        return result;
      } catch (error) {
        try { await callHostMethod("ansight_record_host_span", { phase: "failed", spanId }); } catch { }
        throw error;
      }
    }
  }),
  callTool(name, args = {}) {
    return callHostTool(name, args);
  }
});
const app = Object.freeze({
  available: invocation.capabilities?.appAvailable ?? true,
  ...bindSuites(invocation.appSuites, callAppMethod),
  callTool(toolId, args = {}) {
    return callAppTool(toolId, args);
  }
});

try {
  const output = await taskModule.default(Object.freeze({
    run: Object.freeze(invocation.run),
    input: Object.freeze(invocation.input),
    secrets: Object.freeze({
      get(alias) {
        if (typeof alias !== "string" || !Object.hasOwn(invocation.secrets ?? {}, alias)) return undefined;
        return invocation.secrets[alias];
      },
      require(alias) {
        if (typeof alias !== "string" || !Object.hasOwn(invocation.secrets ?? {}, alias))
          throw new Error(`Secret '${alias}' was not granted to this task.`);
        return invocation.secrets[alias];
      }
    }),
    ansight,
    app,
    expect
  }));
  const failedAssertionCount = assertions.filter(assertion => !assertion.passed).length;
  const status = failedAssertionCount > 0
    ? "failed"
    : assertions.length === 0
      ? "inconclusive"
      : "passed";
  process.stdout.write(`${JSON.stringify({
    type: "result",
    status,
    message: status === "failed"
      ? `Task failed ${failedAssertionCount} of ${assertions.length} assertion(s).`
      : status === "inconclusive"
        ? "Task completed without assertions."
        : `Task passed ${assertions.length} assertion(s).`,
    output: output ?? null,
    assertions
  })}\n`);
} catch (error) {
  const failed = error instanceof TaskAssertionError;
  process.stdout.write(`${JSON.stringify({
    type: "result",
    status: failed ? "failed" : "error",
    message: error instanceof Error ? error.message : String(error),
    output: null,
    assertions
  })}\n`);
}
