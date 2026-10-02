// Assertions for scenarios. Failures throw AssertionFailure with a readable message; the runner records them.

export class AssertionFailure extends Error {
  constructor(message, details) {
    super(message);
    this.name = "AssertionFailure";
    this.details = details;
  }
}

export function assert(condition, message, details) {
  if (!condition) throw new AssertionFailure(message, details);
}

export function assertEq(actual, expected, message) {
  if (!Object.is(actual, expected)) throw new AssertionFailure(`${message}: expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)}`);
}

export function assertMatch(text, pattern, message) {
  if (typeof text !== "string" || !pattern.test(text)) throw new AssertionFailure(`${message}: ${JSON.stringify(text)?.slice(0, 300)} does not match ${pattern}`);
}

export function assertLe(actual, max, message) {
  if (!(actual <= max)) throw new AssertionFailure(`${message}: ${actual} > ${max}`);
}

/** Structural deep equality (object key order ignored, array order kept). */
export function deepEqual(a, b) {
  if (a === b) return true;
  if (typeof a !== typeof b || a === null || b === null || typeof a !== "object") return false;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  if (Array.isArray(a)) return a.length === b.length && a.every((v, i) => deepEqual(v, b[i]));
  const ka = Object.keys(a);
  const kb = Object.keys(b);
  return ka.length === kb.length && ka.every((k) => Object.prototype.hasOwnProperty.call(b, k) && deepEqual(a[k], b[k]));
}

export function assertDeepEqual(actual, expected, message) {
  if (!deepEqual(actual, expected)) throw new AssertionFailure(`${message}: values differ`, { actual: JSON.stringify(actual)?.slice(0, 400), expected: JSON.stringify(expected)?.slice(0, 400) });
}

/** Assert on the parsed JSON line of a call result. */
export function assertJson(result, predicate, message = "JSON line check") {
  let ok = false;
  try {
    ok = !!predicate(result.json);
  } catch (error) {
    throw new AssertionFailure(`${message}: ${error.message}`, { json: JSON.stringify(result.json)?.slice(0, 400) });
  }
  if (!ok) throw new AssertionFailure(message, { json: JSON.stringify(result.json)?.slice(0, 400) });
}
