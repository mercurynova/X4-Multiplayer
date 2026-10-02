import type { APIRequestContext, Page } from '@playwright/test';
import type { LedgerTxDto, TradeOfferDto, WalletDto } from '../src/generated/generated';
import { expect, test } from './fixtures';

const IN_GAME = /in game/;
const fmt = (n: number) => n.toLocaleString('en-US');

async function wallets(api: APIRequestContext): Promise<WalletDto[]> {
  const res = await api.get('/api/v1/economy/wallets');
  expect(res.ok()).toBe(true);
  return (await res.json()) as WalletDto[];
}

async function wallet(api: APIRequestContext, name: string): Promise<WalletDto> {
  const w = (await wallets(api)).find((x) => x.ownerName === name);
  if (!w) throw new Error(`no wallet for ${name}`);
  return w;
}

async function trades(api: APIRequestContext): Promise<TradeOfferDto[]> {
  return (await (await api.get('/api/v1/economy/trades')).json()) as TradeOfferDto[];
}

/** One wallet per player and a short trade timeline, so trades reach InDoubt in seconds. */
async function configureEconomy(api: APIRequestContext) {
  const res = await api.patch('/api/v1/economy/policy', {
    data: { creditMode: 'PerPlayer', confirm: true, tradeRequiresProximity: false, tradeExecuteTimeoutSeconds: 1, tradeQueryIntervalSeconds: 1, reason: 'e2e' },
  });
  expect(res.status(), await res.text()).toBe(200);
}

const walletRow = (page: Page, name: string) => page.getByRole('row', { name: new RegExp(`^${name} `) });

async function reasonDialog(page: Page, title: string | RegExp, reason: string, confirm: string, fill?: (d: ReturnType<Page['getByRole']>) => Promise<void>) {
  const dialog = page.getByRole('dialog', { name: title });
  await fill?.(dialog);
  await dialog.getByRole('textbox', { name: 'Reason' }).fill(reason);
  await dialog.getByRole('button', { name: confirm, exact: true }).click();
  await expect(dialog).toBeHidden();
}

test.describe.configure({ mode: 'serial' });

test.describe('economy', () => {
  test.beforeEach(async ({ authority, api }) => {
    expect(authority.running).toBe(true);
    await configureEconomy(api);
  });

  test('GUI adjust then reversal restores the balance, live in the transactions feed', async ({ page, bots, api }) => {
    const bot = bots.start({ command: 'client', name: 'EconAdj', duration: 90 });
    await bot.waitForLine(IN_GAME);
    await expect.poll(async () => (await wallets(api)).some((w) => w.ownerName === 'EconAdj')).toBe(true);
    const before = (await wallet(api, 'EconAdj')).balance;

    await page.goto('/economy/wallets');
    await expect(walletRow(page, 'EconAdj')).toContainText(fmt(before));
    await walletRow(page, 'EconAdj').getByRole('button', { name: 'Adjust EconAdj' }).click();
    await reasonDialog(page, 'Adjust EconAdj', 'e2e grant', 'Adjust', (d) => d.getByRole('textbox', { name: /Amount/ }).fill('5000'));
    // the wallet row follows the WalletChanged push
    await expect(walletRow(page, 'EconAdj')).toContainText(fmt(before + 5000));
    expect((await wallet(api, 'EconAdj')).balance).toBe(before + 5000);

    await page.getByRole('link', { name: 'Transactions' }).click();
    const adjustRow = page.getByRole('row').filter({ hasText: 'AdminAdjust' }).filter({ hasText: 'EconAdj' }).first();
    await expect(adjustRow).toContainText('5,000');
    await adjustRow.getByRole('button', { name: /^Reverse / }).click();
    await reasonDialog(page, /^Reverse AdminAdjust/, 'e2e undo', 'Reverse');

    await expect(page.getByText(/↺ reversal/).first()).toBeVisible();
    await expect(adjustRow.getByText('reversed', { exact: true })).toBeVisible();
    expect((await wallet(api, 'EconAdj')).balance).toBe(before);
    // a second reversal is refused: the Reverse button is gone from the reversed row
    await expect(adjustRow.getByRole('button', { name: /^Reverse / })).toHaveCount(0);

    const txs = (await (await api.get('/api/v1/economy/transactions?kind=AdminAdjust&limit=5')).json()) as LedgerTxDto[];
    expect(txs.find((t) => t.reversedBy !== null)).toBeTruthy();
  });

  test('freezing a wallet shows the badge and counts it in the summary; unfreeze clears it', async ({ page, bots, api }) => {
    const bot = bots.start({ command: 'client', name: 'EconFrz', duration: 90 });
    await bot.waitForLine(IN_GAME);
    await expect.poll(async () => (await wallets(api)).some((w) => w.ownerName === 'EconFrz')).toBe(true);

    await page.goto('/economy/wallets');
    await walletRow(page, 'EconFrz').getByRole('button', { name: 'Freeze EconFrz' }).click();
    await reasonDialog(page, 'Freeze EconFrz', 'e2e freeze', 'Freeze');
    await expect(walletRow(page, 'EconFrz').getByText('FROZEN')).toBeVisible();

    // The FakeNode clients have no donate behaviour yet (M1-F4), so the effect on a player request is asserted at the API:
    // the wallet is frozen and counted, while admin postings (which ignore freezes) still work.
    expect((await wallet(api, 'EconFrz')).frozen).toBe(true);
    const summary = (await (await api.get('/api/v1/economy/summary')).json()) as { frozenWallets: number };
    expect(summary.frozenWallets).toBeGreaterThanOrEqual(1);

    await walletRow(page, 'EconFrz').getByRole('button', { name: 'Unfreeze EconFrz' }).click();
    await reasonDialog(page, 'Unfreeze EconFrz', 'e2e done', 'Unfreeze');
    await expect(walletRow(page, 'EconFrz').getByText('FROZEN')).toHaveCount(0);
    expect((await wallet(api, 'EconFrz')).frozen).toBe(false);
  });

  test('an InDoubt trade resolved as refund restores the payer', async ({ page, bots, api }) => {
    test.setTimeout(150_000);
    const swarm = bots.start({ command: 'swarm', clients: 3, namePrefix: 'EconTrd', args: ['--trade'], duration: 120 });
    await swarm.waitForLine(IN_GAME, 30_000);

    // The authority withholds every confirm and a third of those never answer a query: some trade ends InDoubt after ~4 s.
    let doubt: TradeOfferDto | undefined;
    await expect
      .poll(async () => (doubt = (await trades(api)).find((t) => t.state === 'InDoubt')), { timeout: 90_000, intervals: [1000] })
      .toBeTruthy();
    swarm.stop(); // no new trades while we compare balances
    const id = (doubt as TradeOfferDto).id;

    // Wait until nothing else is on its way to settle, so the only balance change left is ours.
    await expect
      .poll(async () => (await trades(api)).filter((t) => t.state === 'Transferring').length, { timeout: 30_000, intervals: [1000] })
      .toBe(0);
    const trade = (await trades(api)).find((t) => t.id === id) as TradeOfferDto;
    expect(trade.state).toBe('InDoubt');
    const payerId = trade.initiatorGives.some((i) => i.kind === 'Credits') ? trade.initiatorId : trade.counterpartyId;
    const payerName = payerId === trade.initiatorId ? trade.initiator : trade.counterparty;
    const escrow = trade.escrowAmount;
    expect(escrow).toBeGreaterThan(0);
    const before = (await wallet(api, payerName)).balance;

    await page.goto('/economy/trades');
    await expect(page.getByRole('link', { name: 'Trades' })).toBeVisible();
    const row = page.getByRole('row').filter({ has: page.getByRole('cell', { name: String(id), exact: true }) });
    await expect(row.getByText(/IN DOUBT/)).toBeVisible();
    await row.getByRole('button', { name: `Refund trade ${id}` }).click();
    await reasonDialog(page, `Resolve trade #${id}: refund`, 'authority never applied it', 'Refund');

    await expect(row.getByText(/IN DOUBT/)).toHaveCount(0);
    expect((await trades(api)).find((t) => t.id === id)?.state).toBe('RolledBack');
    expect((await wallet(api, payerName)).balance).toBe(before + escrow);
    // the refund is in the ledger, linked to the trade
    const refunds = (await (await api.get(`/api/v1/economy/transactions?kind=TradeRefund&limit=50`)).json()) as LedgerTxDto[];
    expect(refunds.some((t) => t.refId === id)).toBe(true);
  });
});
