/**
 * Which kernel versions this build of the extension understands.
 *
 * The extension and the `clrkernel` tool ship on separate cadences and talk over a private
 * `clrkernel/*` JSON-RPC surface. That surface is changing in the next kernel release, so an
 * extension paired with a kernel it wasn't built for degrades quietly: cells still run, but the
 * connection UI silently fails. Everything needed to keep the two in step lives in this file —
 * when the pairing changes, this is the only thing to edit.
 */

/** NuGet floating range used when the extension installs the tool itself. */
export const SUPPORTED_KERNEL_RANGE = '0.8.*';

/** How to say it to a human. */
export const SUPPORTED_KERNEL_LABEL = '0.8.x';

const SUPPORTED_MAJOR = 0;
const SUPPORTED_MINOR = 8;

export type KernelCompatibility = 'ok' | 'newer' | 'older' | 'unknown';

/**
 * Compares the version the server reported at `initialize`.
 *
 * The server reports its **assembly** version, which has four parts ("0.8.0.0") even though the
 * package is three ("0.8.0"). Only major.minor is compared — patch releases of a kernel line are
 * expected to stay protocol-compatible.
 */
export function compareKernelVersion(reported: string | undefined): KernelCompatibility {
    if (!reported) {
        return 'unknown';
    }
    const parts = reported.split('.').map((p) => Number.parseInt(p, 10));
    if (parts.length < 2 || Number.isNaN(parts[0]) || Number.isNaN(parts[1])) {
        return 'unknown';
    }
    const [major, minor] = parts;
    if (major === SUPPORTED_MAJOR && minor === SUPPORTED_MINOR) {
        return 'ok';
    }
    return major > SUPPORTED_MAJOR || (major === SUPPORTED_MAJOR && minor > SUPPORTED_MINOR)
        ? 'newer'
        : 'older';
}

/**
 * The warning for an incompatible pairing, or undefined when there's nothing to say.
 *
 * Deliberately a warning and not a refusal: with a mismatched kernel, C# and SQL cells still
 * execute — `clrkernel/execute` is unchanged — and only connection management breaks. Blocking
 * the whole notebook would take away more than the mismatch does.
 */
export function kernelVersionWarning(
    compatibility: KernelCompatibility,
    reported: string | undefined,
): string | undefined {
    switch (compatibility) {
        case 'newer':
            return (
                `This ClrKernel extension supports kernel ${SUPPORTED_KERNEL_LABEL}, but ${reported} is installed. ` +
                'Cells will still run; the SQL and DAX connection buttons will not work until the extension is updated. ' +
                `To go back: dotnet tool update --global ClrKernel --version ${SUPPORTED_KERNEL_RANGE}`
            );
        case 'older':
            return (
                `This ClrKernel extension needs kernel ${SUPPORTED_KERNEL_LABEL}, but ${reported} is installed. ` +
                `Update it with: dotnet tool update --global ClrKernel --version ${SUPPORTED_KERNEL_RANGE}`
            );
        default:
            return undefined;
    }
}
