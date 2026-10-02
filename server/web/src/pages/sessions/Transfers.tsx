import type { TransferProgressDto } from '../../generated/generated';
import { formatBytes, percent } from './format';

export function transferLabel(t: TransferProgressDto): string {
  return `${t.isUpload ? 'Upload from' : 'Download to'} ${t.playerName}`;
}

/** Live save transfers per player, from the hub `SaveTransfer` push (server-design 4.6). */
export function Transfers({ transfers }: { transfers: readonly TransferProgressDto[] }) {
  return (
    <section aria-labelledby="transfers-h">
      <h2 id="transfers-h">Transfers</h2>
      {transfers.length === 0 ? (
        <p className="muted">No save transfers right now.</p>
      ) : (
        <ul className="transfers">
          {transfers.map((t) => {
            const pct = percent(t.done, t.size);
            return (
              <li key={t.id}>
                <span>
                  {t.isUpload ? '⇡' : '⇣'} {transferLabel(t)} <span className="muted">{t.sha256.slice(0, 12)}</span>
                </span>
                <progress value={t.done} max={Math.max(t.size, 1)} aria-label={`${transferLabel(t)} progress`} />
                <span>
                  {pct}% ({formatBytes(t.done)} of {formatBytes(t.size)}){t.finished ? ' - done' : ''}
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
