import { MAX_ZOOM, MIN_ZOOM } from '../thumbnail';

/**
 * How far out the thumbnails are zoomed.
 *
 * A native `<input type="range">`, styled with the token layer. The kit has no
 * slider, and this is the one control the platform already ships complete —
 * keyboard steps, Home and End, the right ARIA role, and a drag that behaves the
 * way every other slider on the machine does. Adding a component to reimplement
 * that would be adding a component to make it worse.
 *
 * Its own row rather than a fourth item in the CONTENTS header: at the 220px the
 * thumbnail view enforces, a title, two toggles, a collapse button and a slider
 * do not fit, and the one that would be squeezed out is the collapse button.
 * Shown only in this view, so the outline pays nothing for it.
 */
export function ThumbnailZoom({
  zoom,
  onZoom,
}: {
  zoom: number;
  onZoom: (zoom: number) => void;
}) {
  return (
    <div className="focus-thumbs-zoom">
      <label htmlFor="thumbnail-zoom">Size</label>
      <input
        id="thumbnail-zoom"
        type="range"
        min={MIN_ZOOM}
        max={MAX_ZOOM}
        // Coarse enough that dragging it feels like picking a size rather than
        // tuning one, and every step is a visible difference.
        step={0.05}
        value={zoom}
        onChange={(event) => onZoom(Number(event.target.value))}
        aria-label="Thumbnail size"
        // The percentage, because "0.75" is not what anybody is choosing.
        aria-valuetext={`${Math.round(zoom * 100)}%`}
      />
      <span className="focus-thumbs-zoom-value">{Math.round(zoom * 100)}%</span>
    </div>
  );
}
