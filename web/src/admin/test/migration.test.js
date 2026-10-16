import { beforeEach, describe, expect, it } from "vitest";

import { adoptStoredUser } from "../auth.js";

const ISSUER = "https://auth.example.com";
const OLD = `tks-auth.admin.user:${ISSUER}:tks-auth`;
const NEW = `tks-auth-client:tks-auth:user:${ISSUER}:tks-auth`;

// The storage the page hands over, as a Storage-shaped map.
let localStorage;
beforeEach(() => {
  const map = new Map();
  localStorage = {
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { map.set(k, String(v)); },
    removeItem: (k) => { map.delete(k); },
    get length() { return map.size; },
  };
});

describe("the stored administrator session", () => {
  it("moves to the package's key with the same JSON", () => {
    localStorage.setItem(OLD, '{"refresh_token":"r1"}');
    adoptStoredUser(localStorage, ISSUER);
    expect(localStorage.getItem(NEW)).toBe('{"refresh_token":"r1"}');
    expect(localStorage.getItem(OLD)).toBeNull();
  });

  it("leaves a session already under the package's key and drops the old one", () => {
    localStorage.setItem(OLD, '{"refresh_token":"old"}');
    localStorage.setItem(NEW, '{"refresh_token":"new"}');
    adoptStoredUser(localStorage, ISSUER);
    expect(localStorage.getItem(NEW)).toBe('{"refresh_token":"new"}');
    expect(localStorage.getItem(OLD)).toBeNull();
  });

  it("does nothing when nothing is stored", () => {
    adoptStoredUser(localStorage, ISSUER);
    expect(localStorage.length).toBe(0);
  });
});
