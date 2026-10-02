import { e2e } from './env';
import { expect, test } from './fixtures';

// No stored sign-in: the visitor is anonymous.
test.use({ storageState: { cookies: [], origins: [] } });

test('unauthenticated visitors land on the login page', async ({ page }) => {
  await page.goto('/players');
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible();
});

test('the admin can sign in with the password set by the global setup', async ({ page }) => {
  await page.goto('/');
  await page.getByLabel('Username').fill(e2e.adminUser);
  await page.getByLabel('Password').fill(e2e.adminPassword);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('navigation', { name: 'Main' })).toBeVisible();
});
