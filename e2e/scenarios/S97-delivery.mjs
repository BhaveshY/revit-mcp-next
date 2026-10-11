// S97 model delivery (SPEC §13.3, P-EXPORT): fixture (always confirm, job) -> save/list/get recipe -> inspect with
// the saved recipe -> preview gives the execute token -> execute (job) publishes the package; sources unchanged.
import { createHash } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";

async function settle(t, r, label) {
  let current = r;
  for (let i = 0; i < 40 && current.status === "running"; i++) {
    const ref = current.json?.job ?? current.json?.ref;
    t.assert(ref, `${label}: running result names its job`);
    current = await t.call("job_status", { id: ref, wait: 40 }, { timeoutMs: 60_000 });
  }
  return current;
}

const sha = (path) => createHash("sha256").update(readFileSync(path)).digest("hex");

export default {
  id: "S97",
  title: "Model delivery: fixture, recipe library, inspect, preview token, execute job",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const folder = join(t.yearDir, "delivery97");
    const id = `s97-${t.year}`;

    const fixture = await t.step("fixture needs confirm, then creates two central models and a recipe", async () => {
      const plan = await t.call("model_delivery", { op: "fixture", template: t.projectTemplate(), folder, id });
      t.assertEq(plan.status, "not_applied", "fixture asks to confirm");
      const r = await settle(t, await t.call("model_delivery", { op: "fixture", confirm: plan.json.confirm }, { timeoutMs: 120_000 }), "fixture");
      t.assertEq(r.status, "ok", "fixture");
      t.assert(r.json?.recipe?.projectId === id, "recipe for the fixture");
      for (const p of r.json.sourcePaths) t.assert(existsSync(p), `source ${p}`);
      return r.json;
    });
    const before = Object.fromEntries(fixture.sourcePaths.map((p) => [p, sha(p)]));

    await t.step("save_recipe, list_recipes, get_recipe", async () => {
      const saved = await t.call("model_delivery", { op: "save_recipe", recipe: fixture.recipe });
      t.assertEq(saved.status, "ok", "save_recipe");
      t.assertMatch(saved.json?.sha ?? "", /^[0-9a-f]{64}$/, "sha");
      const again = await t.call("model_delivery", { op: "save_recipe", recipe: fixture.recipe });
      t.assertEq(again.json?.changed, false, "unchanged recipe is not saved twice");
      await t.expectError("model_delivery", { op: "save_recipe", recipe: fixture.recipe, expected_sha: "0".repeat(64) }, "INVALID_ARGS");
      const list = await t.call("model_delivery", { op: "list_recipes" });
      t.assert(list.json?.rows?.some((row) => row[0] === id), "listed");
      const got = await t.call("model_delivery", { op: "get_recipe", project: id });
      t.assertEq(got.json?.recipe?.projectId, id, "get_recipe");
      await t.expectError("model_delivery", { op: "get_recipe", project: `${id}-missing` }, "NOT_FOUND");
    });

    await t.step("inspect with the saved recipe", async () => {
      const r = await settle(t, await t.call("model_delivery", { op: "inspect", sources: fixture.sourcePaths, project: id }, { timeoutMs: 120_000 }), "inspect");
      t.assertEq(r.status, "ok", "inspect");
      t.assert(r.notices.some((n) => n.startsWith("RECIPE_LOADED")), "saved recipe used");
      t.assertEq((r.json?.models ?? []).length, fixture.sourcePaths.length, "every source inspected");
    });

    const token = await t.step("preview returns a ready plan and the execute token", async () => {
      let r = await t.call("model_delivery", { op: "preview", recipe: fixture.recipe }, { timeoutMs: 120_000 });
      if (r.status === "running") {
        r = await settle(t, r, "preview");
        const notice = await t.call("status", {});
        const ready = notice.notices.find((n) => n.startsWith("DELIVERY_READY"));
        t.assert(ready, "DELIVERY_READY notice after a long preview");
        return /"confirm":"([A-Z0-9]+)"/.exec(ready)?.[1];
      }
      t.assertEq(r.status, "ok", "preview");
      t.assertEq(r.json?.ready, true, `ready: ${JSON.stringify(r.json?.blockers)}`);
      t.assertMatch(r.next ?? "", /^ask the user, then model_delivery \{"op":"execute","confirm":"[A-Z0-9]{6}"\}/, "next names execute");
      return r.json.confirm;
    });

    await t.step("execute without a token is refused; with it the job publishes the package", async () => {
      await t.expectError("model_delivery", { op: "execute", confirm: "ZZZZZZ" }, "CONFIRM_EXPIRED");
      const r = await settle(t, await t.call("model_delivery", { op: "execute", confirm: token }, { timeoutMs: 120_000 }), "execute");
      t.assertEq(r.status, "ok", `execute: ${r.summary}`);
      t.assertEq(r.json?.state, "succeeded", "delivery succeeded");
      t.assert(existsSync(r.json.packagePath), "package published");
      for (const [p, h] of Object.entries(before)) t.assertEq(sha(p), h, `source unchanged: ${p}`);
      await t.expectError("model_delivery", { op: "execute", confirm: token }, "CONFIRM_EXPIRED");
    });
  },
};
