// S92 capture (SPEC §10.1, §13.3; owner P-VIEWS-VISUAL). Asserts the size caps (long edge and megapixels per size),
// the crop aspect within ±1 % (not exact pixels), base64 ≤ 1.5 MB, bounds/mm_per_px on cropped plans, region crops,
// the result cache, compare (changed pixels and model bbox), compare refusals, focus captures of elements with
// highlight, CAPTURE_NOT_EXPORTABLE for templates, sheet captures with region, and that a temporary region capture leaves
// the view unchanged (V1).

import { levelsOf } from "./S50-views.mjs";

const SIZES = { small: [768, 0.4], medium: [1280, 1.0], large: [1568, 1.15] };
const MAX_BASE64 = 1.5 * 1024 * 1024;

/** Width/height from a PNG or JPEG header. */
export function imageSize(buf) {
  if (buf.length > 24 && buf[0] === 0x89 && buf.toString("ascii", 1, 4) === "PNG") return { w: buf.readUInt32BE(16), h: buf.readUInt32BE(20) };
  if (buf.length > 4 && buf[0] === 0xff && buf[1] === 0xd8) {
    let i = 2;
    while (i + 9 < buf.length) {
      if (buf[i] !== 0xff) {
        i++;
        continue;
      }
      const marker = buf[i + 1];
      if (marker >= 0xc0 && marker <= 0xcf && marker !== 0xc4 && marker !== 0xc8 && marker !== 0xcc) return { h: buf.readUInt16BE(i + 5), w: buf.readUInt16BE(i + 7) };
      i += 2 + buf.readUInt16BE(i + 2);
    }
  }
  return null;
}

function checkImage(t, r, size, label) {
  t.assertEq(r.images.length, 1, `${label}: one image block`);
  const img = r.images[0];
  t.assertLe(img.base64Length, MAX_BASE64, `${label}: base64 length`);
  t.assertEq(img.meta?.["codex/imageDetail"], "high", `${label}: image _meta`);
  const dims = imageSize(img.bytes);
  t.assert(dims, `${label}: readable ${img.mimeType} header`);
  const [cap, mp] = SIZES[size];
  t.assertLe(Math.max(dims.w, dims.h), cap, `${label}: long edge`);
  t.assertLe(dims.w * dims.h, mp * 1_000_000 * 1.01, `${label}: megapixels`);
  t.assertEq(r.json?.px?.[0], dims.w, `${label}: px width matches the image`);
  t.assertEq(r.json?.px?.[1], dims.h, `${label}: px height matches the image`);
  t.assertMatch(r.json?.capture ?? "", /^c\d+$/, `${label}: capture id`);
  t.assertMatch(r.summary, new RegExp(`as ${r.json.capture}`), `${label}: summary names the capture id`);
  return dims;
}

function checkAspect(t, dims, expected, label) {
  const aspect = dims.w / dims.h;
  t.assert(Math.abs(aspect - expected) / expected <= 0.01, `${label}: aspect ${aspect.toFixed(4)} within 1 % of ${expected.toFixed(4)} (${dims.w}x${dims.h})`);
}

export default {
  id: "S92",
  title: "Capture: size caps, aspect, bounds, region, cache, compare, focus/highlight, not exportable, sheets, no residue",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S92");
    t.setTargetPath(path);
    const levels = await levelsOf(t);
    const L1 = levels[0];

    await t.step("capture {} returns one image of the active view within the medium cap", async () => {
      const r = await t.call("capture", {});
      checkImage(t, r, "medium", "active view");
    });

    await t.step("a cropped 2:1 plan: aspect within 1 %, bounds and mm_per_px map pixels to mm", async () => {
      await t.call("edit_views", { op: "create", kind: "floor_plan", level: L1, name: "E2E Cap Plan", template: "none" });
      await t.call("edit_views", { op: "set", view: "E2E Cap Plan", crop_box: [0, 0, 24000, 12000] });
      for (const size of ["small", "medium", "large"]) {
        const r = await t.call("capture", { view: "E2E Cap Plan", size });
        const dims = checkImage(t, r, size, `plan ${size}`);
        checkAspect(t, dims, 2, `plan ${size}`);
        const b = r.json.bounds;
        t.assert(Array.isArray(b) && Math.abs(b[0]) <= 5 && Math.abs(b[2] - 24000) <= 5, `bounds ${JSON.stringify(b)}`);
        t.assert(Math.abs(r.json.mm_per_px - 24000 / dims.w) <= 0.05 * (24000 / dims.w), `mm_per_px ${r.json.mm_per_px}`);
      }
    });

    await t.step("the same capture again is served from the cache", async () => {
      const r = await t.call("capture", { view: "E2E Cap Plan", size: "small" });
      t.assertEq(r.json.cached, true, "cached");
      checkImage(t, r, "small", "cached");
    });

    await t.step("region crops a plan to the region's aspect; nothing remains in the model (V1)", async () => {
      const before = await t.call("get_view", { view: "E2E Cap Plan" });
      const r = await t.call("capture", { view: "E2E Cap Plan", region: [0, 0, 6000, 4000], annotations: false });
      const dims = checkImage(t, r, "medium", "region");
      checkAspect(t, dims, 1.5, "region");
      const b = r.json.bounds;
      t.assert(Array.isArray(b) && Math.abs(b[2] - 6000) <= 5 && Math.abs(b[3] - 4000) <= 5, `region bounds ${JSON.stringify(b)}`);
      const after = await t.call("get_view", { view: "E2E Cap Plan" });
      t.assertEq(JSON.stringify(after.json.crop), JSON.stringify(before.json.crop), "the view's crop is unchanged after the temporary capture");
    });

    const walls = await t.step("model content: two walls", async () => {
      const a = await t.call("create_elements", { op: "wall", level: L1, start: [2000, 2000], end: [10000, 2000], height: 3000 });
      const b = await t.call("create_elements", { op: "wall", level: L1, start: [2000, 8000], end: [10000, 8000], height: 3000 });
      return [String(a.json.created[0]), String(b.json.created[0])];
    });

    const base = await t.step("baseline capture for compare", async () => {
      const r = await t.call("capture", { view: "E2E Cap Plan" });
      checkImage(t, r, "medium", "baseline");
      return r.json.capture;
    });

    await t.step("compare marks the changed pixels and maps them to model mm", async () => {
      await t.call("create_elements", { op: "wall", level: L1, start: [14000, 3000], end: [20000, 3000], height: 3000 });
      const r = await t.call("capture", { view: "E2E Cap Plan", compare: base });
      const dims = checkImage(t, r, "medium", "compare");
      checkAspect(t, dims, 2, "compare");
      t.assertEq(r.images[0].mimeType, "image/png", "compare is PNG");
      const changed = r.json.changed;
      t.assert(changed && changed.pixels > 0, `changed pixels: ${JSON.stringify(changed)}`);
      const m = changed.bbox_model;
      t.assert(Array.isArray(m) && m[0] >= 12000 && m[2] <= 22000, `changed area near the new wall (x 14000-20000): ${JSON.stringify(m)}`);
    });

    await t.step("compare refuses another view, size or region and unknown ids", async () => {
      await t.expectError("capture", { view: "E2E Cap Plan", size: "large", compare: base }, "INVALID_ARGS");
      await t.expectError("capture", { compare: base }, "INVALID_ARGS");
      await t.expectError("capture", { view: "E2E Cap Plan", compare: "c999" }, "CAPTURE_GONE");
    });

    await t.step("focus on elements: temporary 3D box with highlight", async () => {
      const r = await t.call("capture", { ids: walls, highlight: [walls[0]], margin: 500, size: "small" });
      checkImage(t, r, "small", "focus");
      t.assertMatch(r.summary, /3D focus on 2 elements/, "focus summary");
      const again = await t.call("capture", { from: "last", orient: "top", style: "hidden" });
      checkImage(t, again, "medium", "focus last");
    });

    await t.step("templates are not exportable", async () => {
      await t.call("edit_views", { op: "create_template", view: "E2E Cap Plan", name: "E2E Cap Template" });
      const e = await t.expectError("capture", { view: "E2E Cap Template" }, "CAPTURE_NOT_EXPORTABLE");
      t.assert(!!e.fix, "fix line");
    });

    await t.step("format jpg and style on a 3D view", async () => {
      await t.call("edit_views", { op: "create", kind: "3d", name: "E2E Cap 3D", orient: "iso_sw" });
      const r = await t.call("capture", { view: "E2E Cap 3D", style: "realistic", format: "jpg", size: "large" });
      checkImage(t, r, "large", "3d jpg");
      t.assertEq(r.images[0].mimeType, "image/jpeg", "JPEG");
    });

    await t.step("sheet capture maps the sheet outline; a sheet region crops in sheet mm", async () => {
      const s = await t.call("edit_sheets", { op: "create", number: "E92", name: "Capture" }, { allowError: true });
      if (s.status !== "ok") {
        t.log(`sheet part skipped: edit_sheets create gave ${s.code}`);
        return;
      }
      await t.call("edit_views", { op: "duplicate", view: "E2E Cap Plan", mode: "copy", name: "E2E Cap Sheet View", sheet: "E92" });
      const r = await t.call("capture", { view: "E92" });
      const dims = checkImage(t, r, "medium", "sheet");
      const b = r.json.bounds;
      if (Array.isArray(b)) checkAspect(t, dims, (b[2] - b[0]) / (b[3] - b[1]), "sheet outline");
      else t.log("sheet has no outline mapping (V2 fallback: omitted)");
      if (Array.isArray(b)) {
        const region = [b[0], b[1], b[0] + (b[2] - b[0]) / 2, b[1] + (b[3] - b[1]) / 3];
        const rr = await t.call("capture", { view: "E92", region });
        const rd = checkImage(t, rr, "medium", "sheet region");
        checkAspect(t, rd, (region[2] - region[0]) / (region[3] - region[1]), "sheet region");
      }
    });
  },
};
