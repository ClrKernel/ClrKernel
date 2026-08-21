import { describe, expect, it } from 'vitest';
import {
    SUPPORTED_KERNEL_LABEL,
    SUPPORTED_KERNEL_MIN,
    SUPPORTED_KERNEL_RANGE,
    compareKernelVersion,
    kernelVersionWarning,
} from '../src/kernelVersion';

/**
 * The extension and the kernel ship separately and talk over a private RPC surface, so this is what
 * stops a mismatched pair failing in confusing ways.
 */
describe('compareKernelVersion', () => {
    it('accepts the supported line, however many parts the version has', () => {
        // The server reports an assembly version (four parts) though the package has three.
        expect(compareKernelVersion('0.10.0.0')).toBe('ok');
        expect(compareKernelVersion('0.10.0')).toBe('ok');
    });

    it('treats a patch release of the same line as compatible', () => {
        expect(compareKernelVersion('0.10.7.0')).toBe('ok');
    });

    it('flags a newer kernel — the case this guard exists for', () => {
        expect(compareKernelVersion('0.11.0.0')).toBe('newer');
        expect(compareKernelVersion('1.0.0.0')).toBe('newer');
    });

    it('flags an older kernel', () => {
        expect(compareKernelVersion('0.8.0.0')).toBe('older');
        expect(compareKernelVersion('0.7.9.0')).toBe('older');
    });

    it('flags a same-line kernel below the minimum patch — 0.9.0 lacks RPCs this build calls', () => {
        expect(compareKernelVersion('0.9.0.0')).toBe('older');
        expect(compareKernelVersion('0.9.0')).toBe('older');
    });

    it('says nothing useful rather than guessing when the version is missing or junk', () => {
        // 'unknown' produces no warning: a false alarm is worse than silence here.
        expect(compareKernelVersion(undefined)).toBe('unknown');
        expect(compareKernelVersion('')).toBe('unknown');
        expect(compareKernelVersion('not-a-version')).toBe('unknown');
        expect(compareKernelVersion('9')).toBe('unknown');
    });
});

describe('kernelVersionWarning', () => {
    it('tells the user what is installed and how to get back', () => {
        const warning = kernelVersionWarning('newer', '0.10.0.0');
        expect(warning).toContain('0.10.0.0');
        expect(warning).toContain(SUPPORTED_KERNEL_LABEL);
        expect(warning).toContain(SUPPORTED_KERNEL_RANGE);
    });

    it('says cells still run, because they do — only connection management breaks', () => {
        expect(kernelVersionWarning('newer', '0.10.0.0')).toContain('still run');
    });

    it('asks for an update when the kernel is behind', () => {
        expect(kernelVersionWarning('older', '0.7.0.0')).toContain('dotnet tool update');
    });

    it('stays quiet when the pair is fine or unknown', () => {
        expect(kernelVersionWarning('ok', '0.9.1.0')).toBeUndefined();
        expect(kernelVersionWarning('unknown', undefined)).toBeUndefined();
    });
});

describe('the pin and the check agree', () => {
    it('the minimum kernel is one the extension accepts', () => {
        // If these drift, the extension installs a kernel it then warns about.
        expect(compareKernelVersion(SUPPORTED_KERNEL_MIN)).toBe('ok');
    });

    it('the minimum lives inside the install range and the label', () => {
        // The floating range installs the newest patch of the line, which is >= the minimum.
        const [major, minor] = SUPPORTED_KERNEL_MIN.split('.');
        expect(SUPPORTED_KERNEL_RANGE).toBe(`${major}.${minor}.*`);
        expect(SUPPORTED_KERNEL_LABEL).toBe(`${major}.${minor}.x`);
    });
});
