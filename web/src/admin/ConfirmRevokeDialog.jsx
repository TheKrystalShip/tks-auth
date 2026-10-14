import { Button, Dialog } from "@thekrystalship/krystal";

// ConfirmRevokeDialog — the confirmation in front of ending somebody else's session, for whoever
// holds auth:accounts.disable, on the account's page. A person's own sessions are ended on their
// own account document, which asks for its own confirmation.
//
// Two variants over the same markup:
//   "other-one" — one of another person's sessions
//   "other-all" — every one of another person's, on every device
//
// Both take `targetName` so the copy names who is affected. A destructive action against another
// person is never anonymous: whoever is about to sign somebody out is owed the name they will have
// to explain it to.
function ConfirmRevokeDialog({ mode, targetName, busy, onConfirm, onClose }) {
  const isAll = mode === "other-all";
  const who = targetName || "that account";

  const title = isAll ? `Sign ${who} out everywhere?` : `End this session for ${who}?`;
  const text = isAll
    ? `This ends every active session for ${who}, on every device. They can sign in again straight away — this ends the sessions, it does not disable the account.`
    : `This ends that one session. ${who}'s other devices stay signed in.`;

  return (
    <Dialog title={title} icon="log-out" onClose={onClose} canClose={!busy}
      foot={(
        <>
          <Button variant="outline" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button variant="danger" icon="log-out" busy={busy} onClick={onConfirm}>
            {busy ? "Working…" : isAll ? "Sign out" : "End session"}
          </Button>
        </>
      )}>
      <p className="admin-dialog-text" data-dialog="confirm-revoke">{text}</p>
    </Dialog>
  );
}

export { ConfirmRevokeDialog };
