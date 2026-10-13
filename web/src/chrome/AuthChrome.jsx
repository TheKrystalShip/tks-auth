import { Icon } from "@thekrystalship/krystal-ui";

// The furniture the wait and device pages share: the shell with its brand, and the refusal.

function AuthShell({ tagline, children }) {
  return (
    <div className="login-shell">
      <div className="login-shell__inner">
        <div className="login-shell__brand">
          <img src={import.meta.env.BASE_URL + "assets/tks-mark.png"} alt="" />
          <div className="login-shell__brand-name">The Krystal Ship</div>
          {tagline ? <div className="login-shell__tagline">{tagline}</div> : null}
        </div>
        {children}
      </div>
    </div>
  );
}

// A refusal. `field` places it inside the form, above what it is about; without it the
// message sits at the top of the card, which is where a fact about the PROVIDER belongs
// because that invalidates every door on the card rather than one of them.
function AuthError({ children, field }) {
  if (!children) return null;
  return (
    <div className={"login-error" + (field ? " login-error--field" : "")} role="alert">
      <Icon name="alert-triangle" size={15} />
      <div>{children}</div>
    </div>
  );
}

export { AuthError, AuthShell };
