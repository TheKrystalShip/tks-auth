import { afterEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, render, screen } from "@testing-library/react";

vi.mock("../auth.js", async () => {
  const { createStandInSession } = await import("@thekrystalship/auth-client/testing");
  const { useSession } = await import("@thekrystalship/auth-client/react");
  return { session: createStandInSession(), useSession };
});
vi.mock("../AdminApp.jsx", () => ({ AdminApp: ({ account }) => <div>admin app for {account.username}</div> }));

import { Gate } from "../Gate.jsx";
import { session } from "../auth.js";

const ada = { claims: { sub: "u1", name: "Ada Lovelace", preferred_username: "ada" } };

afterEach(async () => {
  cleanup();
  await act(async () => { await session.signOut(); });
  vi.restoreAllMocks();
});

describe("the gate", () => {
  it("draws the pages for a signed-in session", () => {
    act(() => session.adopt(ada, async () => null));
    render(<Gate />);
    expect(screen.getByText("admin app for ada")).toBeTruthy();
  });

  it("draws one screen with one action when the session has ended", () => {
    act(() => session.end("refused"));
    render(<Gate />);
    expect(screen.getByText("You have been signed out.")).toBeTruthy();
    expect(screen.getAllByRole("button")).toHaveLength(1);
  });

  it("says so when the account no longer administers", () => {
    act(() => session.end("denied"));
    render(<Gate />);
    expect(screen.getByText("You don’t have access to this.")).toBeTruthy();
    expect(screen.getAllByRole("button")).toHaveLength(1);
  });

  it("signs in again from the ended screen", () => {
    const signIn = vi.spyOn(session, "signIn");
    act(() => session.end("revoked"));
    render(<Gate />);
    act(() => screen.getByRole("button", { name: "Sign in again" }).click());
    expect(signIn).toHaveBeenCalled();
  });

  it("sends a signed-out visitor to sign in and draws nothing", () => {
    const signIn = vi.spyOn(session, "signIn");
    const { container } = render(<Gate />);
    expect(signIn).toHaveBeenCalled();
    expect(container.textContent).toBe("");
  });

  it("draws nothing of its own while the session is stale or restoring", () => {
    act(() => session.adopt(ada, async () => null));
    const { container, rerender } = render(<Gate />);
    expect(container.textContent).toBe("admin app for ada");
    expect(container.querySelector("[role=alert], [role=status]")).toBeNull();
    rerender(<Gate />);
    expect(container.textContent).toBe("admin app for ada");
  });
});
