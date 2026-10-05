export interface Bounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface TextBlock {
  text: string;
  confidence: number;
  bounds: Bounds;
}

export interface OcrResult {
  available: boolean;
  provider: string | null;
  blocks: TextBlock[];
  message: string | null;
}

export interface PiiTools {
  /** Test whether a string matches the configured PII detectors before deciding to keep or remove a field. */
  matches(value: string): boolean;
  /** Replace detected PII in one string, optionally using a caller-supplied replacement marker. */
  redact(value: string, replacement?: string): string;
  /** Return a redacted copy of a structured value while preserving its shape. */
  redactObject<T>(value: T, replacement?: string): T;
}

export interface ScreenshotSanitization {
  action: "keep" | "redact" | "redactAll" | "remove";
  regions: Bounds[];
}

export interface ScreenshotItem extends Record<string, unknown> {
  frameId: string;
  width: number;
  height: number;
  sanitization?: ScreenshotSanitization;
}

export interface LogItem extends Record<string, unknown> {
  message: string;
}

export interface NetworkHeader {
  name: string;
  value: string;
}

export interface NetworkBody {
  contentType?: string | null;
  encoding: "utf8" | "base64";
  data: string;
  capturedBytes: number;
  totalBytes?: number | null;
  truncated: boolean;
}

export interface NetworkRequestItem extends Record<string, unknown> {
  schema: "ansight.network-request.v1";
  id: string;
  source: string;
  startedAtUtc: string;
  completedAtUtc: string;
  durationMilliseconds: number;
  method: string;
  url: string;
  protocol?: string | null;
  requestHeaders: NetworkHeader[];
  requestBodySizeBytes?: number | null;
  requestBody?: NetworkBody | null;
  statusCode?: number | null;
  reasonPhrase?: string | null;
  responseHeaders: NetworkHeader[];
  responseBodySizeBytes?: number | null;
  responseBody?: NetworkBody | null;
  errorType?: string | null;
  errorMessage?: string | null;
}

export interface ArtifactItem extends Record<string, unknown> {
  name?: string;
  extension?: string;
  sizeBytes?: number;
  isBinary?: boolean;
  content?: string | null;
  keepBinary?: boolean;
}

/** Closed string enum matching the Share Session access selection. */
export type SanitizerShareAudience = "team" | "public_with_auth" | "public_no_auth";

export type SanitizerOperationContext =
  | { kind: "export"; share: null }
  | {
      kind: "share";
      share: {
        audience: SanitizerShareAudience;
        teamId: string;
      };
    };

export interface SanitizerTools<T extends Record<string, unknown>> {
  /** Text and object redaction helpers for fields that may contain personal data. */
  pii: PiiTools;
  ocr: {
    /** Scan a screenshot for visible text before choosing regions to redact; inspect available before relying on the blocks. */
    scan(screenshot: T): Promise<OcrResult>
  };
  image: {
    /** Keep the screenshot explicitly after evaluating its sensitivity. */
    keep(): T & { sanitization: ScreenshotSanitization };
    /** Remove this screenshot from the sanitized copy when safe redaction is not possible. */
    remove(): T & { sanitization: ScreenshotSanitization };
    /** Mask selected pixel bounds, for example regions found through OCR or app-specific layout knowledge. */
    redact(regions: Bounds[]): T & { sanitization: ScreenshotSanitization };
    /** Mask the entire screenshot while retaining its place in the session sequence. */
    redactAll(): T & { sanitization: ScreenshotSanitization };
  };
  /** Distinguish local export from sharing and inspect the requested audience before applying stricter rules. */
  operation: SanitizerOperationContext;
  /** Text blocks already detected in the current visual evidence, when available. */
  visualText: TextBlock[];
}

export type SanitizerResult<T> = T | null | Promise<T | null>;
export type SanitizerFunction<T extends Record<string, unknown> = Record<string, unknown>> =
  (item: T, tools: SanitizerTools<T>) => SanitizerResult<T>;
export type SanitizeSession = SanitizerFunction;
export type SanitizeLog = SanitizerFunction<LogItem>;
export type SanitizeApplicationEvent = SanitizerFunction;
export type SanitizeNetworkRequest = SanitizerFunction<NetworkRequestItem>;
export type SanitizeVisualTree = SanitizerFunction;
export type SanitizeScreenshot = SanitizerFunction<ScreenshotItem>;
export type SanitizeAnnotation = SanitizerFunction;
export type SanitizeAnalysis = SanitizerFunction;
export type SanitizeArtifact = SanitizerFunction<ArtifactItem>;
export type SanitizeDefault = SanitizerFunction;
