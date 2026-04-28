/**
 * Parse a timestamp from the backend as UTC.
 *
 * The backend sends ISO-8601 strings without a timezone suffix
 * (e.g. "2026-04-27T10:00:00"). JavaScript's Date constructor treats those
 * as LOCAL time, which shifts relative-time calculations on UTC+ systems.
 * Appending "Z" forces correct UTC interpretation.
 */
export function parseUtcDate(value) {
  if (!value) return null;

  // Already has explicit timezone info — use as-is
  if (typeof value === "string" && (value.endsWith("Z") || /[+-]\d{2}:\d{2}$/.test(value))) {
    return new Date(value);
  }

  // Assume UTC and append Z
  return new Date(value + "Z");
}

/**
 * Timezone-safe date formatter.
 *
 * Dates stored in the backend as YYYY-MM-DD strings or ISO-8601 datetimes
 * must be displayed in local time. Using toISOString() converts to UTC first,
 * which produces a one-day rollback for UTC+ timezones (e.g. UTC+2 South Africa).
 *
 * Strategy:
 *  1. If the value is already a YYYY-MM-DD string, return it as-is (no parsing needed).
 *  2. Otherwise, parse with Date and use toLocaleDateString('en-CA') which returns
 *     YYYY-MM-DD in the local timezone — no UTC conversion.
 */
export function formatDate(value) {
  if (!value) {
    return "-";
  }

  // Already a plain date string (YYYY-MM-DD[...]) — safe to slice directly
  if (typeof value === "string" && value.length >= 10 && /^\d{4}-\d{2}-\d{2}/.test(value)) {
    return value.slice(0, 10);
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "-";
  }

  // en-CA locale formats as YYYY-MM-DD using local timezone
  return date.toLocaleDateString("en-CA");
}
