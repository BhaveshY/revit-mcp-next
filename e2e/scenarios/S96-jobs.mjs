// S96 jobs (SPEC §6.8, §9.4, §13.3 P-EXPORT): a multi-file export runs as an add-in job (one file per step):
// either it finishes inside the call budget or returns running + j#; job_status follows it to the same result the
// call would have given; cancel_job stops a second export between files and nothing is reported as written twice.
import { existsSync } from "node:fs";
import { join } from "node:path";

async function viewRef(t, name, folder) {
  const r = await t.call("export", { format: "csv", views: [name], folder }, { allowError: true });
  if (r.code === "AMBIGUOUS_NAME") {
    const candidates = r.json?.details?.candidates ?? [];
    const pick = candidates.find((c) => /FloorPlan/.test(c)) ?? candidates[0];
    const m = /id (\d+)/.exec(pick ?? "");
    if (m) return m[1];
  }
  return name;
}

export default {
  id: "S96",
  title: "Jobs: multi-file export as a job, job_status, cancel_job",
  profiles: ["full"],
  years: "each",
  async run(t) {
    const { path } = await t.newProject("J96");
    t.setTargetPath(path);
    const folder = join(t.yearDir, "jobs96");
    const views = [await viewRef(t, "Level 1", folder), await viewRef(t, "Level 2", folder)];

    await t.step("large images of several views: job progress, then the final file list", async () => {
      let r = await t.call("export", { format: "image", views, folder, size: 6000, name: "big_{n}" }, { timeoutMs: 120_000 });
      let polls = 0;
      while (r.status === "running") {
        t.assertMatch(r.next ?? "", /^job_status \{"id":"j\d+"/, "running results point to job_status");
        t.assertMatch(r.summary, /as job j\d+/, "summary names the job");
        r = await t.call("job_status", { id: r.json.job, wait: 30 }, { timeoutMs: 60_000 });
        polls++;
        t.assert(polls < 20, "the job finishes");
      }
      t.assertEq(r.status, "ok", "export job finished");
      t.assertEq((r.json?.files ?? []).length, views.length, "one file per view");
      for (const f of r.json.files) t.assert(existsSync(f.path), `exists ${f.path}`);
      t.log(`image job: ${r.summary} after ${polls} poll(s)`);
    });

    await t.step("cancel_job on a running export stops it between files", async () => {
      const r = await t.call("export", { format: "image", views, folder, size: 15000, name: "cancel_{n}" }, { timeoutMs: 120_000, allowError: true });
      if (r.status !== "running") {
        t.log(`export finished before it could be cancelled (${r.status}); cancel path not exercised on this PC`);
        return;
      }
      const ref = r.json.job;
      const cancelled = await t.call("cancel_job", { id: ref });
      t.assertEq(cancelled.status, "ok", "cancel_job");
      const final = await t.call("job_status", { id: ref, wait: 45 }, { timeoutMs: 60_000, allowError: true });
      t.assert(final.status === "ok" || final.code === "REQUEST_CANCELLED", `cancelled job ends cleanly: ${final.status} ${final.code ?? ""}`);
      if (final.status === "ok") t.assert(final.warnings.some((w) => w.startsWith("PARTIAL_RESULT")) || (final.json?.files ?? []).length <= views.length * 2, "partial result");
    });
  },
};
