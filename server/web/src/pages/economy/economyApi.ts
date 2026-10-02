import { api, ApiError, http } from '../../api/http';
import type {
  AdjustWalletRequest,
  AuditorReportDto,
  CancelLoanRequest,
  CancelTradeRequest,
  EconomyEventDto,
  EconomyPolicyDto,
  EconomyPolicyPatch,
  EconomySummaryDto,
  ForgiveLoanRequest,
  FreezeWalletRequest,
  LedgerTxDto,
  LoanDetailDto,
  LoanDto,
  MigrationPreviewDto,
  ResolveTradeRequest,
  ReverseTransactionRequest,
  TradeDetailDto,
  TradeOfferDto,
  WalletDetailDto,
  WalletDto,
} from '../../generated/generated';

const base = '/api/v1/economy';

/** Fresh idempotency key per submit: a double click replays instead of posting twice; a changed retry (force) is a new request. */
function newKey(): string {
  const c = globalThis.crypto as Crypto | undefined;
  return c?.randomUUID ? c.randomUUID() : `k${Date.now().toString(36)}${Math.random().toString(36).slice(2)}`;
}

function post<T>(path: string, body: unknown, idempotent = false): Promise<T> {
  return http<T>(path, {
    method: 'POST',
    body: JSON.stringify(body),
    headers: idempotent ? { 'Idempotency-Key': newKey() } : undefined,
  });
}

export interface TxFilter {
  /** `kind:ownerId`, e.g. `Player:3`. */
  wallet: string;
  kind: string;
  actor: string;
}

export const emptyTxFilter: TxFilter = { wallet: '', kind: '', actor: '' };

function txQuery(f: TxFilter, before: string | null, limit: number): string {
  const q = new URLSearchParams({ limit: String(limit) });
  if (f.wallet) q.set('wallet', f.wallet);
  if (f.kind) q.set('kind', f.kind);
  if (f.actor.trim()) q.set('actor', f.actor.trim());
  if (before) q.set('before', before);
  return q.toString();
}

export type PolicyResult =
  | { status: 'saved'; policy: EconomyPolicyDto }
  | { status: 'confirm'; preview: MigrationPreviewDto; message: string };

/** PATCH /policy. A live credit-mode change answers 409 ConfirmationRequired with a preview body, which `http` would drop, so this reads it. */
async function patchPolicy(patch: Partial<EconomyPolicyPatch>): Promise<PolicyResult> {
  const res = await fetch(`${base}/policy`, {
    method: 'PATCH',
    credentials: 'same-origin',
    headers: { 'X-X4MP': '1', 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  });
  const text = await res.text();
  const body = text === '' ? null : (JSON.parse(text) as Record<string, unknown>);
  if (res.ok) return { status: 'saved', policy: body as unknown as EconomyPolicyDto };
  if (res.status === 409 && body?.['code'] === 'ConfirmationRequired' && body['migrationPreview']) {
    return {
      status: 'confirm',
      preview: body['migrationPreview'] as MigrationPreviewDto,
      message: String(body['detail'] ?? 'Confirm the credit mode change.'),
    };
  }
  throw new ApiError(
    res.status,
    String(body?.['detail'] ?? body?.['title'] ?? res.statusText),
    (body?.['code'] as string | undefined) ?? null,
    (body?.['errors'] as Record<string, string[]> | undefined) ?? null,
    (body?.['errorCodes'] as Record<string, string> | undefined) ?? null,
  );
}

const walletPath = (kind: string, ownerId: number) => `${base}/wallets/${encodeURIComponent(kind)}/${ownerId}`;

/** REST calls of the Economy page (server-design 4.4). Every mutation needs a reason; the server enforces it too. */
export const economyApi = {
  summary: () => api.get<EconomySummaryDto>(`${base}/summary`),
  policy: () => api.get<EconomyPolicyDto>(`${base}/policy`),
  patchPolicy,
  wallets: () => api.get<WalletDto[]>(`${base}/wallets`),
  wallet: (kind: string, ownerId: number) => api.get<WalletDetailDto>(walletPath(kind, ownerId)),
  adjust: (kind: string, ownerId: number, body: AdjustWalletRequest) =>
    post<LedgerTxDto>(`${walletPath(kind, ownerId)}/adjust`, body, true),
  freeze: (kind: string, ownerId: number, body: FreezeWalletRequest) => post<WalletDto>(`${walletPath(kind, ownerId)}/freeze`, body),
  transactions: (f: TxFilter, before: string | null = null, limit = 100) =>
    api.get<LedgerTxDto[]>(`${base}/transactions?${txQuery(f, before, limit)}`),
  transaction: (id: string) => api.get<LedgerTxDto>(`${base}/transactions/${encodeURIComponent(id)}`),
  reverse: (id: string, body: ReverseTransactionRequest) =>
    post<LedgerTxDto>(`${base}/transactions/${encodeURIComponent(id)}/reverse`, body, true),
  ledgerCsvUrl: `${base}/ledger.csv`,
  loans: () => api.get<LoanDto[]>(`${base}/loans`),
  loan: (id: number) => api.get<LoanDetailDto>(`${base}/loans/${id}`),
  forgiveLoan: (id: number, body: ForgiveLoanRequest) => post<LoanDto>(`${base}/loans/${id}/forgive`, body),
  cancelLoan: (id: number, body: CancelLoanRequest) => post<LoanDto>(`${base}/loans/${id}/cancel`, body),
  trades: () => api.get<TradeOfferDto[]>(`${base}/trades`),
  trade: (id: number) => api.get<TradeDetailDto>(`${base}/trades/${id}`),
  cancelTrade: (id: number, body: CancelTradeRequest) => post<TradeOfferDto>(`${base}/trades/${id}/cancel`, body),
  resolveTrade: (id: number, body: ResolveTradeRequest) => post<TradeOfferDto>(`${base}/trades/${id}/resolve`, body),
  events: (refType: string, refId: number) =>
    api.get<EconomyEventDto[]>(`${base}/events?refType=${encodeURIComponent(refType)}&refId=${refId}`),
  audit: (acknowledge: boolean) => post<AuditorReportDto>(`${base}/audit`, { acknowledge }),
};

/** Plain-language text for the economy 409s, so the dialogs explain instead of echoing a code. */
export function describeEconomyError(e: unknown): string {
  if (!(e instanceof ApiError)) return 'Could not reach the server.';
  switch (e.code) {
    case 'NoSession':
      return 'There is no live session yet, so there is no economy to change.';
    case 'AlreadyReversed':
      return `This transaction was already reversed. ${e.message}`;
    case 'NotReversible':
      return `This transaction cannot be reversed, even with force. ${e.message} Use the Loans or Trades tab actions instead.`;
    case 'WouldOverdraw':
      return `${e.message} Tick "force" to allow it.`;
    case 'EconomyFrozen':
      return `The economy is frozen by the auditor. ${e.message}`;
    case 'WrongState':
      return `The item changed in the meantime. ${e.message}`;
    default:
      return e.message;
  }
}
