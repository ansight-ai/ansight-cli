# Sanitizers

A sanitizer is a privacy filter for a captured session. It receives logs,
network requests, screenshots, visual trees, annotations, artifacts, and other session items before
an exported copy is written. It can redact a value, keep it, or return `null` to
remove it from the copy.

Read the full [Ansight workspace sanitizers guide](https://www.ansight.ai/docs/workspace/sanitizers).

Create a safe starter module with:

```sh
ansight workspace add sanitizer . team-safe
```

The generated TypeScript uses the built-in PII matcher for structured content
and combines screenshot OCR with visual-tree text. If neither source can inspect
a screenshot, the starter fails closed by redacting the whole image.

A small custom function looks like this:

```ts
import type * as AnsightSanitizer from "./ansight-sanitizer.d.ts";

export const sanitizeLog: AnsightSanitizer.SanitizeLog = (log, { pii }) => {
  return { ...log, message: pii.redact(log.message) };
};

export const sanitizeArtifact: AnsightSanitizer.SanitizeArtifact = (artifact) => {
  return artifact.isBinary ? null : artifact;
};

export const sanitizeNetworkRequest: AnsightSanitizer.SanitizeNetworkRequest = (request) => {
  return { ...request, requestHeaders: [], responseHeaders: [] };
};
```

Sanitization writes a new portable ZIP; it does not modify the local capture.
Review a custom sanitizer with representative data before relying on it for
sharing. OCR can miss text, and binary artifacts need an explicit keep/remove
policy.

```sh
npx tsc -p ansight/sanitizers/tsconfig.json
ansight session sanitize <session-id> sanitized-session.zip \
  --sanitizer ansight/sanitizers/team-safe.ts
```

`ansight-sanitizer.d.ts` and `tsconfig.json` are generated support files. Put
your privacy policy in your own `.ts` modules.
