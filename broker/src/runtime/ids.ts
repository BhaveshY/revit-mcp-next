// ULID request ids (26 chars Crockford base32, monotonic within a millisecond) and short-id helpers.

import { randomBytes } from "node:crypto";

const CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
let lastTime = -1;
let lastRandom: number[] = [];

function encodeTime(time: number): string {
  let out = "";
  let t = time;
  for (let i = 0; i < 10; i++) {
    out = CROCKFORD[t % 32] + out;
    t = Math.floor(t / 32);
  }
  return out;
}

function freshRandom(): number[] {
  const bytes = randomBytes(16);
  const digits: number[] = [];
  for (let i = 0; i < 16; i++) digits.push(bytes[i]! % 32);
  return digits;
}

function increment(digits: number[]): number[] {
  const next = [...digits];
  for (let i = next.length - 1; i >= 0; i--) {
    if (next[i]! < 31) {
      next[i] = next[i]! + 1;
      return next;
    }
    next[i] = 0;
  }
  return freshRandom();
}

/** New ULID; monotonic within the same millisecond. */
export function ulid(now = Date.now()): string {
  if (now === lastTime) lastRandom = increment(lastRandom);
  else {
    lastTime = now;
    lastRandom = freshRandom();
  }
  return encodeTime(now) + lastRandom.map((d) => CROCKFORD[d]).join("");
}

export const ULID_PATTERN = /^[0-9A-HJKMNP-TV-Z]{26}$/;

/** Confirm token alphabet (no 0, O, 1, I, L, U): 6 chars, case-insensitive (SPEC §6.4). */
export const TOKEN_ALPHABET = "ABCDEFGHJKMNPQRSTVWXYZ23456789";

export function confirmToken(): string {
  const bytes = randomBytes(6);
  let out = "";
  for (let i = 0; i < 6; i++) out += TOKEN_ALPHABET[bytes[i]! % TOKEN_ALPHABET.length];
  return out;
}

export function normalizeToken(token: string): string {
  return token.trim().toUpperCase();
}

export function randomHex(length: number): string {
  return randomBytes(Math.ceil(length / 2)).toString("hex").slice(0, length);
}
