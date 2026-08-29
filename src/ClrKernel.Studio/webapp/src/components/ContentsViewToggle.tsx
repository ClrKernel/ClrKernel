import { GalleryVertical, List } from 'lucide-react';
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import type { ContentsView } from '../prefs';

/**
 * Outline or thumbnails, in the Contents header.
 *
 * The same segmented control the toolbar's Normal|Focus switch uses, because it
 * is the same kind of thing — two readings of one panel, one of them live — and
 * a second idiom for the same idea in the same screen is one to learn for
 * nothing. Icons rather than words: the header has room for a title, this, and
 * the collapse button, and "Outline"/"Thumbnails" spelled out would push the
 * collapse control off a narrow sidebar.
 */
export function ContentsViewToggle({
  view,
  onView,
}: {
  view: ContentsView;
  onView: (view: ContentsView) => void;
}) {
  return (
    <ToggleGroup
      type="single"
      variant="outline"
      size="sm"
      value={view}
      // Radix reports '' when you click the item that is already on. Ignoring
      // that is what stops a second click leaving the panel with no view at all.
      onValueChange={(next) => next && onView(next as ContentsView)}
      aria-label="Contents view"
      className="h-6"
    >
      <Tooltip>
        <TooltipTrigger asChild>
          <ToggleGroupItem value="outline" className="h-6 px-1.5" aria-label="Outline">
            <List className="size-3.5" aria-hidden="true" />
          </ToggleGroupItem>
        </TooltipTrigger>
        <TooltipContent>Outline</TooltipContent>
      </Tooltip>
      <Tooltip>
        <TooltipTrigger asChild>
          <ToggleGroupItem value="thumbnails" className="h-6 px-1.5" aria-label="Thumbnails">
            <GalleryVertical className="size-3.5" aria-hidden="true" />
          </ToggleGroupItem>
        </TooltipTrigger>
        <TooltipContent>Thumbnails</TooltipContent>
      </Tooltip>
    </ToggleGroup>
  );
}
