/**
 * Runs one asynchronous operation at a time, joining callers that arrive while it is in flight.
 *
 * Guards work that is expensive and must not be duplicated — starting the language server, in
 * particular. A plain `if (!started) { await start(); }` does not: the check and the assignment sit
 * either side of an `await`, so two callers can both pass the check and both start one. The second
 * result overwrites the first and the first process is leaked, still running, with nothing left
 * holding a reference to shut it down.
 *
 * The slot clears once the operation settles, so a failure can be retried and a later call after a
 * shutdown starts afresh.
 */
export class SingleFlight<T> {
    private inFlight: Promise<T> | undefined;

    run(factory: () => Promise<T>): Promise<T> {
        if (this.inFlight) {
            return this.inFlight;
        }
        const promise = factory().finally(() => {
            // Only clear our own slot: a retry may already have claimed it.
            if (this.inFlight === promise) {
                this.inFlight = undefined;
            }
        });
        this.inFlight = promise;
        return promise;
    }

    /** True while an operation is running. */
    get busy(): boolean {
        return this.inFlight !== undefined;
    }
}
