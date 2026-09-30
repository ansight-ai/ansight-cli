import { pathToFileURL } from "node:url";
import { createInterface } from "node:readline";

const modulePath = process.argv[1];
console.log = (...values) => console.error(...values);
console.info = (...values) => console.error(...values);
console.debug = (...values) => console.error(...values);

const sanitizer = await import(pathToFileURL(modulePath).href);
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
const patterns = [
  ["email", /(?<![\w.+-])[\w.!#$%&'*+/=?^`{|}~-]+@[\w-]+(?:\.[\w-]+)+/gi],
  ["phone", /(?<!\d)(?:\+\d[\d ()-]{7,}\d|\(\d{2,4}\)[\d ()-]{5,}\d|\d{9,15})(?!\d)/g],
  ["ipAddress", /(?<!\d)(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?!\d)/g],
  ["creditCard", /(?<!\d)(?:\d[ -]*?){13,19}(?!\d)/g],
  ["credential", /(?:bearer\s+[a-z0-9._~+/-]+=*|(?:api[_-]?key|access[_-]?token|auth(?:orization)?)[\s:=]+[^\s,;]+|sk-[a-z0-9_-]{12,})/gi]
];
const sensitiveKeys = new Set([
  "accessibilitylabel", "address", "author", "authorization", "birthdate", "clientname",
  "contentdescription", "cookie", "displayname", "email", "firstname", "fullname",
  "lastname", "name", "password", "path", "phone", "relativepath", "rootpath", "secret",
  "text", "token", "username"
]);
const normalizeKey = value => String(value).replace(/[^a-z0-9]/gi, "").toLowerCase();
const clone = value => value === undefined ? undefined : JSON.parse(JSON.stringify(value));

process.stdout.write(`${JSON.stringify({ type: "ready" })}\n`);
for await (const line of lines) {
  let redactions = 0;
  try {
    const request = JSON.parse(line);
    const item = clone(request.item);
    const redactText = (value, replacement = "[REDACTED]") => {
      if (typeof value !== "string") return value;
      let result = value;
      for (const [, pattern] of patterns) {
        pattern.lastIndex = 0;
        result = result.replace(pattern, () => {
          redactions += 1;
          return replacement;
        });
      }
      return result;
    };
    const redactObject = (value, replacement = "[REDACTED]") => {
      const result = clone(value);
      const visit = (current, key = "") => {
        if (Array.isArray(current)) {
          for (let index = 0; index < current.length; index += 1) {
            const value = current[index];
            current[index] = typeof value === "string" ? redactText(value, replacement) : visit(value, key);
          }
        } else if (current && typeof current === "object") {
          for (const [childKey, childValue] of Object.entries(current)) {
            if (typeof childValue === "string") {
              if (sensitiveKeys.has(normalizeKey(childKey))) {
                if (childValue !== replacement) redactions += 1;
                current[childKey] = replacement;
              } else {
                current[childKey] = redactText(childValue, replacement);
              }
            } else {
              visit(childValue, childKey);
            }
          }
        }
        return current;
      };
      return visit(result);
    };
    const pii = Object.freeze({
      matches(value) {
        if (typeof value !== "string") return false;
        return patterns.some(([, pattern]) => {
          pattern.lastIndex = 0;
          return pattern.test(value);
        });
      },
      redact: redactText,
      redactObject
    });
    const sanitization = (action, regions = []) => ({
      ...item,
      sanitization: { action, regions: clone(regions) }
    });
    const image = Object.freeze({
      keep: () => sanitization("keep"),
      remove: () => sanitization("remove"),
      redact: regions => sanitization("redact", regions),
      redactAll: () => sanitization("redactAll")
    });
    const ocr = Object.freeze({
      scan: async () => clone(request.context?.ocr ?? {
        available: false,
        provider: null,
        blocks: [],
        message: "OCR was not available."
      })
    });
    const operation = Object.freeze(clone(request.context?.operation ?? { kind: "export", share: null }));
    const tools = Object.freeze({ pii, ocr, image, operation, visualText: clone(request.context?.visualText ?? []) });
    const namedHandler = sanitizer[request.handler];
    const handler = typeof namedHandler === "function"
      ? namedHandler
      : (typeof sanitizer.sanitizeDefault === "function" ? sanitizer.sanitizeDefault : null);
    let value;
    if (handler) {
      value = await handler(item, tools);
    } else if (request.handler === "sanitizeScreenshot") {
      value = image.redactAll();
    } else if (request.handler === "sanitizeArtifact" && item?.isBinary) {
      value = null;
    } else {
      value = redactObject(item);
    }
    if (value !== null && (typeof value !== "object" || Array.isArray(value))) {
      throw new TypeError(`${request.handler} must return an object or null.`);
    }
    process.stdout.write(`${JSON.stringify({ type: "result", value: value ?? null, redactions })}\n`);
  } catch (error) {
    process.stdout.write(`${JSON.stringify({
      type: "error",
      message: error instanceof Error ? error.message : String(error)
    })}\n`);
  }
}
