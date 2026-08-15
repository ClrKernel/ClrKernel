import { describe, expect, it } from 'vitest';
import { SingleFlight } from '../src/singleFlight';

/**
 * The bug this exists to stop: two callers both find no server running and both start one, because
 * the check and the assignment sit either side of an await. The loser's process keeps running with
 * nothing holding a reference to shut it down.
 *
 * Reachable without anything exotic — restoring a window opens every notebook at once, and a cell
 * run can race the connection button.
 */
describe('SingleFlight', () => {
    const deferred = <T>() => {
        let resolve!: (value: T) => void;
        let reject!: (reason: unknown) => void;
        const promise = new Promise<T>((res, rej) => { resolve = res; reject = rej; });
        return { promise, resolve, reject };
    };

    it('runs the operation once for callers that arrive together', async () => {
        const flight = new SingleFlight<string>();
        const gate = deferred<string>();
        let started = 0;
        const factory = () => { started++; return gate.promise; };

        const callers = [flight.run(factory), flight.run(factory), flight.run(factory)];
        expect(started).toBe(1);

        gate.resolve('server');
        expect(await Promise.all(callers)).toEqual(['server', 'server', 'server']);
        expect(started).toBe(1);
    });

    it('gives every joiner the same result', async () => {
        const flight = new SingleFlight<{ id: number }>();
        const gate = deferred<{ id: number }>();
        const a = flight.run(() => gate.promise);
        const b = flight.run(() => gate.promise);
        gate.resolve({ id: 1 });
        expect(await a).toBe(await b); // identity: one server, not two equal ones
    });

    it('starts again once the first attempt has finished', async () => {
        const flight = new SingleFlight<number>();
        let started = 0;
        const factory = () => { started++; return Promise.resolve(started); };

        expect(await flight.run(factory)).toBe(1);
        expect(await flight.run(factory)).toBe(2); // a later call after a shutdown must start afresh
    });

    it('rejects every joiner when the operation fails', async () => {
        const flight = new SingleFlight<string>();
        const gate = deferred<string>();
        const a = flight.run(() => gate.promise);
        const b = flight.run(() => gate.promise);

        gate.reject(new Error('tool not installed'));
        await expect(a).rejects.toThrow('tool not installed');
        await expect(b).rejects.toThrow('tool not installed');
    });

    it('clears after a failure so the next attempt retries', async () => {
        const flight = new SingleFlight<string>();
        let attempts = 0;
        const factory = () => {
            attempts++;
            return attempts === 1 ? Promise.reject(new Error('boom')) : Promise.resolve('ok');
        };

        await expect(flight.run(factory)).rejects.toThrow('boom');
        expect(await flight.run(factory)).toBe('ok'); // a failed start must not wedge it forever
        expect(attempts).toBe(2);
    });

    it('reports whether an operation is in flight', async () => {
        const flight = new SingleFlight<string>();
        const gate = deferred<string>();
        expect(flight.busy).toBe(false);
        const running = flight.run(() => gate.promise);
        expect(flight.busy).toBe(true);
        gate.resolve('done');
        await running;
        expect(flight.busy).toBe(false);
    });
});
