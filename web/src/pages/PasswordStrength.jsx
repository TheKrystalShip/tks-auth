import { Meter } from "@thekrystalship/krystal-ui";
import { passwordStrength } from "../lib/credentialRules.js";

// PasswordStrength — a typed password's strength as four segments and a word, beneath its field.

// The segments lit by a strength level, toned by how far it is.
const STRENGTH_TONES = [null, "danger", "warn", "accent", "success"];

function PasswordStrength({ value }) {
  const { level, label } = passwordStrength(value);
  if (!level) return null;
  return (
    <Meter label="Password strength" max={4} valueText={label} data-strength={level}
      segments={[1, 2, 3, 4].map((i) => ({ value: 1, tone: i <= level ? STRENGTH_TONES[level] : "neutral" }))}
      end={label} />
  );
}

export { PasswordStrength };
