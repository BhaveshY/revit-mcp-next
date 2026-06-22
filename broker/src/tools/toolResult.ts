import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import type { BridgeResponse } from "@revit-mcp-next/contracts";

export function asToolResult<T>(
  response: BridgeResponse<T>,
  summarize: (data: T) => string
): CallToolResult {
  if (!response.ok) {
    return {
      isError: true,
      content: [
        {
          type: "text",
          text: `${response.error.code}: ${response.error.message}`,
        },
      ],
      structuredContent: {
        data: {
          error: response.error,
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
        text: summarize(response.data),
      },
    ],
    structuredContent: {
      data: response.data,
      warnings: response.warnings,
      metrics: response.metrics,
      generation: response.generation,
    },
  };
}
