// Read coalescing (D2 §11.6): identical reads in flight share one add-in call. An abort from one subscriber detaches
// only that subscriber; the shared call is aborted when the last subscriber leaves. Reads only (never writes).

export class ReadCoalescer<T> {
  private readonly inflight = new Map<string, { promise: Promise<T>; refs: number; controller: AbortController }>();

  get size(): number {
    return this.inflight.size;
  }

  /** Run `fn` once per key while in flight; `fn` receives a signal that aborts when every subscriber left. */
  run(key: string, fn: (signal: AbortSignal) => Promise<T>, signal?: AbortSignal): Promise<{ value: T; coalesced: boolean }> {
    let entry = this.inflight.get(key);
    let coalesced = true;
    if (!entry) {
      coalesced = false;
      const controller = new AbortController();
      const promise = fn(controller.signal).finally(() => {
        if (this.inflight.get(key)?.promise === promise) this.inflight.delete(key);
      });
      entry = { promise, refs: 0, controller };
      this.inflight.set(key, entry);
    }
    const current = entry;
    current.refs += 1;
    return new Promise((resolve, reject) => {
      let done = false;
      const leave = () => {
        if (done) return;
        done = true;
        current.refs -= 1;
        if (current.refs <= 0) current.controller.abort();
      };
      if (signal) {
        if (signal.aborted) {
          leave();
          reject(new Error("aborted"));
          return;
        }
        signal.addEventListener(
          "abort",
          () => {
            if (done) return;
            leave();
            reject(new Error("aborted"));
          },
          { once: true }
        );
      }
      current.promise.then(
        (value) => {
          if (done) return;
          done = true;
          current.refs -= 1;
          resolve({ value, coalesced });
        },
        (error) => {
          if (done) return;
          done = true;
          current.refs -= 1;
          reject(error);
        }
      );
    });
  }
}
