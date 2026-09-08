/**
 * The graph column beside a list of commits: a dot per commit, a line joining
 * them, and a merge drawn hollow with a stub arriving from the side.
 *
 * <p>The line means <em>order</em> — this is the sequence in which these commits
 * touched the thing you are looking at, newest first. In a branch's history that
 * is also ancestry, because the walk is first-parent; in a file's history it is
 * only order, since the list is filtered to one path and two adjacent rows may
 * have commits between them. Order is the claim both can support.</p>
 *
 * <p>Deliberately not lane assignment. The general graph is an arbitrary DAG and
 * neither list is one: both are a single walk, and the honest drawing of a single
 * walk is a single line. The merge stub is a stub rather than a second lane for
 * the same reason — the other parent's commits are not rows here, so a lane down
 * the list would lead nowhere.</p>
 */
export function CommitRail({ merge, first, last }: {
  merge: boolean;
  first: boolean;
  last: boolean;
}) {
  return (
    <svg width="20" height="100%" viewBox="0 0 20 40" preserveAspectRatio="none"
      className="shrink-0 self-stretch" aria-hidden="true">
      {!first && <line x1="10" y1="0" x2="10" y2="20" className="stroke-border" strokeWidth="2" />}
      {!last && <line x1="10" y1="20" x2="10" y2="40" className="stroke-border" strokeWidth="2" />}
      {merge && (
        <path d="M19 40 C 19 27, 10 31, 10 20" fill="none"
          className="stroke-border" strokeWidth="2" />
      )}
      {merge
        ? <circle cx="10" cy="20" r="4.5" className="fill-background stroke-primary" strokeWidth="2" />
        : <circle cx="10" cy="20" r="4" className="fill-primary" />}
    </svg>
  );
}
