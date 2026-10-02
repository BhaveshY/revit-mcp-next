// Capture image blocks (SPEC §5.5, §10.1): the add-in writes the file under <home>\captures and returns its path;
// the broker only reads it (the path must be inside <home>\captures) and base64-encodes it. Images never cross the
// pipe and never carry structuredContent. Frozen in wave 2.

import { readFileSync, realpathSync, statSync } from "node:fs";
import { extname, isAbsolute, relative } from "node:path";
import { capturesDir } from "@revit-mcp-next/contracts/home";
import type { ImageBlock } from "./types.js";

/** Encoded file cap (base64 ≤ ~1.5 MB). */
export const MAX_IMAGE_FILE_BYTES = 1_150_000;

export type ImageReadResult =
  | { ok: true; block: ImageBlock; bytes: number; mime: string; path: string }
  | { ok: false; code: "CAPTURE_GONE" | "INVALID_ARGS" | "RESPONSE_TOO_LARGE"; message: string };

const MIME_BY_EXT: Record<string, string> = { ".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg", ".webp": "image/webp", ".gif": "image/gif" };

/** True when `path` is inside <home>\captures (after resolving links). */
export function isInsideCaptures(home: string, path: string): boolean {
  try {
    const real = realpathSync(path).toLowerCase();
    const root = realpathSync(capturesDir(home)).toLowerCase();
    const rel = relative(root, real);
    return rel !== "" && !rel.startsWith("..") && !isAbsolute(rel);
  } catch {
    return false;
  }
}

/** Read a capture into an image block (synchronous: files are ≤ ~1.1 MB). */
export function readCaptureImage(home: string, path: string, mimeHint?: string, maxBytes = MAX_IMAGE_FILE_BYTES): ImageReadResult {
  let real: string;
  try {
    real = realpathSync(path);
  } catch {
    return { ok: false, code: "CAPTURE_GONE", message: `the capture file is gone (${path})` };
  }
  if (!isInsideCaptures(home, real)) return { ok: false, code: "INVALID_ARGS", message: `image path is outside ${capturesDir(home)}` };
  let size: number;
  try {
    const info = statSync(real);
    if (!info.isFile()) return { ok: false, code: "CAPTURE_GONE", message: `the capture file is gone (${path})` };
    size = info.size;
  } catch {
    return { ok: false, code: "CAPTURE_GONE", message: `the capture file is gone (${path})` };
  }
  if (size > maxBytes) return { ok: false, code: "RESPONSE_TOO_LARGE", message: `the image is ${size} bytes, above ${maxBytes}` };
  const data = readFileSync(real);
  const mime = sniffMime(data) ?? mimeHint ?? MIME_BY_EXT[extname(real).toLowerCase()] ?? "image/png";
  return {
    ok: true,
    path: real,
    bytes: data.byteLength,
    mime,
    block: { type: "image", mimeType: mime, data: data.toString("base64"), _meta: { "codex/imageDetail": "high" } },
  };
}

function sniffMime(data: Buffer): string | null {
  if (data.length >= 8 && data[0] === 0x89 && data[1] === 0x50 && data[2] === 0x4e && data[3] === 0x47) return "image/png";
  if (data.length >= 3 && data[0] === 0xff && data[1] === 0xd8 && data[2] === 0xff) return "image/jpeg";
  return null;
}
