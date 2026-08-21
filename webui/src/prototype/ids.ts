/** Deterministic UUID-shaped identities for prototype resources. */
export function prototypeUuid(namespace: number, value: number): string {
  const ns = Math.max(0, namespace).toString(16).padStart(8, '0').slice(-8);
  const tail = Math.max(0, value).toString(16).padStart(12, '0').slice(-12);
  return `${ns}-0000-4000-8000-${tail}`;
}

export function prototypeNumericId(namespace: number, value: number): string {
  return String(namespace * 1_000_000 + value);
}
