import { describe, expect, it } from 'vitest';
import { isBinaryMime, toOutputItemData } from '../src/outputItems';

describe('isBinaryMime', () => {
    it('treats raster images, pdf, audio/video and octet streams as binary', () => {
        expect(isBinaryMime('image/png')).toBe(true);
        expect(isBinaryMime('image/jpeg')).toBe(true);
        expect(isBinaryMime('application/pdf')).toBe(true);
        expect(isBinaryMime('application/octet-stream')).toBe(true);
        expect(isBinaryMime('audio/wav')).toBe(true);
    });

    it('keeps text formats as text', () => {
        expect(isBinaryMime('text/plain')).toBe(false);
        expect(isBinaryMime('text/html')).toBe(false);
        expect(isBinaryMime('image/svg+xml')).toBe(false);
        expect(isBinaryMime('application/json')).toBe(false);
    });
});

describe('toOutputItemData', () => {
    it('decodes base64 for binary mimes', () => {
        const bytes = Buffer.from([137, 80, 78, 71]); // PNG magic
        const [item] = toOutputItemData({ 'image/png': bytes.toString('base64') });
        expect(item.mime).toBe('image/png');
        expect(item.text).toBeUndefined();
        expect(Array.from(item.bytes!)).toEqual([137, 80, 78, 71]);
    });

    it('passes text mimes through unchanged', () => {
        const [item] = toOutputItemData({ 'text/html': '<b>x</b>' });
        expect(item.text).toBe('<b>x</b>');
        expect(item.bytes).toBeUndefined();
    });

    it('stringifies non-string values', () => {
        const [item] = toOutputItemData({ 'application/json': { a: 1 } });
        expect(item.text).toBe('{"a":1}');
    });

    it('falls back to one empty text/plain item for an empty bundle', () => {
        const items = toOutputItemData({});
        expect(items).toEqual([{ mime: 'text/plain', text: '' }]);
    });
});
