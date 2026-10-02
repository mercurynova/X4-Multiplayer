import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { AppProviders } from '../../AppProviders';
import { AppRoutes } from '../../AppRoutes';
import type {
  EconomyPolicyDto,
  EconomySummaryDto,
  LedgerTxDto,
  LoanDto,
  TradeOfferDto,
  WalletDto,
} from '../../generated/generated';
import { CaptureHub } from '../../test-utils/captureHub';
import { json, mockApi, type Call } from '../../test-utils/fakes';

const wallet = (kind: string, ownerId: number, ownerName: string, balance: number, extra: Partial<WalletDto> = {}): WalletDto => ({
  kind, ownerId, ownerName, balance, frozen: false, frozenReason: null, overdrawn: false, version: 1, ...extra,
});

const tx = (id: string, kind: string, from: [number, string, number], to: [number, string, number], extra: Partial<LedgerTxDto> = {}): LedgerTxDto => ({
  id, at: '2026-10-02T21:04:55Z', kind, actor: 'player:1', requestId: null, refType: null, refId: null, reverses: null, reversedBy: null, note: null,
  entries: [
    { walletKind: 'Player', walletOwnerId: from[0], walletName: from[1], amount: -from[2], balanceAfter: 0 },
    { walletKind: 'Player', walletOwnerId: to[0], walletName: to[1], amount: to[2], balanceAfter: 0 },
  ],
  ...extra,
});

const summary = (extra: Partial<EconomySummaryDto> = {}): EconomySummaryDto => ({
  creditMode: 'Auto', effectiveCreditMode: 'PerPlayer', appliedCreditMode: 'PerPlayer', migrationPending: false, moneySupply: 8_410_000, inEscrow: 350_000,
  openLoans: 2, overdueLoans: 1, outstandingDebt: 190_000, openTrades: 1, inDoubtTrades: 1, frozenWallets: 0, economyFrozen: false, freezeReason: null,
  lastAudit: { at: '2026-10-02T21:05:00Z', ok: true, economyFrozen: false, violations: [] }, ...extra,
});

const loan = (id: number, extra: Partial<LoanDto> = {}): LoanDto => ({
  id, lenderId: 1, lender: 'Bob', borrowerId: 2, borrower: 'Dan', principal: 100_000, interestBp: 500, repayTotal: 105_000, outstanding: 100_000, repaid: 0,
  forgiven: 0, autoRepayPercent: 0, dueInSeconds: 3600, dueAt: '2026-10-02T22:00:00Z', state: 'Active', overdue: false, createdAt: '2026-10-02T20:00:00Z',
  offerExpiresAt: '2026-10-02T20:30:00Z', acceptedAt: '2026-10-02T20:05:00Z', closedAt: null, closeReason: null, memo: null, ...extra,
});

const trade = (id: number, extra: Partial<TradeOfferDto> = {}): TradeOfferDto => ({
  id, initiatorId: 3, initiator: 'Eve', counterpartyId: 4, counterparty: 'Carol',
  initiatorGives: [{ kind: 'Ship', amount: 1, wareRef: 0, asset: 77 }], counterpartyGives: [{ kind: 'Credits', amount: 6000, wareRef: 0, asset: 0 }],
  state: 'InDoubt', version: 3, expiresAt: '2026-10-02T22:00:00Z', queryAttempts: 3, escrowAmount: 6000, reason: null, detail: null, resolvedBy: null,
  reversed: false, createdAt: '2026-10-02T20:00:00Z', updatedAt: '2026-10-02T20:00:00Z', memo: null, ...extra,
});

const policy: EconomyPolicyDto = {
  creditMode: 'Auto', effectiveCreditMode: 'PerPlayer', startingCredits: 100_000, teamPoolEnabled: true, poolWithdrawPolicy: 'AnyMember',
  poolWithdrawDailyLimitPerPlayer: 0, sharedWalletSpend: 'AnyMember', donateScope: 'Teammates', allowAlliedTransfers: false, loanScope: 'Teammates',
  tradeScope: 'Allied', tradeShipsEnabled: true, maxOpenTradesPerPlayer: 5, tradeRequiresProximity: true, tradeExecuteTimeoutSeconds: 30,
  tradeQueryIntervalSeconds: 10, maxOpenLoansPerPlayer: 5, maxLoanPrincipal: 1_000_000, maxLoanInterestBp: 5000, offerDefaultTtlMinutes: 30,
  maxSingleTransfer: 1_000_000, auditIntervalSeconds: 60,
};

const problem = (status: number, code: string, detail: string, extra: object = {}) => json(status, { title: code, status, code, detail, ...extra });

interface World {
  wallets?: WalletDto[];
  txs?: LedgerTxDto[];
  loans?: LoanDto[];
  trades?: TradeOfferDto[];
  summary?: EconomySummaryDto;
  /** Extra routes tried first; return undefined to fall through. */
  route?: (c: Call) => Response | undefined;
}

let calls: Call[];

function setup(path: string, world: World = {}, role = 'Admin') {
  calls = mockApi((c) => {
    const custom = world.route?.(c);
    if (custom) return custom;
    const u = c.url.replace('/api/v1/economy', '');
    if (c.method === 'GET') {
      if (u === '/summary') return json(200, world.summary ?? summary());
      if (u === '/policy') return json(200, policy);
      if (u === '/wallets') return json(200, world.wallets ?? []);
      if (u.startsWith('/transactions?')) return json(200, world.txs ?? []);
      if (u === '/loans') return json(200, world.loans ?? []);
      if (u === '/trades') return json(200, world.trades ?? []);
      if (u.startsWith('/wallets/')) return json(200, { wallet: world.wallets?.[0], recent: world.txs ?? [], openLoans: [], openTrades: [] });
    }
    return problem(404, 'NotFound', 'unscripted ' + c.method + ' ' + c.url);
  });
  const hub = new CaptureHub();
  render(
    <AppProviders initialMe={{ username: 'admin', role, mustChangePassword: false }} hub={hub}>
      <MemoryRouter initialEntries={[path]}>
        <AppRoutes />
      </MemoryRouter>
    </AppProviders>,
  );
  return hub;
}

const posts = () => calls.filter((c) => c.method !== 'GET' && !c.url.endsWith('/server'));

afterEach(() => vi.unstubAllGlobals());

describe('overview and live data', () => {
  it('shows the summary tiles, subscribes to the economy topic and follows EconomySummary pushes', async () => {
    const hub = setup('/economy');
    expect(await screen.findByText('Money supply')).toBeInTheDocument();
    expect(screen.getByText('8,410,000 Cr')).toBeInTheDocument();
    expect(hub.keys).toContain('economy');
    expect(screen.getByText('Auto → PER PLAYER', { selector: '.tile-value' })).toBeInTheDocument();
    expect(screen.getByText(/All balances reconcile/)).toBeInTheDocument();
    act(() => hub.push('EconomySummary', summary({ moneySupply: 9_000_000 })));
    expect(await screen.findByText('9,000,000 Cr')).toBeInTheDocument();
    // tab badges count overdue loans and in-doubt trades
    expect(screen.getByLabelText('1 need attention', { selector: 'a[href="/economy/loans"] span' })).toBeInTheDocument();
  });

  it('shows a red banner and the acknowledge button when the auditor froze the economy', async () => {
    setup('/economy', {
      summary: summary({ economyFrozen: true, freezeReason: 'conservation breach', lastAudit: { at: '2026-10-02T21:05:00Z', ok: false, economyFrozen: true, violations: ['sum != 0'] } }),
    });
    expect(await screen.findByText(/Economy frozen by the auditor/)).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Acknowledge and unfreeze' }).length).toBeGreaterThan(0);
  });

  it('says so when there is no session yet', async () => {
    setup('/economy/wallets', { route: (c) => (c.url.endsWith('/wallets') ? problem(409, 'NoSession', 'No session') : undefined) });
    expect(await screen.findByText(/No session yet/)).toBeInTheDocument();
  });
});

describe('wallets', () => {
  it('shows balances and the frozen / overdrawn badges, and follows WalletChanged', async () => {
    const hub = setup('/economy/wallets', {
      wallets: [wallet('Player', 1, 'Bob', 912_400), wallet('Player', 2, 'Eve', 51_000, { frozen: true, frozenReason: 'investigating' }), wallet('Player', 3, 'Carol', -4100, { overdrawn: true })],
    });
    const eve = await screen.findByRole('row', { name: /Eve/ });
    expect(within(eve).getByText('FROZEN')).toBeInTheDocument();
    expect(within(eve).getByRole('button', { name: 'Unfreeze Eve' })).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Carol/ })).getByText('OVERDRAWN')).toBeInTheDocument();
    expect(within(screen.getByRole('row', { name: /Bob/ })).queryByText('FROZEN')).toBeNull();
    act(() => hub.push('WalletChanged', wallet('Player', 1, 'Bob', 5, { frozen: true, version: 2 })));
    await waitFor(() => expect(within(screen.getByRole('row', { name: /Bob/ })).getByText('FROZEN')).toBeInTheDocument());
    expect(within(screen.getByRole('row', { name: /Bob/ })).getByText('5')).toBeInTheDocument();
  });

  it('freeze asks for a reason, posts it, and a Viewer sees no actions', async () => {
    setup('/economy/wallets', {
      wallets: [wallet('Player', 1, 'Bob', 100)],
      route: (c) => (c.method === 'POST' && c.url.endsWith('/wallets/Player/1/freeze') ? json(200, wallet('Player', 1, 'Bob', 100, { frozen: true })) : undefined),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Freeze Bob' }));
    const dialog = screen.getByRole('dialog', { name: 'Freeze Bob' });
    await user.click(within(dialog).getByRole('button', { name: 'Freeze' }));
    expect(within(dialog).getByText('A reason is required.')).toBeInTheDocument();
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'suspicious');
    await user.click(within(dialog).getByRole('button', { name: 'Freeze' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toEqual({ frozen: true, reason: 'suspicious' });
  });

  it('hides mutation buttons from a Viewer', async () => {
    setup('/economy/wallets', { wallets: [wallet('Player', 1, 'Bob', 100)] }, 'Viewer');
    await screen.findByRole('row', { name: /Bob/ });
    expect(screen.queryByRole('button', { name: /Freeze|Adjust/ })).toBeNull();
    expect(screen.getByRole('button', { name: 'Ledger Bob' })).toBeInTheDocument();
  });
});

describe('transactions', () => {
  const donate = tx('01HZ00000000000000000AAAAA', 'Donate', [1, 'Bob', 50_000], [2, 'Dan', 50_000]);

  it('renders the loaded rows and a pushed LedgerPosted live, and marks the original when its reversal arrives', async () => {
    const hub = setup('/economy/transactions', { txs: [donate], wallets: [wallet('Player', 1, 'Bob', 1), wallet('Player', 2, 'Dan', 1)] });
    expect(await screen.findByText('Bob → Dan')).toBeInTheDocument();
    act(() => hub.push('LedgerPosted', tx('01HZ00000000000000000BBBBB', 'GameIncome', [0, 'World', 120_000], [1, 'Bob', 120_000], { actor: 'node:1' })));
    expect(await screen.findByText('World → Bob')).toBeInTheDocument();
    expect(screen.getByText('120,000')).toBeInTheDocument();
    act(() =>
      hub.push('LedgerPosted', tx('01HZ00000000000000000CCCCC', 'Reversal', [2, 'Dan', 50_000], [1, 'Bob', 50_000], { reverses: donate.id, actor: 'admin:jack' })),
    );
    expect(await screen.findByText(/↺ reversal/)).toBeInTheDocument();
    expect(screen.getByText(/^reversed$/)).toBeInTheDocument();
  });

  it('filters pushes by the active kind filter', async () => {
    const hub = setup('/economy/transactions', { txs: [donate] });
    await screen.findByText('Bob → Dan');
    const user = userEvent.setup();
    await user.selectOptions(screen.getByLabelText('Kind'), 'Donate');
    await waitFor(() => expect(calls.some((c) => c.url.includes('kind=Donate'))).toBe(true));
    act(() => hub.push('LedgerPosted', tx('01HZ00000000000000000DDDDD', 'GameIncome', [0, 'World', 5], [1, 'Bob', 5])));
    expect(screen.queryByText('World → Bob')).toBeNull();
    act(() => hub.push('LedgerPosted', tx('01HZ00000000000000000EEEEE', 'Donate', [2, 'Dan', 7], [1, 'Bob', 7])));
    expect(await screen.findByText('Dan → Bob')).toBeInTheDocument();
  });

  it('reverse: previews the overdraft, explains WouldOverdraw, and retries with force', async () => {
    let attempt = 0;
    setup('/economy/transactions', {
      txs: [donate],
      wallets: [wallet('Player', 1, 'Bob', 912_400), wallet('Player', 2, 'Dan', 20_000)],
      route: (c) => {
        if (c.method === 'POST' && c.url.endsWith(`/transactions/${donate.id}/reverse`)) {
          return ++attempt === 1
            ? problem(409, 'WouldOverdraw', 'The reversal would take Dan below zero. Use force to allow it on a player or shared wallet.')
            : json(200, tx('01HZ00000000000000000FFFFF', 'Reversal', [2, 'Dan', 50_000], [1, 'Bob', 50_000], { reverses: donate.id }));
        }
        return undefined;
      },
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: `Reverse ${donate.id}` }));
    const dialog = screen.getByRole('dialog', { name: /Reverse Donate/ });
    expect(within(dialog).getByText(/Dan's balance would become -30,000; tick force to allow/)).toBeInTheDocument();
    // a reason is required before anything is sent
    await user.click(within(dialog).getByRole('button', { name: 'Reverse' }));
    expect(within(dialog).getByText('A reason is required.')).toBeInTheDocument();
    expect(posts()).toHaveLength(0);
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'wrong recipient');
    await user.click(within(dialog).getByRole('button', { name: 'Reverse' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(/below zero.*Tick "force"/);
    await user.click(within(dialog).getByRole('checkbox', { name: /force/ }));
    await user.click(within(dialog).getByRole('button', { name: 'Reverse' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    const bodies = posts().map((c) => JSON.parse(c.body ?? '{}') as Record<string, unknown>);
    expect(bodies[0]).toMatchObject({ reason: 'wrong recipient', force: null });
    expect(bodies[1]).toMatchObject({ reason: 'wrong recipient', force: true });
  });

  it.each([
    ['AlreadyReversed', 'The transaction was already reversed (01HZ).', /already reversed/],
    ['NotReversible', 'It belongs to loan 12.', /cannot be reversed, even with force.*Loans or Trades tab/],
  ])('reverse: a 409 %s is explained and keeps the dialog open', async (code, detail, expected) => {
    setup('/economy/transactions', {
      txs: [donate],
      route: (c) => (c.method === 'POST' && c.url.endsWith('/reverse') ? problem(409, code, detail) : undefined),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: `Reverse ${donate.id}` }));
    const dialog = screen.getByRole('dialog', { name: /Reverse Donate/ });
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'oops');
    await user.click(within(dialog).getByRole('button', { name: 'Reverse' }));
    expect(await within(dialog).findByRole('alert')).toHaveTextContent(expected);
    expect(screen.getByRole('dialog', { name: /Reverse Donate/ })).toBeInTheDocument();
  });

  it('offers CSV export for admins only', async () => {
    setup('/economy/transactions', { txs: [donate] });
    const link = await screen.findByRole('link', { name: /CSV/ });
    expect(link).toHaveAttribute('href', '/api/v1/economy/ledger.csv');
  });
});

describe('loans and trades', () => {
  it('flags an overdue loan and forgives it with a reason', async () => {
    setup('/economy/loans', {
      loans: [loan(12), loan(14, { overdue: true, state: 'Overdue', lender: 'Alice', borrower: 'Carol' })],
      route: (c) => (c.method === 'POST' && c.url.endsWith('/loans/14/forgive') ? json(200, loan(14, { state: 'Forgiven' })) : undefined),
    });
    const row = await screen.findByRole('row', { name: /Alice → Carol/ });
    expect(within(row).getByText('OVERDUE')).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(within(row).getByRole('button', { name: 'Forgive loan 14' }));
    const dialog = screen.getByRole('dialog', { name: 'Forgive loan #14' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'goodwill');
    await user.click(within(dialog).getByRole('button', { name: 'Forgive' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toEqual({ reason: 'goodwill' });
  });

  it('cancels a loan with the disbursement reversal option', async () => {
    setup('/economy/loans', {
      loans: [loan(12)],
      route: (c) => (c.method === 'POST' && c.url.endsWith('/loans/12/cancel') ? json(200, loan(12, { state: 'Cancelled' })) : undefined),
    });
    const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Cancel loan 12' }));
    const dialog = screen.getByRole('dialog', { name: 'Cancel loan #12' });
    await user.click(within(dialog).getByRole('checkbox', { name: /reverse the disbursement/ }));
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'mistake');
    await user.click(within(dialog).getByRole('button', { name: 'Cancel loan' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toMatchObject({ reason: 'mistake', reverseDisbursement: true });
  });

  it('shows an InDoubt trade and resolves it as refund', async () => {
    const hub = setup('/economy/trades', {
      trades: [trade(11), trade(13, { state: 'Proposed', queryAttempts: 0, initiator: 'Bob', initiatorGives: [{ kind: 'Credits', amount: 20000, wareRef: 0, asset: 0 }] })],
      route: (c) => (c.method === 'POST' && c.url.endsWith('/trades/11/resolve') ? json(200, trade(11, { state: 'RolledBack' })) : undefined),
    });
    const row = await screen.findByRole('row', { name: /Eve: ship #77/ });
    expect(within(row).getByText(/IN DOUBT/)).toBeInTheDocument();
    expect(within(row).queryByRole('button', { name: /Cancel trade/ })).toBeNull();
    expect(within(screen.getByRole('row', { name: /^13/ })).getByRole('button', { name: 'Cancel trade 13' })).toBeInTheDocument();
    const user = userEvent.setup();
    await user.click(within(row).getByRole('button', { name: 'Refund trade 11' }));
    const dialog = screen.getByRole('dialog', { name: 'Resolve trade #11: refund' });
    await user.type(within(dialog).getByRole('textbox', { name: 'Reason' }), 'authority never applied it');
    await user.click(within(dialog).getByRole('button', { name: 'Refund' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(JSON.parse(posts()[0]?.body ?? '{}')).toEqual({ outcome: 'refund', reason: 'authority never applied it' });
    // the hub pushes the new state
    act(() => hub.push('TradeChanged', trade(11, { state: 'RolledBack' })));
    await waitFor(() => expect(screen.queryByRole('row', { name: /Eve: ship #77/ })).toBeNull());
  });
});

describe('policy', () => {
  it('shows the migration preview on a live credit mode change and confirms with confirm: true', async () => {
    let patches = 0;
    const preview = {
      needed: true, requiresConfirm: true, from: 'PerPlayer', to: 'Shared', kind: 'Merge', totalMoved: 150_000, teamMoves: ['Red: 2 wallets merged'],
      changes: [
        { kind: 'Player', ownerId: 1, ownerName: 'Bob', before: 100_000, after: 0 },
        { kind: 'TeamShared', ownerId: 1, ownerName: 'Red (shared)', before: 0, after: 150_000 },
      ],
    };
    setup('/economy/policy', {
      route: (c) => {
        if (c.method !== 'PATCH') return undefined;
        patches++;
        const body = JSON.parse(c.body ?? '{}') as { confirm?: boolean | null };
        return body.confirm
          ? json(200, { ...policy, creditMode: 'Shared', effectiveCreditMode: 'Shared' })
          : problem(409, 'ConfirmationRequired', 'Switching moves money.', { migrationPreview: preview });
      },
    });
    const user = userEvent.setup();
    const mode = await screen.findByLabelText('Credit mode');
    await user.selectOptions(mode, 'Shared');
    await user.click(screen.getByRole('button', { name: /Save policy \(1 change\)/ }));
    const dialog = await screen.findByRole('dialog', { name: 'Confirm credit mode change' });
    expect(within(dialog).getByText(/moves 150,000 Cr/)).toBeInTheDocument();
    expect(within(dialog).getByRole('row', { name: /Bob 100,000 0/ })).toBeInTheDocument();
    expect(within(dialog).getByText('Red: 2 wallets merged')).toBeInTheDocument();
    expect(patches).toBe(1);
    await user.click(within(dialog).getByRole('button', { name: 'Apply and move balances' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(patches).toBe(2);
    const sent = calls.filter((c) => c.method === 'PATCH').map((c) => JSON.parse(c.body ?? '{}') as Record<string, unknown>);
    expect(sent[0]).toMatchObject({ creditMode: 'Shared', confirm: null });
    expect(sent[1]).toMatchObject({ creditMode: 'Shared', confirm: true });
    expect(sent[1]).not.toHaveProperty('donateScope');
    expect(screen.getByText('Economy policy saved.')).toBeInTheDocument();
  });

  it('shows per-field validation errors from the server and rejects non-numbers locally', async () => {
    setup('/economy/policy', {
      route: (c) =>
        c.method === 'PATCH'
          ? json(400, { title: 'ValidationFailed', status: 400, code: 'ValidationFailed', errors: { maxLoanInterestBp: ['Must be at most 100000.'] } })
          : undefined,
    });
    const user = userEvent.setup();
    const interest = await screen.findByLabelText(/Max interest/);
    await user.clear(interest);
    await user.type(interest, 'abc');
    expect(screen.getAllByText('Enter a whole number.').length).toBeGreaterThan(0);
    await user.clear(interest);
    await user.type(interest, '999999');
    await user.click(screen.getByRole('button', { name: /Save policy/ }));
    expect(await screen.findByText('Must be at most 100000.')).toBeInTheDocument();
  });
});
