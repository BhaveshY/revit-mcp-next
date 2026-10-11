// S78 worksharing (SPEC §13.3, §5.9 "manage_document open (central)"): enable -> central; opening the central
// creates a local (IsWorkshared && !central, path != central) and leaves the central file untouched until sync;
// central:true needs confirm; worksets create/rename/set_active/move_to; borrow; sync (confirm, job); relinquish;
// reload_latest.
import { existsSync, statSync } from "node:fs";
import { join } from "node:path";

async function settle(t, r, label) {
  let current = r;
  for (let i = 0; i < 12 && current.status === "running"; i++) {
    const ref = current.json?.job ?? current.json?.ref;
    t.assert(ref, `${label}: running result names its job`);
    current = await t.call("job_status", { id: ref, wait: 30 }, { timeoutMs: 60_000 });
  }
  return current;
}

async function confirm(t, tool, r, op) {
  t.assertEq(r.status, "not_applied", `${tool}.${op} asks to confirm`);
  t.assert(r.json?.confirm, `${tool}.${op} carries a confirm token`);
  return settle(t, await t.call(tool, { op, confirm: r.json.confirm }, { timeoutMs: 120_000 }), `${tool}.${op}`);
}

const mtime = (path) => statSync(path).mtimeMs;

export default {
  id: "S78",
  title: "Worksharing: enable, local copy on open, worksets, borrow, sync, relinquish, reload latest",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const central = await t.step("enable worksharing on W (always confirms) saves it as the central", async () => {
      const { r, path } = await t.newProject("W");
      t.assertEq(r.status, "ok", "new_project W");
      t.setTargetPath(path);
      await t.expectError("worksharing", { op: "relinquish" }, "UNSUPPORTED_OP");
      const enabled = await confirm(t, "worksharing", await t.call("worksharing", { op: "enable" }), "enable");
      t.assertEq(enabled.status, "ok", "enable");
      t.assertEq(enabled.json?.central?.toLowerCase(), path.toLowerCase(), "saved as central");
      return path;
    });

    await t.step("close the central (another doc is open first)", async () => {
      const { path } = await t.newProject("Scratch78");
      t.setTargetPath(path);
      const closed = await settle(t, await t.call("manage_document", { op: "close", doc: "W" }), "close W");
      t.assertEq(closed.status, "ok", "close W");
    });

    const local = join(t.yearDir, "W_local.rvt");
    const centralBefore = mtime(central);
    await t.step("opening the central with central:true needs confirm (not applied)", async () => {
      const r = await t.call("manage_document", { op: "open", path: central, central: true });
      t.assertEq(r.status, "not_applied", "central_open");
      t.assertEq(r.json?.rule, "central_open", "rule");
    });

    await t.step("open the central creates a local copy and opens it", async () => {
      const r = await t.call("manage_document", { op: "open", path: central, local }, { timeoutMs: 180_000 });
      t.assertEq(r.status, "ok", "open central");
      t.assert(r.json?.is_local === true && r.json?.created_local === true, `local created: ${r.jsonText}`);
      t.assert(existsSync(local), "local file exists");
      t.assert(String(r.json?.path).toLowerCase() !== central.toLowerCase(), "the opened doc is not the central");
      t.assert(r.notices.some((n) => n.startsWith("LOCAL_COPY")), "LOCAL_COPY notice");
      t.assert(r.notices.some((n) => n.startsWith("TARGET_NOW")), "re-pinned");
      t.setTargetPath(local);
      t.assertEq(mtime(central), centralBefore, "the central file is unchanged by opening");
    });

    await t.step("worksets: create, NAME_TAKEN, rename, set_active", async () => {
      const created = await t.call("worksharing", { op: "workset_create", name: "Facade" });
      t.assertEq(created.status, "ok", "workset_create");
      const taken = await t.expectError("worksharing", { op: "workset_create", name: "Facade" }, "NAME_TAKEN");
      t.assert(/Facade 2/.test(taken.fix ?? ""), `fix suggests the next name: ${taken.fix}`);
      const renamed = await t.call("worksharing", { op: "workset_rename", workset: "Facade", name: "Facade North" });
      t.assertEq(renamed.status, "ok", "workset_rename");
      const active = await t.call("worksharing", { op: "set_active", workset: "Facade North" });
      t.assertEq(active.status, "ok", "set_active");
    });

    await t.step("move levels to a workset (filter) and borrow them", async () => {
      const moved = await t.call("worksharing", { op: "move_to", workset: "Facade North", filter: { category: "Levels" } });
      t.assertEq(moved.status, "ok", "move_to");
      t.assert(Number(moved.json?.moved) >= 1, `moved levels: ${moved.jsonText}`);
      const borrowed = await t.call("worksharing", { op: "borrow", filter: { category: "Levels" } });
      t.assertEq(borrowed.status, "ok", "borrow");
      t.assertEq(mtime(central), centralBefore, "nothing reached the central before sync");
    });

    await t.step("sync (confirm, job) writes the central; relinquish; reload_latest", async () => {
      const synced = await confirm(t, "worksharing", await t.call("worksharing", { op: "sync", comment: "S78 e2e" }), "sync");
      t.assertEq(synced.status, "ok", "sync");
      t.assert(mtime(central) > centralBefore, "the central changed after sync");
      const relinquished = await t.call("worksharing", { op: "relinquish" });
      t.assertEq(relinquished.status, "ok", "relinquish");
      const reloaded = await settle(t, await t.call("worksharing", { op: "reload_latest" }, { timeoutMs: 120_000 }), "reload_latest");
      t.assertEq(reloaded.status, "ok", "reload_latest");
    });

    await t.step("save_as of a workshared doc without as_central is refused", async () => {
      await t.expectError("manage_document", { op: "save_as", path: join(t.yearDir, "W_other.rvt") }, "INVALID_ARGS");
    });
  },
};
