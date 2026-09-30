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
  matches(value: string): boolean;
  redact(value: string, replacement?: string): string;
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
  pii: PiiTools;
  ocr: { scan(screenshot: T): Promise<OcrResult> };
  image: {
    keep(): T & { sanitization: ScreenshotSanitization };
    remove(): T & { sanitization: ScreenshotSanitization };
    redact(regions: Bounds[]): T & { sanitization: ScreenshotSanitization };
    redactAll(): T & { sanitization: ScreenshotSanitization };
  };
  operation: SanitizerOperationContext;
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
