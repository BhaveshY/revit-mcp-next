import type { CallToolResult } from "@modelcontextprotocol/server";
import type { BridgeResponse, RevitTarget } from "@revit-mcp-next/contracts";

type ToolDataShape = {
  truncated?: unknown;
  cursor?: unknown;
};

type RevitToolResult = CallToolResult & {
  structuredContent: {
    data: unknown;
    warnings: unknown;
    metrics: unknown;
    generation?: unknown;
    target?: unknown;
  };
};

export function asToolResult<T>(
  response: BridgeResponse<T>,
  summarize: (data: T) => string
): RevitToolResult {
  if (!response.ok) {
    const suggestedNextAction = response.error.suggestedNextAction
      ? ` Next: ${response.error.suggestedNextAction}`
      : "";

    return {
      isError: true,
      content: [
        {
          type: "text",
          text: appendTargetIdentity(`${response.error.code}: ${response.error.message}${suggestedNextAction}`, response.target),
        },
      ],
      structuredContent: {
        data: {
          error: sanitizeStructuredValue(response.error),
        },
        warnings: response.warnings,
        metrics: response.metrics ?? { elapsedMs: 0 },
        ...(response.target ? { target: sanitizeStructuredValue(response.target) } : {}),
      },
    };
  }

  return {
    content: [
      {
        type: "text",
        text: appendTargetIdentity(appendResultHints(summarize(response.data), response.data), response.target),
      },
    ],
    structuredContent: {
      data: sanitizeStructuredValue(response.data),
      warnings: response.warnings,
      metrics: response.metrics,
      ...(response.generation === undefined ? {} : { generation: response.generation }),
      ...(response.target ? { target: sanitizeStructuredValue(response.target) } : {}),
    },
  };
}

function appendResultHints(text: string, data: unknown): string {
  if (!isRecord(data) || data.truncated !== true) return text;

  const cursor = typeof data.cursor === "string" && data.cursor.length > 0 ? data.cursor : undefined;
  const hint = cursor
    ? "More results available; call the same tool with the same arguments and structuredContent.data.cursor."
    : "Result was truncated; narrow the filters or request the next page if the tool returned a cursor.";
  const separator = text.endsWith(".") ? " " : ". ";
  return `${text}${separator}${hint}`;
}

function appendTargetIdentity(text: string, target: RevitTarget | undefined): string {
  if (!target) return text;
  const version = target.revitVersion ? `Revit ${target.revitVersion}` : "Revit";
  const process = target.processId ? ` process ${target.processId}` : "";
  const path = target.documentPath ? `, path ${target.documentPath}` : "";
  const central = target.centralModelPath ? `, central ${target.centralModelPath}` : "";
  return `${text} Target: ${version}${process}, instance ${target.instanceId}, document "${target.documentTitle}" (${target.documentFingerprint}), generation ${target.generation}${path}${central}.`;
}

function isRecord(value: unknown): value is ToolDataShape {
  return typeof value === "object" && value !== null;
}

function sanitizeStructuredValue(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => (item === undefined ? null : sanitizeStructuredValue(item)));
  }

  if (!isPlainRecord(value)) return value;

  const sanitized: Record<string, unknown> = {};
  for (const [key, child] of Object.entries(value)) {
    if (child !== undefined) sanitized[key] = sanitizeStructuredValue(child);
  }
  return sanitized;
}

function isPlainRecord(value: unknown): value is Record<string, unknown> {
  return Object.prototype.toString.call(value) === "[object Object]";
}
