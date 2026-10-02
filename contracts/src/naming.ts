// Transaction / group naming table and parser (SPEC §4.4). Mirrors addin/RevitMcpNext.Addin/Core/Naming.cs.
// Nothing else may write free-form transaction names.
//
// | Class                | Name                                                         | Example                       |
// | write                | MCP <wtag> <tool>.<op>  or  MCP <wtag> change_set <name?> (<n> ops) | MCP w17 create_elements.wall |
// | write (family doc)   | MCP <wtag> edit_family.<op> (inside the family document)     | MCP w18 edit_family.add_param |
// | write (compensation) | MCP <wtag> undo.compensate                                   |                               |
// | temp                 | MCP temp <purpose>                                           | MCP temp capture              |
// | ui                   | MCP ui <op>                                                  | MCP ui isolate                |

export type TxClass = "write" | "temp" | "ui" | "foreign";

export const TX_PREFIX = "MCP ";
export const TX_NAME_PATTERN = /^MCP (?:(w\d+) (.+)|temp (.+)|ui (\S+))$/;
export const WRITE_TAG_PATTERN = /^w\d+$/;

export const TEMP_PURPOSES = ["preview", "capture", "probe", "run_csharp", "export.ifc", "schedule_fields"] as const;
export type TempPurpose = (typeof TEMP_PURPOSES)[number];

export function writeTxName(writeTag: string, tool: string, op?: string | null): string {
  assertWriteTag(writeTag);
  return `MCP ${writeTag} ${op ? `${tool}.${op}` : tool}`;
}

export function changeSetTxName(writeTag: string, opCount: number, name?: string | null): string {
  assertWriteTag(writeTag);
  const label = name && name.trim() && name.trim() !== "change_set" ? `${name.trim()} ` : "";
  return `MCP ${writeTag} change_set ${label}(${opCount} ops)`;
}

export function compensateTxName(writeTag: string): string {
  assertWriteTag(writeTag);
  return `MCP ${writeTag} undo.compensate`;
}

export function tempTxName(purpose: TempPurpose): string {
  return `MCP temp ${purpose}`;
}

export function uiTxName(op: string): string {
  if (!/^\S+$/.test(op)) throw new Error(`ui transaction op must be one word: ${op}`);
  return `MCP ui ${op}`;
}

export interface ParsedTxName {
  cls: TxClass;
  /** write: w17 */
  writeTag?: string;
  /** write: "create_elements.wall", "change_set Facade (3 ops)", "undo.compensate" */
  label?: string;
  /** temp: purpose */
  purpose?: string;
  /** ui: op */
  op?: string;
}

/** Classify a transaction or group name. Anything that does not match the table is "foreign". */
export function parseTxName(name: string | null | undefined): ParsedTxName {
  if (!name) return { cls: "foreign" };
  const match = TX_NAME_PATTERN.exec(name);
  if (!match) return { cls: "foreign" };
  if (match[1] !== undefined) return { cls: "write", writeTag: match[1], label: match[2] };
  if (match[3] !== undefined) return { cls: "temp", purpose: match[3] };
  return { cls: "ui", op: match[4] };
}

/** get_changes marks write|ui entries as ours. */
export function isOursTx(name: string | null | undefined): boolean {
  const cls = parseTxName(name).cls;
  return cls === "write" || cls === "ui";
}

/** Generation and element stamps ignore temp transactions. */
export function bumpsGeneration(name: string | null | undefined): boolean {
  return parseTxName(name).cls !== "temp";
}

function assertWriteTag(writeTag: string): void {
  if (!WRITE_TAG_PATTERN.test(writeTag)) throw new Error(`invalid write tag: ${writeTag}`);
}
