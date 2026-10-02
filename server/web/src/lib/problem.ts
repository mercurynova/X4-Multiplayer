import { ApiError } from '../api/http';

export interface FormErrors {
  /** Field name (camelCase, as in the request body) to the first message for it. */
  fields: Record<string, string>;
  /** Every message per field, in case a form wants to show them all. */
  all: Record<string, string[]>;
  /** Message for the form as a whole: the problem detail/title, or a generic text for non-API failures. */
  form: string;
}

const lowerFirst = (s: string) => s.charAt(0).toLowerCase() + s.slice(1);

/**
 * Maps a thrown value to form errors. An `ApiProblem` with an `errors` bag (the 400 `ValidationFailed` shape) becomes
 * per-field messages; keys are normalised to camelCase so they match the request DTO property names. Anything else
 * becomes a form-level message only.
 */
export function problemToFormErrors(e: unknown): FormErrors {
  if (e instanceof ApiError) {
    const all: Record<string, string[]> = {};
    const fields: Record<string, string> = {};
    for (const [key, msgs] of Object.entries(e.errors ?? {})) {
      const k = lowerFirst(key);
      all[k] = msgs;
      if (msgs.length > 0) fields[k] = msgs[0] ?? '';
    }
    return { fields, all, form: e.message };
  }
  return { fields: {}, all: {}, form: 'Could not reach the server.' };
}
