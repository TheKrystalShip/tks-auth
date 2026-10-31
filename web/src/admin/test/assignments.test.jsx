import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";

vi.mock("../stores.js", () => ({ authorityStore: { edit: vi.fn() }, editRefusal: () => null }));
vi.mock("../kit.jsx", () => ({
  Locked: () => null,
  RefusalNote: () => null,
  roleName: (view, id) => view.roles.find((r) => r.id === id)?.name ?? id,
  useChecks: () => null,
}));

import { Assignments } from "../Assignments.jsx";

afterEach(cleanup);

// The accounts the authority read carries have no kind: every one is a person.
const view = {
  accounts: [
    { id: "usr_ada", username: "ada", displayName: "Ada Lovelace", status: "active" },
    { id: "usr_bob", username: "bob", displayName: "Bob", status: "pending" },
  ],
  roles: [
    { id: "role_owner", name: "Owner", kind: "owner", rank: 0, permissions: [] },
    { id: "role_ops", name: "Operator", kind: "custom", rank: 2, permissions: [] },
  ],
  assignments: [],
};

describe("the assignments editor", () => {
  it("offers every active account and no other", () => {
    render(<Assignments view={view} />);
    const options = [...screen.getByLabelText("Account").querySelectorAll("option")].map((o) => o.textContent);
    expect(options).toEqual(["Account…", "Ada Lovelace"]);
  });

  it("offers the roles an account can be given", () => {
    render(<Assignments view={view} />);
    const options = [...screen.getByLabelText("Role").querySelectorAll("option")].map((o) => o.textContent);
    expect(options).toEqual(["Role…", "Owner", "Operator"]);
  });
});
