/**
 * Pure mapping from a kernel display-data bundle (mime type → value) to output
 * item descriptors. Binary content (the kernel's DisplayBytes concept — images,
 * pdfs, …) arrives as a base64 string and must become real bytes: feeding base64
 * text to the notebook renderer would show gibberish instead of the image.
 * Kept free of the vscode API so it is unit-testable.
 */

export interface OutputItemData {
    mime: string;
    /** Present for text mimes. */
    text?: string;
    /** Present for binary mimes (decoded from the kernel's base64). */
    bytes?: Uint8Array;
}

// image/svg+xml is XML text and renders as such; every other image (and pdf,
// audio, video, fonts, raw octet streams) is bytes on the wire.
const binaryMime = /^(image\/(?!svg)|audio\/|video\/|font\/|application\/(pdf|zip|octet-stream))/;

export function isBinaryMime(mime: string): boolean {
    return binaryMime.test(mime);
}

export function toOutputItemData(data: Record<string, unknown>): OutputItemData[] {
    const items: OutputItemData[] = [];
    for (const [mime, value] of Object.entries(data ?? {})) {
        if (isBinaryMime(mime) && typeof value === 'string') {
            items.push({ mime, bytes: new Uint8Array(Buffer.from(value, 'base64')) });
            continue;
        }
        items.push({ mime, text: typeof value === 'string' ? value : JSON.stringify(value) });
    }
    if (items.length === 0) {
        items.push({ mime: 'text/plain', text: '' });
    }
    return items;
}
