import * as path from 'path';
import { defineConfig } from 'vitest/config';

export default defineConfig({
    resolve: {
        // Extension code imports 'vscode', which only exists inside the editor. The stub supplies
        // the data types; anything interactive throws, so a test can't drift into pretending to
        // drive a UI.
        alias: { vscode: path.resolve(__dirname, 'test/vscode-stub.ts') },
    },
    test: {
        include: ['test/**/*.test.ts'],
        environment: 'node',
    },
});
