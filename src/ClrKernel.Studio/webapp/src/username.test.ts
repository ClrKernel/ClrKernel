import { describe, expect, it } from 'vitest';
import { suggestUsername } from './auth';

/**
 * The same cases UserNameTest pins on the C# side. This is a suggestion, not a
 * validator — the server decides — but the two drifting apart would mean an
 * admin typing a name and being handed a handle the server then refuses, which
 * is exactly the friction the invite form exists to remove.
 */
describe('suggestUsername', () => {
  it('slugs a display name the way the server does', () => {
    expect(suggestUsername('Jeremy Adams')).toBe('jeremy-adams');
    expect(suggestUsername("O'Brien")).toBe('o-brien');
    expect(suggestUsername('  José  García  ')).toBe('jose-garcia');
  });

  it('returns something the server would accept, or nothing at all', () => {
    // Nothing usable in it: empty, and the form's own "required" catches that.
    // Better than offering a name and having the server refuse it.
    for (const input of ['', '   ', '!!!', '试验']) {
      expect(suggestUsername(input)).toBe('');
    }
  });

  it('stays inside the length limit and never ends on punctuation', () => {
    const long = suggestUsername('x'.repeat(200));
    expect(long.length).toBeLessThanOrEqual(39);
    expect(long).toMatch(/^[a-z0-9][a-z0-9._-]*$/);
    expect(suggestUsername('Ada '.repeat(20))).not.toMatch(/[-.]$/);
  });
});
