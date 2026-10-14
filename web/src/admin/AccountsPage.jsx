// AccountsPage — the accounts somebody may sign in with, where each stands, and the roles each holds.
//
// An account is the primary identity object: it exists on its own, and an external provider (Discord
// today, others later) is a credential attached to it rather than the source of it. What an account
// may do is the roles assigned to it — each everywhere, at a node or on one server — plus what
// `everyone` holds; approving somebody makes them active holding only that, and assigning a role is
// the next, separate act (`AccountPage`).
//
// The status filter is in the address (`#/accounts?status=pending`), so a link opens the queue. People
// waiting come first: they are the only rows that need something done, and a long list would otherwise
// bury them. The roles come from the authority, which only somebody administering some of it may read;
// without it the rows show no roles rather than claiming there are none.
//
// Every control is gated on the request it would make and every refusal is the provider's sentence.

import React from "react";

import {
  AccountAvatar, Button, Dialog, EmptyState, Field, Input, List, ListRow, PageHeader, PasswordInput, Pill,
  Select, SettingsRow, SettingsSection, Toolbar, ToolbarCount, ToolbarFilters, ToolbarSearch, useFilters, useStore,
} from "@thekrystalship/krystal-ui";
import { StatusPill, nameOf, providersOf, useAccounts } from "./accountParts.jsx";
import { api } from "./client.js";
import { Loading, RefusalNote, roleName } from "./kit.jsx";
import { go } from "./route.js";
import { accessStore, authorityStore, refusalOf, userRefusal } from "./stores.js";

const FILTERS = ["all", "pending", "active", "disabled"];
const ROLES_SHOWN = 3;

function AccountsPage({ params }) {
  // Re-rendered when the caller's own access moves, which is what every gate below reads.
  useStore(accessStore, (s) => s.report);
  const view = useStore(authorityStore, (s) => s.view);
  const { rows, refusal, setRefusal, reload } = useAccounts();
  const { query, setQuery, debouncedQuery } = useFilters({ search: "" });
  const [creating, setCreating] = React.useState(false);
  const [approving, setApproving] = React.useState(null);

  const status = FILTERS.includes(params.status) ? params.status : "all";
  const canApprove = !userRefusal("PATCH", "/_", { status: "active" });
  const canCreate = !userRefusal("POST", "");

  // Approve in place: the account becomes active holding what `everyone` holds, and nothing more.
  const approve = (u) => {
    setApproving(u.id);
    setRefusal(null);
    api.users.update(u.id, { status: "active" }).then(
      () => reload().then(() => setApproving(null)),
      (e) => { setRefusal(refusalOf(e)); setApproving(null); });
  };

  const all = rows || [];
  const needle = debouncedQuery.trim().toLowerCase();
  const shown = all
    .filter((u) => status === "all" || u.status === status)
    .filter((u) => !needle || nameOf(u).toLowerCase().includes(needle) || (u.username || "").toLowerCase().includes(needle))
    .sort((a, b) => (a.status === "pending" ? 0 : 1) - (b.status === "pending" ? 0 : 1));
  const countOf = (s) => all.filter((u) => u.status === s).length;
  const services = view ? view.accounts.filter((a) => a.kind === "service") : [];

  const rolesOf = (id) => {
    if (!view) return null;
    const ids = [...new Set(view.assignments.filter((a) => a.accountId === id).map((a) => a.roleId))];
    return ids.map((r) => roleName(view, r));
  };

  return (
    <>
      <PageHeader title="Accounts"
        actions={canCreate ? <Button variant="primary" icon="plus" onClick={() => setCreating(true)}>Add an account</Button> : null} />
      <RefusalNote refusal={refusal} />

      <Toolbar>
        <ToolbarSearch value={query} onChange={setQuery} placeholder="Search accounts" />
        <ToolbarFilters fields={[{
          id: "status", label: "Status", value: status, default: "all",
          options: [
            { value: "all", label: "All", count: all.length },
            { value: "pending", label: "Awaiting approval", count: countOf("pending") },
            { value: "active", label: "Active", count: countOf("active") },
            { value: "disabled", label: "Disabled", count: countOf("disabled") },
          ],
          onChange: (v) => go("accounts", null, { status: v === "all" ? null : v }),
        }]} />
        <ToolbarCount shown={shown.length} total={all.length} unit={all.length === 1 ? "account" : "accounts"} />
      </Toolbar>

      {rows === null ? <Loading /> : shown.length === 0 ? (
        !refusal && (
          <EmptyState icon="users" title={all.length === 0 ? "No accounts yet" : "No account matches"} />
        )
      ) : (
        <List label="Accounts">
          {shown.map((u) => {
            const held = rolesOf(u.id);
            const providers = providersOf(u);
            return (
              <ListRow key={u.id} data-account={u.username}
                lead={<AccountAvatar user={{ display: nameOf(u), name: u.username }} size={32} />}
                title={nameOf(u)}
                sub={[
                  u.username !== nameOf(u) ? u.username : null,
                  providers.length > 0 ? providers.join(", ") : null,
                  !u.hasPassword && providers.length === 0 ? "no way to sign in" : null,
                ].filter(Boolean).join(" · ") || undefined}
                meta={<>
                  {held && (held.length === 0
                    ? <Pill size="sm">everyone</Pill>
                    : held.slice(0, ROLES_SHOWN).map((r) => <Pill key={r} size="sm">{r}</Pill>))}
                  {held && held.length > ROLES_SHOWN && <Pill size="sm">+{held.length - ROLES_SHOWN}</Pill>}
                  <StatusPill status={u.status} />
                </>}
                actions={u.status === "pending" && canApprove ? (
                  <Button size="sm" variant="primary" busy={approving === u.id} disabled={approving === u.id}
                    onClick={() => approve(u)}>
                    {approving === u.id ? "Approving…" : "Approve"}
                  </Button>
                ) : null}
                onOpen={() => go("accounts", u.id)} />
            );
          })}
        </List>
      )}

      {services.length > 0 && (
        <SettingsSection icon="bot" title="Service accounts">
          {services.map((s) => (
            <SettingsRow key={s.id} icon="bot" title={s.username}
              sub={s.requirements.length + (s.requirements.length === 1 ? " requirement" : " requirements")}>
              <StatusPill status={s.status} />
            </SettingsRow>
          ))}
        </SettingsSection>
      )}

      {creating && <CreateDialog onClose={() => setCreating(false)} onCreated={() => { setCreating(false); reload(); }} />}
    </>
  );
}

// Naming a new account, optionally with a first password. It is created active, or waiting for approval.
function CreateDialog({ onClose, onCreated }) {
  const [form, setForm] = React.useState({ username: "", displayName: "", status: "active", password: "" });
  const [busy, setBusy] = React.useState(false);
  const [refusal, setRefusal] = React.useState(null);
  const set = (k) => (e) => setForm((f) => ({ ...f, [k]: e.target.value }));
  const username = form.username.trim();

  const submit = async (e) => {
    e.preventDefault();
    if (!username || busy) return;
    setBusy(true);
    setRefusal(null);
    try {
      await api.users.create({
        username,
        displayName: form.displayName.trim() || username,
        status: form.status,
        password: form.password || undefined,
      });
      onCreated();
    } catch (err) {
      setBusy(false);
      setRefusal(refusalOf(err));
    }
  };

  return (
    <Dialog title="Add an account" icon="user-plus" as="form" onSubmit={submit} onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="primary" type="submit" busy={busy} disabled={!username}>{busy ? "Creating…" : "Create"}</Button>
        </>
      )}>
      <div data-dialog="create-account" className="admin-form">
        <RefusalNote refusal={refusal} compact />
        <Field label="Username">
          <Input id="user-username" wide value={form.username} autoCapitalize="off" spellCheck="false" autoFocus
            disabled={busy} onChange={set("username")} />
        </Field>
        <Field label="Display name">
          <Input id="user-display" wide value={form.displayName} disabled={busy} onChange={set("displayName")} />
        </Field>
        <Field label="Status">
          <Select id="user-status" value={form.status} disabled={busy} onChange={set("status")}>
            <option value="active">Active</option>
            <option value="pending">Awaiting approval</option>
          </Select>
        </Field>
        <Field label="Password" aside="optional">
          <PasswordInput id="user-password" wide value={form.password} autoComplete="new-password"
            disabled={busy} onChange={set("password")} />
        </Field>
      </div>
    </Dialog>
  );
}

export { AccountsPage };
