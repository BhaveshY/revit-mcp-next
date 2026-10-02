// MCP server factory (SPEC §5.1, §5.7, §5.8): one McpServer + Session per connection. Tools are registered from the
// catalog with fromJsonSchema(advertisedSchema, PERMISSIVE_VALIDATOR), so tools/list serves the catalog JSON verbatim
// and the SDK never rejects arguments (lenient validation runs in the handler and yields §5.6 errors).
// Handlers never throw. No outputSchema. Frozen in wave 2.

import { acceptedContent, completable, fromJsonSchema, inputRequired, McpServer, ResourceTemplate } from "@modelcontextprotocol/server";
import * as z from "zod";
import { advertisedSchema } from "@revit-mcp-next/contracts/catalog";
import { INSTRUCTIONS } from "@revit-mcp-next/contracts/catalog/instructions";
import { TOPICS, WORKFLOW_NAMES, workflowTopic } from "@revit-mcp-next/contracts/catalog/topics";
import { runTool, type PipelineDeps } from "./framework/context.js";
import { defaultHandler } from "./framework/defaults.js";
import { helpFor, helpIndex, helpNames } from "./framework/help.js";
import { renderOutcome, renderText } from "./framework/render.js";
import { Session } from "./framework/session.js";
import type { BrokerServices, ToolOutcome } from "./framework/types.js";
import { ulid } from "./runtime/ids.js";

export const SERVER_NAME = "revit-mcp-next";
export const CACHE_TTL_MS = 300_000;

/** Validator that accepts every argument object unchanged (SPEC §5.1). */
export const PERMISSIVE_VALIDATOR = {
  getValidator: () => (data: unknown) => ({ valid: true as const, data, errorMessage: undefined }),
};

interface McpCtx {
  mcpReq: {
    _meta?: { progressToken?: string | number; [k: string]: unknown };
    signal: AbortSignal;
    notify: (n: { method: string; params?: Record<string, unknown> }) => Promise<void>;
    requestState?: () => unknown;
    inputResponses?: Record<string, unknown>;
    envelope?: Record<string, unknown>;
  };
}

export function createServerFactory(services: BrokerServices) {
  const deps: PipelineDeps = {
    services,
    modules: (tool) => services.tools.module(tool),
    defaults: (ctx) => defaultHandler(ctx),
  };
  const listed = services.tools.listed();
  const names = helpNames(listed);

  return () => {
    const cache = { ttlMs: CACHE_TTL_MS, cacheScope: "private" as const };
    const server = new McpServer(
      { name: SERVER_NAME, version: services.build.version },
      {
        instructions: INSTRUCTIONS,
        cacheHints: {
          "server/discover": cache,
          "tools/list": cache,
          "prompts/list": cache,
          "resources/list": cache,
          "resources/templates/list": cache,
          "resources/read": cache,
        },
      }
    );
    const session = new Session(ulid());

    const updateClient = (ctx: McpCtx | undefined) => {
      try {
        const info = server.server.getClientVersion?.();
        if (info?.name) {
          session.client.name = info.name;
          session.client.version = info.version ?? "";
        } else {
          const envelopeInfo = ctx?.mcpReq.envelope?.["io.modelcontextprotocol/clientInfo"] as { name?: string; version?: string } | undefined;
          if (envelopeInfo?.name) {
            session.client.name = envelopeInfo.name;
            session.client.version = envelopeInfo.version ?? "";
          }
        }
        const caps = server.server.getClientCapabilities?.();
        session.client.elicitation = !!caps?.elicitation;
        const negotiated = (server.server as unknown as { _negotiatedProtocolVersion?: string })._negotiatedProtocolVersion;
        if (negotiated) session.client.protocol = negotiated;
      } catch {
        // client info is best effort
      }
    };

    for (const spec of listed) {
      const handler = async (args: unknown, ctx: McpCtx) => {
        try {
          updateClient(ctx);
          let rawArgs = args;
          let reentry = false;
          // Elicitation re-entry (useElicitation): the SDK re-invokes the handler with the user's answer.
          const state = typeof ctx?.mcpReq.requestState === "function" ? ctx.mcpReq.requestState() : undefined;
          if (typeof state === "string" && state.startsWith("confirm:")) {
            reentry = true;
            const token = state.slice("confirm:".length);
            const accepted = acceptedContent<{ apply?: boolean }>(ctx.mcpReq.inputResponses, "confirm");
            if (accepted?.apply === true) {
              const disc = spec.discriminator;
              const op = disc && args && typeof args === "object" ? (args as Record<string, unknown>)[disc] : undefined;
              rawArgs = { ...(disc && op ? { [disc]: op } : {}), confirm: token };
            } else {
              session.confirms.discard(token);
              const declined: ToolOutcome = { status: "not_applied", summary: "the user did not approve; nothing was changed", data: { confirm: null }, doc: null };
              return renderOutcome(declined, { structured: services.structured, detailFull: false });
            }
          }
          const progress = progressSender(ctx);
          const { outcome, detailFull } = await runTool(deps, session, spec.name, rawArgs, { signal: ctx?.mcpReq.signal, progress });
          const token = outcome.status === "not_applied" && outcome.data && typeof outcome.data === "object" ? (outcome.data as Record<string, unknown>).confirm : undefined;
          if (!reentry && typeof token === "string" && services.settings.get().useElicitation && session.client.elicitation) {
            const text = renderText(outcome, { structured: false, detailFull: false });
            return inputRequired({
              inputRequests: {
                confirm: inputRequired.elicit({
                  message: `Revit: apply this change?\n${text}`,
                  requestedSchema: { type: "object", properties: { apply: { type: "boolean", title: "Apply", description: "Apply exactly this plan" } }, required: ["apply"] },
                }),
              },
              requestState: `confirm:${token}`,
            });
          }
          return renderOutcome(outcome, { structured: services.structured, detailFull });
        } catch (error) {
          const requestId = ulid();
          services.log.error("handler_crash", { tool: spec.name, requestId, error });
          return renderOutcome(
            {
              status: "error",
              code: "INTERNAL_ERROR",
              summary: `unexpected broker error: ${(error as Error)?.message ?? String(error)}`,
              fix: `ask the user: report requestId ${requestId} to the developer; retry once`,
              details: { requestId },
            },
            { structured: services.structured, detailFull: false }
          );
        }
      };
      server.registerTool(
        spec.name,
        {
          title: spec.title,
          description: spec.description,
          annotations: spec.annotations,
          inputSchema: fromJsonSchema(advertisedSchema(spec) as never, PERMISSIVE_VALIDATOR as never),
        },
        handler as never
      );
    }

    // ---------------------------------------------------------------- prompts (§5.8)
    server.registerPrompt(
      "start_workflow",
      { title: "Start a Revit workflow", description: "The rules for working with these Revit tools, and the workflows to choose from." },
      async () => ({
        messages: [
          {
            role: "user" as const,
            content: {
              type: "text" as const,
              text: `${INSTRUCTIONS}\n\nWorkflows (prompt workflow {name} or help {"topic":"workflow:<name>"}): ${WORKFLOW_NAMES.map((n) => `${n} (${workflowTopic(n)?.title ?? n})`).join(", ")}.\nStart with status {}.`,
            },
          },
        ],
      })
    );
    server.registerPrompt(
      "workflow",
      {
        title: "Revit workflow",
        description: "Step-by-step tool calls for a common Revit task.",
        argsSchema: z.object({
          name: completable(z.enum(WORKFLOW_NAMES).describe("Workflow name"), (value) => WORKFLOW_NAMES.filter((n) => n.startsWith(String(value ?? "").toLowerCase()))),
        }),
      },
      async ({ name }) => {
        const topic = workflowTopic(name);
        return {
          messages: [{ role: "user" as const, content: { type: "text" as const, text: topic ? `${topic.title}\n${topic.text}` : `Unknown workflow ${name}. Use one of: ${WORKFLOW_NAMES.join(", ")}.` } }],
        };
      }
    );

    // ---------------------------------------------------------------- resources (§5.8): content equals help output
    server.registerResource(
      "help-overview",
      "revit://help/overview",
      { title: "Revit tools help", description: "Rules, the tool index and the help topics (same as help {}).", mimeType: "text/plain" },
      async (uri) => ({ contents: [{ uri: uri.href, mimeType: "text/plain", text: renderText(helpIndex(listed), { structured: false, detailFull: true }) }] })
    );
    server.registerResource(
      "help",
      new ResourceTemplate("revit://help/{name}", {
        list: async () => ({
          resources: [
            ...listed.map((t) => ({ name: t.name, title: t.title, uri: `revit://help/${t.name}`, description: t.description, mimeType: "text/plain" })),
            ...TOPICS.map((t) => ({ name: t.name, title: t.title, uri: `revit://help/${encodeURIComponent(t.name)}`, mimeType: "text/plain" })),
          ],
        }),
        complete: {
          name: async (value) => {
            const v = String(value ?? "").toLowerCase();
            return names.filter((n) => n.toLowerCase().startsWith(v)).slice(0, 50);
          },
        },
      }),
      { title: "Revit help for a tool, tool.op, topic or error code", description: "Same content as help {tool}, help {tool,op} and help {topic}.", mimeType: "text/plain" },
      async (uri, variables) => {
        const raw = Array.isArray(variables.name) ? (variables.name[0] ?? "") : (variables.name ?? "");
        const name = decodeURIComponent(String(raw));
        const outcome: ToolOutcome = helpFor(name, listed) ?? { status: "error", code: "NOT_FOUND", summary: `no help for "${name}"`, fix: "help {}", doc: null };
        return { contents: [{ uri: uri.href, mimeType: "text/plain", text: renderText(outcome, { structured: false, detailFull: true }) }] };
      }
    );

    return server;
  };

  /** notifications/progress when the client sent a progressToken (SDK 2.2: ctx.mcpReq.notify). */
  function progressSender(ctx: McpCtx | undefined) {
    const token = ctx?.mcpReq._meta?.progressToken;
    if (token === undefined || token === null || !ctx) return undefined;
    let last = 0;
    let lastSent = 0;
    return (message: string, done?: number, total?: number) => {
      const now = Date.now();
      if (now - lastSent < 400) return;
      const progress = Math.max(last + 1, typeof done === "number" && Number.isFinite(done) ? done : last + 1);
      last = progress;
      lastSent = now;
      void ctx.mcpReq
        .notify({ method: "notifications/progress", params: { progressToken: token, progress, ...(typeof total === "number" && total >= progress ? { total } : {}), message } })
        .catch(() => undefined);
    };
  }
}
