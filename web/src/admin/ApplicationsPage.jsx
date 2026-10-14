// ApplicationsPage — the applications this provider signs people in to: register one and open one.
//
// An application is the API its clients' tokens are for: an id that is also the namespace of its
// actions and the audience its resource server checks. KGSM and tks-auth itself are built in, listed and
// never edited. Registering one with a confidential client answers with that client's secret, which the
// application page opened next shows once.

import React from "react";

import { Button, EmptyState, Icon, List, ListRow, PageHeader, Pill } from "@thekrystalship/krystal";
import { APPLICATIONS } from "./client.js";
import { RegisterDialog } from "./applicationParts.jsx";
import { keepSecrets, refreshApplications, useApplications } from "./applicationsStore.js";
import { Brief, Loading } from "./kit.jsx";
import { countOf } from "./roleParts.jsx";
import { go } from "./route.js";
import { callRefusal } from "./stores.js";

// What the application's manifest last said, in words: its action count, or why it could not be read.
const manifestText = (app) => (!app.manifest ? null
  : app.manifest.taken ? countOf(app.manifest.actions, "action") : app.manifest.problem);

// An application's id, its audience when that is another word, and what its manifest said.
const identityText = (app, manifest) => [app.id, app.audience !== app.id ? app.audience : null, manifest].filter(Boolean).join(" · ");

function ApplicationsPage() {
  const { allowed, status, list, error } = useApplications();
  const [registering, setRegistering] = React.useState(false);

  if (!allowed) return <Brief icon="lock" title="You don’t have access to this" />;
  if (status === "error" && !list) return <Brief icon="wifi-off" tone="warn" title="Couldn’t read the applications" sub={error && error.userMessage} />;
  if (!list) return <Loading />;

  const registered = (id, answer) => {
    keepSecrets(id, answer);
    refreshApplications();
    go("applications", id);
  };

  return (
    <>
      <PageHeader title="Applications"
        actions={!callRefusal("POST", APPLICATIONS)
          ? <Button variant="primary" icon="plus" onClick={() => setRegistering(true)}>Register an application</Button>
          : null} />

      {list.length === 0 ? <EmptyState icon="app-window" title="No applications" /> : (
        <List label="Applications">
          {list.map((app) => {
            const builtin = app.source === "builtin";
            const manifest = manifestText(app);
            return (
              <ListRow key={app.id} data-application={app.id}
                lead={<Icon name={builtin ? "lock" : "app-window"} size={16} />}
                title={app.name}
                sub={identityText(app, manifest)}
                meta={builtin ? <Pill size="sm" tone="muted">Built in</Pill> : null}
                aside={countOf(app.clients.length, "client")}
                onOpen={() => go("applications", app.id)} />
            );
          })}
        </List>
      )}

      {registering && <RegisterDialog onDone={registered} onClose={() => setRegistering(false)} />}
    </>
  );
}

export { ApplicationsPage, identityText, manifestText };
