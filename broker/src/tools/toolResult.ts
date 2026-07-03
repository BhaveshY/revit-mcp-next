import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { BridgeResponse } from "@revit-mcp-next/contracts";

type ToolDataShape = {
  truncated?: unknown;
  cursor?: unknown;
};

export function asToolResult<T>(
  response: BridgeResponse<T>,
  summarize: (data: T) => string
): CallToolResult {
  if (!response.ok) {
    const suggestedNextAction = response.error.suggestedNextAction
      ? ` Next: ${response.error.suggestedNextAction}`
      : "";

    return {
      isError: true,
      content: [
        {
          type: "text",
          text: `${response.error.code}: ${response.error.message}${suggestedNextAction}`,
        },
      ],
      structuredContent: {
        data: {
          error: sanitizeStructuredValue(response.error),
        },
        warnings: response.warnings,
        metrics: response.metrics ?? { elapsedMs: 0 },
      },
    };
  }

  return {
    content: [
      {
        type: "text",
        text: appendResultHints(summarize(response.data), response.data),
      },
    ],
    structuredContent: {
      data: sanitizeStructuredValue(response.data),
      warnings: response.warnings,
      metrics: response.metrics,
      generation: response.generation,
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
