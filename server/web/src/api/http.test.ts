import { afterEach, describe, expect, it, vi } from 'vitest';
import { problemToFormErrors } from '../lib/problem';
import { json, mockApi } from '../test-utils/fakes';
import { api, ApiError } from './http';

afterEach(() => vi.unstubAllGlobals());

describe('http wrapper', () => {
  it('sends X-X4MP on every verb', async () => {
    const calls = mockApi(() => new Response(null, { status: 204 }));
    await api.get('/api/v1/x');
    await api.post('/api/v1/x', { a: 1 });
    await api.put('/api/v1/x', {});
    await api.patch('/api/v1/x', {});
    await api.delete('/api/v1/x');
    expect(calls.map((c) => c.method)).toEqual(['GET', 'POST', 'PUT', 'PATCH', 'DELETE']);
    expect(calls.every((c) => c.csrf === '1')).toBe(true);
  });

  it('maps an ApiProblem 400 to per-field form errors', async () => {
    mockApi(() =>
      json(400, {
        type: 'x',
        status: 400,
        title: 'Validation failed',
        code: 'ValidationFailed',
        detail: 'One or more fields are invalid.',
        errors: { Name: ['Name is required.', 'Name is too short.'], maxPlayers: ['Must be 1-16.'] },
        errorCodes: { Name: 'Required' },
      }),
    );
    const err = await api.post('/api/v1/sessions', {}).catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe('ValidationFailed');
    expect((err as ApiError).errorCodes).toEqual({ Name: 'Required' });
    const fe = problemToFormErrors(err);
    expect(fe.fields).toEqual({ name: 'Name is required.', maxPlayers: 'Must be 1-16.' });
    expect(fe.all.name).toHaveLength(2);
    expect(fe.form).toBe('One or more fields are invalid.');
  });

  it('falls back to a form message for non-API failures', () => {
    expect(problemToFormErrors(new TypeError('Failed to fetch'))).toEqual({
      fields: {},
      all: {},
      form: 'Could not reach the server.',
    });
  });
});
