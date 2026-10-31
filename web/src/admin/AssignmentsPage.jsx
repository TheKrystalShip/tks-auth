// AssignmentsPage — who holds which role, and giving or taking one away.

import { PageHeader, SettingsSection } from "@thekrystalship/krystal";
import { Assignments } from "./Assignments.jsx";
import { authorityGate, useAuthority } from "./kit.jsx";

function AssignmentsPage() {
  const state = useAuthority();

  return (
    <>
      <PageHeader title="Assignments" />
      {authorityGate(state) || (
        <SettingsSection icon="user-check" title="Roles held">
          <Assignments view={state.view} />
        </SettingsSection>
      )}
    </>
  );
}

export { AssignmentsPage };
