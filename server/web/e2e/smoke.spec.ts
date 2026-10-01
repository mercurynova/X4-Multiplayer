import { test, expect } from '@playwright/test';

test('unauthenticated visitors land on the login page', async ({ page }) => {
  await page.goto('/players');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});
