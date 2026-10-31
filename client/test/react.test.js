import { act, renderHook } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { createSession } from "../src/index.js";
import { useSession } from "../src/react.js";
import { jwt } from "./provider.js";

describe("useSession", () => {
  it("renders the snapshot and re-renders when it changes", () => {
    const session = createSession({ issuer: "https://auth.test", clientId: "site" });
    const { result } = renderHook(() => useSession(session));
    expect(result.current.status).toBe("none");

    act(() => session.adopt({ access_token: jwt({ sub: "u", exp: Math.floor(Date.now() / 1000) + 300 }), expires_in: 300 }, async () => null));
    expect(result.current.status).toBe("live");

    act(() => session.end("revoked"));
    expect(result.current).toMatchObject({ status: "ended", reason: "revoked" });
  });
});
