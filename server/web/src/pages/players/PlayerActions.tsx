import { useState } from 'react';
import { useAlerts } from '../../alerts/AlertsProvider';
import type { PlayerDto } from '../../generated/generated';
import { BanDialog, ConfirmDialog, KickDialog, MuteDialog } from './ActionDialogs';
import { playersApi } from './playersApi';

type Open = 'kick' | 'mute' | 'unmute' | 'ban' | 'unban' | 'release' | null;

/**
 * Action buttons for one player plus the dialogs they open. `onChanged` runs after the server accepted an action, so
 * the caller can reload. Buttons only appear for what applies (Kick when online, Unban when banned, ...).
 */
export function PlayerActions({
  player,
  online,
  onChanged,
  withRelease = false,
}: {
  player: PlayerDto;
  online: boolean;
  onChanged: () => void;
  withRelease?: boolean;
}) {
  const [open, setOpen] = useState<Open>(null);
  const { toast } = useAlerts();
  const close = () => setOpen(null);
  const done = (message: string) => {
    toast('success', message);
    onChanged();
  };
  const banned = player.activeBan !== null;

  return (
    <div className="actions">
      {online && (
        <button type="button" className="ghost" aria-label={`Kick ${player.name}`} onClick={() => setOpen('kick')}>
          Kick
        </button>
      )}
      {player.muted ? (
        <button type="button" className="ghost" aria-label={`Unmute ${player.name}`} onClick={() => setOpen('unmute')}>
          Unmute
        </button>
      ) : (
        <button type="button" className="ghost" aria-label={`Mute ${player.name}`} onClick={() => setOpen('mute')}>
          Mute
        </button>
      )}
      {banned ? (
        <button type="button" className="ghost" aria-label={`Unban ${player.name}`} onClick={() => setOpen('unban')}>
          Unban
        </button>
      ) : (
        <button type="button" className="ghost" aria-label={`Ban ${player.name}`} onClick={() => setOpen('ban')}>
          Ban
        </button>
      )}
      {withRelease && (
        <button
          type="button"
          className="ghost"
          disabled={online}
          title={online ? 'Kick the player first' : undefined}
          onClick={() => setOpen('release')}
        >
          Release name
        </button>
      )}

      {open === 'kick' && <KickDialog player={player} onClose={close} onDone={done} />}
      {open === 'mute' && <MuteDialog player={player} onClose={close} onDone={done} />}
      {open === 'ban' && <BanDialog player={player} onClose={close} onDone={done} />}
      {open === 'unmute' && (
        <ConfirmDialog
          title={`Unmute ${player.name}`}
          body={`Let ${player.name} chat again?`}
          confirm="Unmute"
          run={() => playersApi.unmute(player.id)}
          doneMessage={`Unmuted ${player.name}.`}
          onClose={close}
          onDone={done}
        />
      )}
      {open === 'unban' && player.activeBan && (
        <ConfirmDialog
          title={`Unban ${player.name}`}
          body={`Revoke the ban on ${player.name} (${player.activeBan.reason})? They can rejoin right away.`}
          confirm="Unban"
          run={() => playersApi.unban(player.activeBan?.id ?? 0)}
          doneMessage={`Unbanned ${player.name}.`}
          onClose={close}
          onDone={done}
        />
      )}
      {open === 'release' && (
        <ConfirmDialog
          title={`Release the name ${player.name}`}
          body={`Free the name ${player.name} so another player (or a new key) can take it? ${player.name} keeps no claim on it.`}
          confirm="Release name"
          run={() => playersApi.releaseName(player.id)}
          doneMessage={`Released the name ${player.name}.`}
          onClose={close}
          onDone={done}
        />
      )}
    </div>
  );
}
