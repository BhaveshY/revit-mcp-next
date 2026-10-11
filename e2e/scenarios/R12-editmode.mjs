// R12 edit mode (SPEC §8.8 rule 5; D2 E9/E10). Owner P-REL-ADDIN.
// test.enter_command posts the Wall tool so Revit sits in a command (no Idling, ExternalEvents wait). A queued read then
// reports REVIT_EDIT_MODE_OR_COMMAND within 7 s and nothing executed (a raw dev.ping times out in the queue with
// REVIT_BUSY reason not_picked_up). The test op posts Escape twice after escapeAfterMs, after which calls work again.
// Variants with an export / family load executing must report REVIT_BUSY (shared with R5).

import { busyVariants, health, rawAddin, requireTestOps, sleep } from "./R5-busy.mjs";

export default {
  id: "R12",
  title: "Edit mode: a read while Revit sits in the Wall command gives REVIT_EDIT_MODE_OR_COMMAND and runs nothing",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const addin = rawAddin(t);
    await requireTestOps(t, addin);
    const { path } = await t.newProject("R12");
    t.setTargetPath(path);

    const escapeAfterMs = 25_000;
    const entered = await addin.call("test.enter_command", { command: "Wall", escapeAfterMs }, { timeoutMs: 10_000 });
    t.assert(entered.ok, `test.enter_command: ${entered.code ?? ""} ${entered.message ?? ""}`);
    const enteredAt = Date.now();
    await sleep(1_500);

    await t.step("a queued raw dev.ping is not picked up (REVIT_BUSY not_picked_up; nothing ran)", async () => {
      const r = await addin.call("dev.ping", {}, { timeoutMs: 3_000 });
      t.assert(!r.ok, "dev.ping did not run while Revit sits in the command");
      t.assertEq(r.code, "REVIT_BUSY", "code");
      t.assertEq(r.details?.reason, "not_picked_up", "details.reason");
    });

    await t.step("add-in health: nothing executing, window enabled, Idling silent", async () => {
      const h = await health(addin);
      t.assertEq(h?.queue?.executing ?? null, null, "nothing executing");
      t.assertEq(h.ui?.mainWindowEnabled, true, "main window enabled (no dialog)");
      const silentMs = Date.now() - Date.parse(h.lastIdlingAtUtc ?? 0);
      t.log(`Idling silent for ${silentMs} ms`);
    });

    await t.step("a read reports REVIT_EDIT_MODE_OR_COMMAND within 7 s", async () => {
      const t0 = Date.now();
      const r = await t.call("find_elements", { category: ["Walls"], count_only: true }, { expectError: ["REVIT_EDIT_MODE_OR_COMMAND"] });
      const ms = Date.now() - t0;
      t.assertLe(ms, 7_500, "edit-mode classification ms");
      t.assertMatch(r.summary, /probably/i, "the message says 'probably'");
    });

    await t.step("after Escape, calls work again", async () => {
      const wait = Math.max(0, enteredAt + escapeAfterMs + 2_000 - Date.now());
      await sleep(wait);
      const r = await t.call("find_elements", { category: ["Walls"], count_only: true }, { allowError: true });
      t.assertEq(r.status, "ok", `read after Escape: ${r.code ?? ""}`);
    });

    await busyVariants(t, addin);
  },
};
