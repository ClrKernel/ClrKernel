import type { ApiCommit } from '../api';

/**
 * Two lanes and where they parted — test above, your branch below, joined at the
 * commit they share.
 *
 * <p>Hand-drawn SVG rather than a graph library. The general case (an arbitrary
 * DAG, lanes assigned by walking parents) is a real piece of work and a real
 * dependency; this picture answers one question — <em>what is on each side of
 * the merge I am about to do</em> — and that question has exactly two lanes.</p>
 *
 * <p>Oldest on the left, so the arrow of time runs the way it reads. The commit
 * lists below are newest-first, which is the opposite, and deliberately: a list
 * is scanned from the top for "what just happened", a graph is read left to right
 * for "how did we get here".</p>
 */
export function BranchGraph({
  incoming,
  outgoing,
  branch,
  merged,
}: {
  /** Commits on test that your branch has not got, newest first. */
  incoming: ApiCommit[];
  /** Commits on your branch that test has not got, newest first. */
  outgoing: ApiCommit[];
  branch: string;
  /** Draws the merge that is about to happen, rather than only the two lanes. */
  merged: boolean;
}) {
  // Left to right, so reverse the newest-first lists.
  const top = [...incoming].reverse();
  const bottom = [...outgoing].reverse();

  const step = 34;
  const r = 5;
  const baseX = 26;
  const topY = 26;
  const bottomY = 74;
  const width = baseX + (Math.max(top.length, bottom.length) + (merged ? 2 : 1)) * step + 90;
  const height = 100;

  const topX = (i: number) => baseX + (i + 1) * step;
  const bottomX = (i: number) => baseX + (i + 1) * step;
  const mergeX = baseX + (Math.max(top.length, bottom.length) + 1) * step;

  return (
    <svg
      viewBox={`0 0 ${width} ${height}`}
      className="h-[100px] w-full max-w-full"
      role="img"
      aria-label={
        `${incoming.length} commit(s) on test and ${outgoing.length} on ${branch} `
        + 'since the branches parted'
      }
    >
      {/* The shared commit both lanes grow out of. */}
      <line x1={baseX} y1={topY} x2={topX(top.length - 1)} y2={topY}
        className="stroke-status-warning" strokeWidth="2" />
      <line x1={baseX} y1={bottomY} x2={bottomX(bottom.length - 1)} y2={bottomY}
        className="stroke-primary" strokeWidth="2" />
      <line x1={baseX} y1={topY} x2={baseX} y2={bottomY}
        className="stroke-border" strokeWidth="2" />

      {top.map((commit, i) => (
        <g key={commit.sha}>
          {i > 0 && (
            <line x1={topX(i - 1)} y1={topY} x2={topX(i)} y2={topY}
              className="stroke-status-warning" strokeWidth="2" />
          )}
          <circle cx={topX(i)} cy={topY} r={r} className="fill-status-warning" />
        </g>
      ))}
      {bottom.map((commit, i) => (
        <g key={commit.sha}>
          {i > 0 && (
            <line x1={bottomX(i - 1)} y1={bottomY} x2={bottomX(i)} y2={bottomY}
              className="stroke-primary" strokeWidth="2" />
          )}
          <circle cx={bottomX(i)} cy={bottomY} r={r} className="fill-primary" />
        </g>
      ))}

      {/* Where they parted. */}
      <circle cx={baseX} cy={topY} r={r} className="fill-muted-foreground" />
      <circle cx={baseX} cy={bottomY} r={r} className="fill-muted-foreground" />

      {merged && (
        <>
          {/* test's tip curves down into your branch — the direction the merge
              actually goes, which is the thing people get backwards. */}
          <path
            d={`M ${topX(top.length - 1)} ${topY} C ${mergeX - 10} ${topY}, `
              + `${mergeX - 10} ${bottomY}, ${mergeX} ${bottomY}`}
            className="stroke-status-warning"
            strokeWidth="2"
            fill="none"
            strokeDasharray="4 3"
          />
          <line
            x1={bottom.length > 0 ? bottomX(bottom.length - 1) : baseX} y1={bottomY}
            x2={mergeX} y2={bottomY}
            className="stroke-primary" strokeWidth="2" strokeDasharray="4 3" />
          <circle cx={mergeX} cy={bottomY} r={r + 1}
            className="fill-primary stroke-background" strokeWidth="2" />
        </>
      )}

      <text x={width - 84} y={topY + 4} className="fill-status-warning text-[11px] font-semibold">
        test
      </text>
      <text x={width - 84} y={bottomY + 4} className="fill-primary text-[11px] font-semibold">
        {branch.startsWith('user/') ? 'your branch' : branch}
      </text>
    </svg>
  );
}
