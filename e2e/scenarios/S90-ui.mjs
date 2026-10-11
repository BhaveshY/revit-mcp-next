// S90 ui (SPEC §13.3, §5.9, §8.5; owner P-VIEWS-VISUAL). activate_view/open_views/close_views (LAST_VIEW guard),
// zoom_to (fit, region), select (replace/add/remove), temporary isolate/hide/reset (MCP ui transactions, no model change),
// NEEDS_ACTIVE_DOC for a background target, activate_doc re-pins with TARGET_NOW, and the dialog press policy:
// Cancel applies at once, OK needs the user's OK (confirm), No on a save-type dialog is refused (DIALOG_PROTECTED).

import { health, rawAddin, requireTestOps, sleep } from "./R5-busy.mjs";
import { levelsOf } from "./S50-views.mjs";

async function waitFor(fn, timeoutMs, stepMs = 200) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await fn();
    if (value) return value;
    if (Date.now() > deadline) return null;
    await sleep(stepMs);
  }
}

export default {
  id: "S90",
  title: "UI: activate/open/close views, zoom, select, temporary isolate/hide/reset, activate_doc, dialog press policy",
  profiles: ["quick", "full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("S90");
    t.setTargetPath(path);
    const levels = await levelsOf(t);
    const L1 = levels[0];

    await t.step("activate_view opens a new plan; get_view sees it active", async () => {
      await t.call("edit_views", { op: "create", kind: "floor_plan", level: L1, name: "E2E UI Plan" });
      const r = await t.call("ui", { op: "activate_view", view: "E2E UI Plan" });
      t.assertEq(r.status, "ok", "activate_view");
      const g = await t.call("get_view", {});
      t.assertEq(g.json.name, "E2E UI Plan", "active view");
      t.assertEq(g.json.active, true, "flagged active");
    });

    await t.step("open_views lists the open views with the active one marked", async () => {
      const r = await t.call("ui", { op: "open_views" });
      const rows = r.json?.rows ?? [];
      const cols = r.json?.cols ?? [];
      const nameAt = cols.indexOf("name");
      const activeAt = cols.indexOf("active");
      t.assert(rows.some((row) => row[nameAt] === "E2E UI Plan" && row[activeAt] === true), `open views: ${r.jsonText?.slice(0, 300)}`);
    });

    await t.step("zoom_to fit and region", async () => {
      const fit = await t.call("ui", { op: "zoom_to", fit: true });
      t.assertMatch(fit.summary, /fit/, "fit summary");
      const region = await t.call("ui", { op: "zoom_to", region: [0, 0, 10000, 8000] });
      t.assertEq(region.status, "ok", "region zoom");
    });

    const walls = await t.step("two walls to select", async () => {
      const a = await t.call("create_elements", { op: "wall", level: L1, start: [0, 0], end: [8000, 0], height: 3000 });
      const b = await t.call("create_elements", { op: "wall", level: L1, start: [0, 5000], end: [8000, 5000], height: 3000 });
      return [String(a.json.created[0]), String(b.json.created[0])];
    });

    await t.step("select replace/add/remove and zoom", async () => {
      const one = await t.call("ui", { op: "select", ids: [walls[0]], zoom: true });
      t.assertEq(one.json.selected, 1, "replace selects one");
      const two = await t.call("ui", { op: "select", ids: [walls[1]], mode: "add" });
      t.assertEq(two.json.selected, 2, "add makes two");
      const back = await t.call("ui", { op: "select", ids: [walls[0]], mode: "remove" });
      t.assertEq(back.json.selected, 1, "remove leaves one");
      const byFilter = await t.call("ui", { op: "select", filter: { category: "Walls" } });
      t.assert(byFilter.json.selected >= 2, "filter selects both walls");
    });

    await t.step("temporary isolate/hide/reset run as MCP ui transactions and change no element", async () => {
      const iso = await t.call("ui", { op: "isolate", category: ["Walls"] });
      t.assertMatch(iso.summary, /temporary view mode/, "isolate summary names the temporary mode");
      let g = await t.call("get_view", {});
      t.assertEq(g.json.temporary_hide_isolate, true, "temporary hide/isolate active");
      const hide = await t.call("ui", { op: "hide", ids: [walls[1]] });
      t.assertEq(hide.status, "ok", "temporary hide");
      const reset = await t.call("ui", { op: "reset" });
      t.assertEq(reset.json.was_active, true, "reset ended the temporary mode");
      g = await t.call("get_view", {});
      t.assert(g.json.temporary_hide_isolate === undefined, "temporary mode off");
    });

    await t.step("close_views all_but_active keeps one view; closing the last gives LAST_VIEW", async () => {
      await t.call("ui", { op: "activate_view", view: L1 }, { allowError: true });
      await t.call("ui", { op: "activate_view", view: "E2E UI Plan" });
      const r = await t.call("ui", { op: "close_views", views: ["all_but_active"] });
      t.assertEq(r.json.open, 1, "one view stays open");
      await t.expectError("ui", { op: "close_views", views: ["E2E UI Plan"] }, "LAST_VIEW");
    });

    await t.step("a background target gives NEEDS_ACTIVE_DOC; activate_doc brings it back and re-pins", async () => {
      const second = await t.newProject("S90b");
      const listing = await t.call("status", {});
      const rowA = listing.json.docs.rows.find((row) => row[1] === "S90" && row[3] === t.year);
      t.assert(rowA, "doc S90 is listed");
      await t.call("set_target", { doc: `#${rowA[0]}` });
      t.setTargetPath(path);
      const e = await t.expectError("ui", { op: "select", ids: [walls[0]] }, "NEEDS_ACTIVE_DOC");
      t.assertMatch(e.fix ?? "", /activate_doc/, "fix activates the doc");
      const r = await t.call("ui", { op: "activate_doc", doc: `#${rowA[0]}` });
      t.assert(r.notices.some((n) => n.startsWith("TARGET_NOW")), `TARGET_NOW notice: ${r.notices.join(" | ")}`);
      const sel = await t.call("ui", { op: "select", ids: [walls[0]] });
      t.assertEq(sel.json.selected, 1, "select works once active");
      t.log(`second doc at ${second.path}`);
    });

    await t.step("activate_view of a background doc switches the doc first", async () => {
      const listing = await t.call("status", {});
      const rowB = listing.json.docs.rows.find((row) => row[1] === "S90b" && row[3] === t.year);
      t.assert(rowB, "doc S90b is listed");
      await t.call("edit_views", { op: "create", kind: "floor_plan", level: L1, name: "E2E B Plan", doc: `#${rowB[0]}` });
      const r = await t.call("ui", { op: "activate_view", view: "E2E B Plan", doc: `#${rowB[0]}` });
      t.assertEq(r.json.doc_activated, true, "the doc was activated first");
      t.assert(r.notices.some((n) => n.startsWith("TARGET_NOW")), `re-pinned: ${r.notices.join(" | ")}`);
      await t.call("ui", { op: "activate_doc", doc: `#${listing.json.docs.rows.find((row) => row[1] === "S90")[0]}` });
    });

    await t.step("dialog press policy: Cancel at once, OK needs confirm, No on a save dialog refused", async () => {
      const addin = rawAddin(t);
      await requireTestOps(t, addin);
      const show = async (spec) => {
        const r = await addin.call("test.show_dialog", spec, { timeoutMs: 10_000 });
        t.assert(r.ok, `test.show_dialog: ${r.code ?? ""} ${r.message ?? ""}`);
        const shown = await waitFor(async () => {
          const h = await health(addin);
          return h && h.ui?.mainWindowEnabled === false ? h : null;
        }, 5_000);
        t.assert(shown, "the dialog is open");
      };
      await show({ id: "E2E_S90_Ok", title: "E2E S90", text: "Press policy check.", buttons: ["ok", "cancel"] });
      const ok = await t.call("ui", { op: "press", button: "OK" });
      t.assertEq(ok.status, "not_applied", "OK needs the user's OK");
      t.assertMatch(ok.next ?? "", /ask the user/, "next asks the user");
      const cancel = await t.call("ui", { op: "press", button: "Cancel" });
      t.assertEq(cancel.status, "ok", "Cancel applies at once");
      await waitFor(async () => ((await health(addin))?.ui?.mainWindowEnabled === true ? true : null), 5_000);

      await show({ id: "E2E_S90_Save", title: "Save changes?", text: "Do you want to save changes to S90?", buttons: ["yes", "no", "cancel"] });
      await t.expectError("ui", { op: "press", button: "No" }, "DIALOG_PROTECTED");
      const close = await t.call("ui", { op: "press", button: "Cancel" });
      t.assertEq(close.status, "ok", "Cancel closes the save dialog");
      await waitFor(async () => ((await health(addin))?.ui?.mainWindowEnabled === true ? true : null), 5_000);
    });
  },
};
