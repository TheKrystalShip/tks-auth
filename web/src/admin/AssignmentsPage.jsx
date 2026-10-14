// AssignmentsPage — who holds which role at one scope, and giving or taking one away.
//
// The scope is the page's parameter, so another surface links here with it filled in — a KGSM server's
// "manage access" opens the roles held on that install — and the name that surface knows it by rides
// along as `label`, since this provider holds no record of servers.

import { PageHeader, Select, SettingsSection } from "@thekrystalship/krystal-ui";
import { Assignments } from "./Assignments.jsx";
import { authorityGate, nameScope, useAuthority, useScopeOptions, useScopeText } from "./kit.jsx";
import { go } from "./route.js";

function AssignmentsPage({ params }) {
  const scope = params.scope || "cluster";
  nameScope(scope, params.label);
  const state = useAuthority();
  const options = useScopeOptions(state.view, scope);
  const scopeText = useScopeText();

  return (
    <>
      <PageHeader title="Assignments" sub={scopeText(scope)}
        actions={(
          <Select className="admin-scope" value={scope} aria-label="Scope"
            onChange={(e) => go("assignments", null, { scope: e.target.value, label: params.label })}>
            {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
          </Select>
        )} />
      {authorityGate(state) || (
        <SettingsSection icon="user-check" title="Roles held">
          <Assignments view={state.view} scope={scope} />
        </SettingsSection>
      )}
    </>
  );
}

export { AssignmentsPage };
