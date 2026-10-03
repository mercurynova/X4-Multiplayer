import { api } from '../../api/http';
import type {
  BanDto,
  CreateBanRequest,
  KickRequest,
  MuteRequest,
  PatchPlayerRequest,
  NodeDiagnosticsDto,
  PlayerDetailDto,
  PlayerDto,
} from '../../generated/generated';

const base = '/api/v1/players';

/** REST calls of the Players pages (server-design 4.4). Kick, mute and ban require a reason; the server enforces it too. */
export const playersApi = {
  list: () => api.get<PlayerDto[]>(`${base}?limit=1000`),
  get: (id: number) => api.get<PlayerDetailDto>(`${base}/${id}`),
  diagnostics: (id: number) => api.get<NodeDiagnosticsDto>(`${base}/${id}/diagnostics`),
  kick: (id: number, reason: string, removeAvatar = false) =>
    api.post(`${base}/${id}/kick`, { reason, removeAvatar: removeAvatar ? true : null } satisfies KickRequest),
  mute: (id: number, minutes: number | null, reason: string) =>
    api.post(`${base}/${id}/mute`, { minutes, reason } satisfies MuteRequest),
  unmute: (id: number) => api.delete(`${base}/${id}/mute`),
  setNotes: (id: number, notes: string) =>
    api.patch<PlayerDto>(`${base}/${id}`, { notes, releaseName: null } satisfies PatchPlayerRequest),
  releaseName: (id: number) =>
    api.patch<PlayerDto>(`${base}/${id}`, { notes: null, releaseName: true } satisfies PatchPlayerRequest),
  ban: (req: CreateBanRequest) => api.post<BanDto>('/api/v1/bans', req),
  unban: (banId: number) => api.delete(`/api/v1/bans/${banId}`),
};
