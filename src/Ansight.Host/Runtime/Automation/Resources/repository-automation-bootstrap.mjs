import { pathToFileURL } from "node:url";

const modulePath = process.argv[1];
console.log = (...values) => console.error(...values);
console.info = (...values) => console.error(...values);
console.debug = (...values) => console.error(...values);

const automationModule = await import(pathToFileURL(modulePath).href);
if (typeof automationModule.default !== "function") {
  throw new TypeError("The trigger module must default-export a function.");
}

process.stdout.write(`${JSON.stringify({ type: "ready" })}\n`);

const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);
const input = JSON.parse(Buffer.concat(chunks).toString("utf8"));

const createAppToolAction = (toolId, args = {}) => {
    if (input.capabilities?.appAvailable === false) {
      const error = new Error(`App tool '${toolId}' requires a connected SDK tool provider. Add the Ansight SDK and the relevant tools to the app, then start an SDK session. Setup: https://www.ansight.ai/docs/sdk/`);
      Object.assign(error, { code: "capability_unavailable", capability: `app.tool:${toolId}`, retryable: false });
      throw error;
    }
    if (typeof toolId !== "string" || toolId.trim().length === 0) {
      throw new TypeError("app.callTool requires a tool id.");
    }

    if (args === null || typeof args !== "object" || Array.isArray(args)) {
      throw new TypeError("app.callTool arguments must be an object.");
    }

    return {
      type: "appTool",
      toolId: toolId.trim(),
      arguments: args
    };
};
const standardSuites = Object.fromEntries(
  Object.entries(input.appSuites).map(([suiteName, methods]) => [
    suiteName,
    Object.freeze(Object.fromEntries(
      Object.entries(methods).map(([methodName, toolId]) => [
        methodName,
        (args = {}) => createAppToolAction(toolId, args)
      ])
    ))
  ])
);
const app = Object.freeze({
  available: input.capabilities?.appAvailable ?? true,
  ...standardSuites,
  callTool(toolId, args = {}) {
    return createAppToolAction(toolId, args);
  }
});
const hostAction = (toolName, args = {}) => ({ type: "hostTool", toolName, arguments: args });
const ansight = Object.freeze({
  capabilities: Object.freeze(input.capabilities ?? {}),
  screenshots: Object.freeze({ capture: (args = {}) => hostAction("ansight_take_screenshot", args) }),
  files: Object.freeze({ capture: args => hostAction("ansight_capture_sandbox_file", args) }),
  annotations: Object.freeze({ create: args => hostAction("ansight_create_annotation", args) })
});
try {
  const action = await automationModule.default(Object.freeze({ run: input.run, event: input.event, app, ansight }));
  process.stdout.write(`${JSON.stringify({ type: "result", action: action ?? null })}\n`);
} catch (error) {
  if (error?.code !== "capability_unavailable") throw error;
  process.stdout.write(`${JSON.stringify({ type: "result", error: { code: error.code, capability: error.capability,
    retryable: false, message: error.message } })}\n`);
}
