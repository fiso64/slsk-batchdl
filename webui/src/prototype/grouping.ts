export interface AdjacentGroup<T> {
  key: string;
  identity: string;
  items: T[];
}

export function groupAdjacentBy<T>(
  items: readonly T[],
  identityFor: (item: T) => string,
  keyPrefix = '',
): AdjacentGroup<T>[] {
  const groups: AdjacentGroup<T>[] = [];

  for (const item of items) {
    const identity = identityFor(item);
    const previous = groups.at(-1);
    if (previous?.identity === identity) {
      previous.items.push(item);
      continue;
    }

    groups.push({
      key: `${keyPrefix}${identity}-${groups.length}`,
      identity,
      items: [item],
    });
  }

  return groups;
}
