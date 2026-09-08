import { FilePlus2, FolderGit2 } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { toast } from 'sonner';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { api, projectSlug } from '../api';
import { ErrorBanner, usePolling } from '../components/common';
import { MergePreview } from '../components/MergePreview';
import { NotebookExplorer } from '../components/NotebookExplorer';
import { RepoBrowser } from '../components/RepoBrowser';
import { Splitter } from '../components/Splitter';
import { createNotebook, promptForNotebook } from '../newNotebook';
import {
  DEFAULT_LAYOUT, loadBranch, loadLayout, MAX_EXPLORER, MIN_EXPLORER, saveLayout,
  type LayoutPrefs,
} from '../prefs';
import { editPath } from '../routes';
import { useIsProjectAdmin, useIsProjectMember } from '../sessionContext';

function clamp(value: number, low: number, high: number): number {
  return Math.min(Math.max(value, low), high);
}

/**
 * The Files area with nothing open: the editor's explorer, and the pane it fills
 * left empty.
 *
 * <p>It used to be a card of its own — a different tree, a different branch
 * picker, different indentation — so opening a file rearranged the page around
 * you and the thing you had just been reading moved. Same sidebar, same width,
 * same rows: opening a file now only fills the space to the right of it.</p>
 *
 * <p>The layout is read and written through the same <c>loadLayout</c> the editor
 * uses, which is what makes the width survive the crossing in both directions.</p>
 */
export function Files() {
  const navigate = useNavigate();
  const mayEdit = useIsProjectMember();
  const isProjectAdmin = useIsProjectAdmin();
  const { data: tree, error, reload } = usePolling(() => api.notebooks(), null);
  // Reloaded by hand after setting up the workflow: it is what decides whether
  // the "no workflow" notice is still on screen.
  const { data: health, reload: reloadHealth } = usePolling(() => api.health(), null);
  // The explorer draws the "test has moved on" indicator, and it is the same
  // explorer here as in the editor — so it needs the same standing. Without this
  // the indicator existed only once a file was open, which is the wrong half:
  // this is the page a branch switch now lands on.
  const { data: standing, reload: reloadStanding } = usePolling(
    () => api.branchStanding(), 15000);
  const [notice, setNotice] = useState<string | null>(null);
  const [setting, setSetting] = useState(false);
  const [layout, setLayout] = useState<LayoutPrefs>(() => loadLayout());
  const [previewMerge, setPreviewMerge] = useState(false);
  useEffect(() => saveLayout(layout), [layout]);
  // The explorer's drag reports a viewport X; the sidebar's width is that minus
  // wherever this row actually starts, which is not the window's edge.
  const shell = useRef<HTMLDivElement>(null);

  // The branch you were last on in this project. The explorer keeps its own
  // selection and falls back to the first branch when this names one that no
  // longer exists — the same guard the card had, in one place now.
  const branch = loadBranch(projectSlug()) ?? 'mine';

  /**
   * Turns this project's folder into a test/prod workspace — what
   * `clrkernel-studio git init` does, from the page that told you to go and do it.
   * Idempotent, and it adopts whatever is already in the folder.
   */
  async function setUpGit() {
    setSetting(true);
    setNotice(null);
    try {
      const result = await api.initProject(projectSlug());
      // A toast, not the notice line: that renders through ErrorBanner, so this
      // said "initialized; adopted 3 existing item(s)" in red with an error icon
      // — a success that reads as a failure is worse than no message.
      toast.success(result.message);
      reload();
      reloadHealth();
    } catch (e) {
      setNotice((e as Error).message);
    } finally {
      setSetting(false);
    }
  }

  /** Makes it on your branch and opens it there. */
  async function create() {
    const wanted = promptForNotebook();
    if (wanted == null) {
      return;
    }
    setNotice(null);
    try {
      await createNotebook(wanted);
      reload();
      navigate(editPath(projectSlug(), 'mine', wanted));
    } catch (e) {
      setNotice((e as Error).message);
    }
  }

  const environments = (tree?.environments ?? []).filter((e) => e.tree != null);
  const mayWrite = (health?.gitEnabled ?? false) && mayEdit;

  return (
    // The same three parts as the editor, in the same order: explorer, gutter,
    // and a pane that fills the rest. Only the third one differs.
    <div className="flex min-h-0 flex-1 overflow-hidden" ref={shell}>
      <NotebookExplorer
        path={null}
        branch={branch}
        width={layout.explorerWidth}
        collapsed={layout.explorerCollapsed}
        onCollapse={(explorerCollapsed) => setLayout({ ...layout, explorerCollapsed })}
        standing={standing}
        onUpdate={() => setPreviewMerge(true)}
      />
      {!layout.explorerCollapsed && (
        <Splitter
          orientation="vertical"
          label="Explorer width"
          onDrag={(clientX) =>
            setLayout({
              ...layout,
              explorerWidth: clamp(
                clientX - (shell.current?.getBoundingClientRect().left ?? 0),
                MIN_EXPLORER,
                MAX_EXPLORER,
              ),
            })
          }
          onReset={() => setLayout({ ...layout, explorerWidth: DEFAULT_LAYOUT.explorerWidth })}
        />
      )}

      <div className="flex min-w-0 flex-1 flex-col overflow-auto px-7 py-5">
        <ErrorBanner error={error} />
        <ErrorBanner error={notice} />

        {health && !health.gitEnabled && (
          <Alert variant="warning" className="mb-4 max-w-[640px]">
            <AlertTitle>Editing needs the git workflow</AlertTitle>
            <AlertDescription>
              <p>
                Editing notebooks in the browser needs the test→prod git workflow, so every save is
                a commit. It is set up once, and this folder keeps everything already in it.
              </p>
              {isProjectAdmin ? (
                <>
                  <Button className="my-2" size="sm" disabled={setting} onClick={setUpGit}>
                    <FolderGit2 className="size-3.5" aria-hidden="true" />
                    {setting ? 'Setting up…' : 'Set up the git workflow'}
                  </Button>
                  <p>
                    Your notebooks move into <code className="font-mono text-code">test/</code> and{' '}
                    <code className="font-mono text-code">prod/</code> worktrees. Test notebooks
                    then get an <strong>Edit</strong> button, and changes promote to production
                    after a green run.
                  </p>
                </>
              ) : (
                <p className="mt-2">
                  Ask an admin of this project to set it up — it is one button on this page for
                  them, or <code className="font-mono text-code">clrkernel-studio git init</code> on
                  the server.
                </p>
              )}
            </AlertDescription>
          </Alert>
        )}

        {tree != null && environments.length === 0 ? (
          <p className="text-base text-muted-foreground">No files under the notebooks root.</p>
        ) : (
          <>
            <div className="mb-3 flex items-center gap-2">
              <span className="flex-1" />
              {mayWrite && (
                <Button variant="outline" size="sm" onClick={create}>
                  <FilePlus2 className="size-3.5" aria-hidden="true" />
                  New notebook
                </Button>
              )}
            </div>
            {/* The repo, rather than an empty pane with a button in the middle of
                it: opening Files used to say nothing at all about the project you
                had just opened. */}
            <RepoBrowser branch={branch} />
          </>
        )}
      </div>

      {previewMerge && (
        <MergePreview
          onClose={() => setPreviewMerge(false)}
          onMerged={(message) => {
            toast.success(message);
            reload();
            reloadStanding();
          }}
        />
      )}
    </div>
  );
}
