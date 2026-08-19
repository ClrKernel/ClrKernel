import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

/**
 * The dev notebook editor: every save is a commit on the dev branch, the diff tab
 * shows what promotion would ship, and Promote applies it once every job on the
 * notebook has clean green evidence.
 */
export function Editor() {
  const [search] = useSearchParams();
  const path = search.get('path') ?? '';

  const [content, setContent] = useState<string | null>(null);
  const [savedContent, setSavedContent] = useState<string | null>(null);
  const [diff, setDiff] = useState('');
  const [tab, setTab] = useState<'edit' | 'diff'>('edit');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const { data: promotion, reload: reloadPromotion } = usePolling(
    () => api.promotionStatus(path),
    null,
    [path],
  );
  const dirty = content != null && content !== savedContent;

  useEffect(() => {
    api
      .notebookContent('dev', path)
      .then((text) => {
        setContent(text);
        setSavedContent(text);
      })
      .catch(() => setError(`Could not load ${path}.`));
  }, [path]);

  async function save() {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const result = await api.saveNotebookContent(path, content ?? '');
      setSavedContent(content);
      setNotice(`Saved and committed (${result.commitSha.slice(0, 8)}).`);
      reloadPromotion();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function showDiff() {
    setDiff(await api.gitDiff(path));
    setTab('diff');
  }

  async function promote() {
    if (!confirm(`Promote ${path} to production?`)) {
      return;
    }
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const result = await api.promote(path);
      setNotice(`Promoted to production (${result.commitSha.slice(0, 8)}). The prod scheduler picks it up on its next tick.`);
      reloadPromotion();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="row-between">
        <h1>
          <code>{path}</code> <span className="chip env-dev">dev</span>
        </h1>
        <div className="row-gap">
          <button className="button button-primary" onClick={save} disabled={busy || !dirty}>
            {dirty ? 'Save (commits to dev)' : 'Saved'}
          </button>
          <button
            className="button"
            onClick={promote}
            disabled={busy || !promotion?.eligible}
            title={promotion?.eligible ? 'Ship to production' : promotion?.reasons.join('\n')}
          >
            {promotion?.isDeletion ? 'Promote deletion' : 'Promote to production'}
          </button>
        </div>
      </div>
      <ErrorBanner error={error} />
      {notice && <div className="banner banner-ok">{notice}</div>}

      {promotion && !promotion.eligible && (
        <div className="banner banner-warn">
          <strong>Not promotable yet</strong>
          <ul>
            {promotion.reasons.map((reason) => (
              <li key={reason}>{reason}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="tabs">
        <button className={tab === 'edit' ? 'active' : ''} onClick={() => setTab('edit')}>
          Edit
        </button>
        <button className={tab === 'diff' ? 'active' : ''} onClick={showDiff}>
          Diff vs production
        </button>
      </div>

      {tab === 'edit' &&
        (content == null ? (
          <p className="muted">Loading…</p>
        ) : (
          <textarea
            className="editor"
            value={content}
            spellCheck={false}
            onChange={(e) => setContent(e.target.value)}
          />
        ))}
      {tab === 'diff' &&
        (diff ? (
          <pre className="output-text log">{diff}</pre>
        ) : (
          <p className="muted">No differences — dev and production are identical for this file.</p>
        ))}

      <p className="muted">
        Every save commits to the dev branch. Run the notebook's jobs from the{' '}
        <Link to="/jobs">Jobs</Link> page; promotion unlocks when every job on this notebook has a
        clean green run of exactly this content.
      </p>
    </div>
  );
}
