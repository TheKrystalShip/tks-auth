# `Auth.Journal` — locked decisions

- **It has ZERO package dependencies.** It is the names and the payload writers of the provider's audit
  events. Anything that needs a logger or a container belongs in the caller (`AuditLog`).
- **The names and the payload writers live together and are called, never copied,** so a field is spelled
  one way on every line of one type.
- **An absent value is a real null, never an empty string.** "Nobody looked this up" and "this is blank"
  are different facts, and a reader that meets `""` cannot tell which it has.
- **Facts, not sentences.** No summary, no severity, no formatted value: a reader builds those at read
  time, which keeps a wording improvement from applying only to lines written after it.
- **It never takes a password parameter.** What is recorded is that a credential was set and by whom — the
  only signal an account takeover leaves — and the credential is not part of that fact.
