// R7 dialogs (SPEC §8.5, §8.8 rule 3, §17 V17/V20; D4 R7). Owner P-REL-ADDIN.
// test.show_dialog (raw pipe) opens a TaskDialog with an unknown id at the next Idling, outside any MCP request, so the
// auto-responder leaves it for the user. Then: status shows the dialog within 3 s; health.lastDialog has the id (V20);
// a read reports REVIT_DIALOG_OPEN; ui dialogs lists its buttons (V17, UI Automation); ui press OK needs the user's OK
// (NOT APPLIED + confirm) and the confirm presses it; Cancel applies at once; No and Yes on a save-type dialog are refused
// with DIALOG_PROTECTED. Run it on EN and DE Revit (start Revit with /language DEU) for V17.

import { health, rawAddin, requireTestOps, sleep } from "./R5-busy.mjs";

async function waitFor(fn, timeoutMs, stepMs = 200) {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await fn();
    if (value) return value;
    if (Date.now() > deadline) return null;
    await sleep(stepMs);
  }
}

/** The last test dialog record (test.state runs on the UI thread: call it only while no dialog is open). */
async function testDialogState(addin) {
  const r = await addin.call("test.state", {}, { timeoutMs: 5_000 });
  return r.ok ? r.data?.lastTestDialog : null;
}

/** Shows a test dialog and waits until the UI monitor sees the main window disabled. */
async function show(t, addin, spec) {
  const r = await addin.call("test.show_dialog", spec, { timeoutMs: 10_000 });
  t.assert(r.ok, `test.show_dialog: ${r.code ?? ""} ${r.message ?? ""}`);
  const shown = await waitFor(async () => {
    const h = await health(addin);
    return h && h.ui?.mainWindowEnabled === false ? h : null;
  }, 5_000);
  t.assert(shown, "the dialog disabled the Revit main window within 5 s");
  // The health sample can still show the previous dialog: wait until ui dialogs lists this one by its title.
  const listed = await waitFor(async () => findDialog(await t.call("ui", { op: "dialogs" }, { allowError: true }), spec.title), 5_000, 250);
  t.assert(listed, `ui dialogs lists '${spec.title}' within 5 s`);
  return shown;
}

function findDialog(r, title) {
  const rows = r.json?.dialogs ?? [];
  return rows.find((d) => d.title === title) ?? null;
}

export default {
  id: "R7",
  title: "Dialogs: status/dialogs show an unknown dialog; press Cancel at once, OK with confirm; save-type No refused",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const addin = rawAddin(t);
    await requireTestOps(t, addin);
    try {
      await this.steps(t, addin);
    } finally {
      // Never leave a test dialog open for the next scenario: Cancel is pressed at once by the product's ui press.
      for (let i = 0; i < 3; i++) {
        const h = await health(addin).catch(() => null);
        if (!h || h.ui?.mainWindowEnabled !== false) break;
        await t.call("ui", { op: "press", button: "Cancel" }, { allowError: true });
        await sleep(500);
      }
    }
  },

  async steps(t, addin) {
    const { path } = await t.newProject("R7");
    t.setTargetPath(path);
    const pid = t.revit.pid;

    // 1. Unknown dialog with OK/Cancel.
    const shownAt = Date.now();
    await show(t, addin, { id: "E2E_Unknown_Dialog", title: "E2E dialog", text: "Unknown dialog for R7.", buttons: ["ok", "cancel"] });

    await t.step("status shows the dialog within 3 s", async () => {
      const row = await waitFor(async () => {
        const r = await t.call("status", { instance: String(pid) }, { allowError: true });
        const found = Array.isArray(r.json?.revit) ? r.json.revit.find((x) => x[1] === pid) : null;
        return found && String(found[2]).startsWith("dialog") ? found : null;
      }, 3_000 - Math.min(2_500, Date.now() - shownAt), 250);
      t.assert(row, "status row state starts with 'dialog'");
    });

    await t.step("V20: health.lastDialog carries the TaskDialog id (or the title fallback is needed)", async () => {
      const h = await health(addin);
      t.assert(h?.lastDialog, "health.lastDialog is set");
      t.assertEq(h.lastDialog.answer ?? null, null, "the unknown dialog was not auto-answered");
      t.log(`V20 DialogBoxShowing id: ${h.lastDialog.dialogId} (${h.lastDialog.dialogId === "E2E_Unknown_Dialog" ? "pass" : "fallback: match by title"})`);
    });

    await t.step("a read while the dialog is open reports REVIT_DIALOG_OPEN (nothing ran)", async () => {
      const t0 = Date.now();
      const r = await t.call("find_elements", { category: ["Walls"], count_only: true }, { expectError: ["REVIT_DIALOG_OPEN"] });
      t.assertLe(Date.now() - t0, 10_000, "REVIT_DIALOG_OPEN latency ms");
      t.assertMatch(r.fix ?? "", /ui/, "fix names ui dialogs");
    });

    const listed = await t.step("V17: ui dialogs lists the dialog with its OK and Cancel buttons", async () => {
      const r = await t.call("ui", { op: "dialogs" });
      const d = findDialog(r, "E2E dialog");
      t.assert(d, `the dialog is listed: ${r.jsonText?.slice(0, 300)}`);
      const names = d.buttons.map((b) => String(b.name).toLowerCase());
      t.assert(names.includes("ok") && (names.includes("cancel") || names.includes("abbrechen")), `buttons ${JSON.stringify(d.buttons)}`);
      t.log(`dialog ${d.dialog} dialogId=${d.dialogId ?? "-"} buttons=${JSON.stringify(d.buttons)}`);
      return d;
    });

    await t.step("press OK returns NOT APPLIED with a confirm token (rule button); the dialog stays open", async () => {
      const r = await t.call("ui", { op: "press", button: "OK", dialog: listed.dialog });
      t.assertEq(r.status, "not_applied", "press OK status");
      t.assert(typeof r.json?.confirm === "string", "confirm token");
      t.assertMatch(r.next ?? "", /ask the user/, "next asks the user");
      const h = await health(addin);
      t.assertEq(h?.ui?.mainWindowEnabled, false, "the dialog is still open (main window disabled)");
    });

    await t.step("press Cancel applies at once and closes the dialog", async () => {
      const r = await t.call("ui", { op: "press", button: "Cancel" });
      t.assertEq(r.status, "ok", "press Cancel");
      t.log(`press Cancel: ${r.summary}`);
      const closed = await waitFor(async () => {
        const s = await testDialogState(addin);
        return s?.state === "closed" ? s : null;
      }, 5_000);
      t.assert(closed, "the dialog closed");
      t.assertEq(closed.result, "Cancel", "TaskDialog result");
    });

    // 2. OK with the user's confirmation.
    await show(t, addin, { id: "E2E_Confirm_Dialog", title: "E2E confirm", text: "Press OK after the user agreed.", buttons: ["ok", "cancel"] });
    await t.step("press OK with confirm presses it", async () => {
      const plan = await t.call("ui", { op: "press", button: "OK" });
      t.assertEq(plan.status, "not_applied", "first press is NOT APPLIED");
      const r = await t.call("ui", { op: "press", confirm: plan.json.confirm });
      t.assertEq(r.status, "ok", "confirmed press");
      const closed = await waitFor(async () => {
        const s = await testDialogState(addin);
        return s?.state === "closed" && s.id === "E2E_Confirm_Dialog" ? s : null;
      }, 5_000);
      t.assert(closed, "the dialog closed");
      t.assertEq(closed.result, "Ok", "TaskDialog result");
    });

    // 3. A save-type dialog: No and Yes are never pressed.
    await show(t, addin, { id: "E2E_Save_Changes", title: "Save changes to E2E?", text: "Do you want to save changes to E2E?", buttons: ["yes", "no", "cancel"] });
    await t.step("No and Yes on a save-type dialog are refused with DIALOG_PROTECTED; Cancel closes it", async () => {
      await t.call("ui", { op: "press", button: "No" }, { expectError: ["DIALOG_PROTECTED"] });
      await t.call("ui", { op: "press", button: "Yes", }, { expectError: ["DIALOG_PROTECTED"] });
      const h = await health(addin);
      t.assertEq(h?.ui?.mainWindowEnabled, false, "the save dialog is still open (main window disabled)");
      const r = await t.call("ui", { op: "press", button: "Cancel" });
      t.assertEq(r.status, "ok", "Cancel on the save dialog");
      const closed = await waitFor(async () => {
        const s = await testDialogState(addin);
        return s?.state === "closed" && s.id === "E2E_Save_Changes" ? s : null;
      }, 5_000);
      t.assertEq(closed?.result, "Cancel", "save dialog result");
    });

    await t.step("press with no dialog open gives DIALOG_NOT_FOUND", async () => {
      await t.call("ui", { op: "press", button: "Cancel" }, { expectError: ["DIALOG_NOT_FOUND"] });
    });

    await t.step("Revit is idle again (reads work)", async () => {
      const r = await t.call("find_elements", { category: ["Walls"], count_only: true });
      t.assertEq(r.status, "ok", "read after the dialogs");
    });

    t.log("unresolved-references variant skipped: needs a linked model (P-DOCS links) - add when links.add is bound");
  },
};
