import type * as AnsightSanitizer from "./ansight-sanitizer.d.ts";

export const sanitizeSession: AnsightSanitizer.SanitizeSession = (session, { pii }) => {
  return pii.redactObject(session);
};

export const sanitizeLog: AnsightSanitizer.SanitizeLog = (log, { pii }) => {
  return pii.redactObject(log);
};

export const sanitizeApplicationEvent: AnsightSanitizer.SanitizeApplicationEvent = (event, { pii }) => {
  return pii.redactObject(event);
};

export const sanitizeNetworkRequest: AnsightSanitizer.SanitizeNetworkRequest = (request, { pii }) => {
  const sanitizeHeaders = (headers: AnsightSanitizer.NetworkHeader[]) =>
    headers.map(header => ({ ...header, value: pii.redact(header.value) }));
  const sanitizeBody = (body: AnsightSanitizer.NetworkBody | null | undefined) =>
    body?.encoding === "utf8" ? { ...body, data: pii.redact(body.data) } : undefined;
  return {
    ...request,
    url: pii.redact(request.url),
    requestHeaders: sanitizeHeaders(request.requestHeaders),
    requestBody: sanitizeBody(request.requestBody),
    responseHeaders: sanitizeHeaders(request.responseHeaders),
    responseBody: sanitizeBody(request.responseBody),
    reasonPhrase: request.reasonPhrase ? pii.redact(request.reasonPhrase) : request.reasonPhrase,
    errorMessage: request.errorMessage ? pii.redact(request.errorMessage) : request.errorMessage
  };
};

export const sanitizeVisualTree: AnsightSanitizer.SanitizeVisualTree = (tree, { pii }) => {
  return pii.redactObject(tree);
};

export const sanitizeScreenshot: AnsightSanitizer.SanitizeScreenshot = async (
  screenshot,
  { ocr, image, pii, visualText }) => {
  const scan = await ocr.scan(screenshot);
  const candidates = [...scan.blocks, ...visualText];
  const sensitive = candidates.filter(block => pii.matches(block.text));
  if (sensitive.length > 0) {
    return image.redact(sensitive.map(block => block.bounds));
  }

  // If neither OCR nor the visual tree could inspect the image, fail closed.
  return scan.available || visualText.length > 0 ? image.keep() : image.redactAll();
};

export const sanitizeAnnotation: AnsightSanitizer.SanitizeAnnotation = (annotation, { pii }) => {
  return pii.redactObject(annotation);
};

export const sanitizeAnalysis: AnsightSanitizer.SanitizeAnalysis = (analysis, { pii }) => {
  return pii.redactObject(analysis);
};

export const sanitizeArtifact: AnsightSanitizer.SanitizeArtifact = (artifact, { pii }) => {
  if (!("content" in artifact)) return pii.redactObject(artifact);
  if (artifact.isBinary) return null;
  return { ...artifact, content: pii.redact(artifact.content ?? "") };
};

export const sanitizeDefault: AnsightSanitizer.SanitizeDefault = (value, { pii }) => {
  return pii.redactObject(value);
};
