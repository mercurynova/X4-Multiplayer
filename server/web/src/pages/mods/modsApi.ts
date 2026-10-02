import { api } from '../../api/http';
import type {
  ImportModsRequest,
  ModCatalogEntryDto,
  ModEntryDto,
  ModPolicyDto,
  ModsStateDto,
  PatchModPolicyRequest,
  PlayerExtensionsDto,
  PutModCatalogRequest,
  PutModEntryRequest,
  UnboundRejectionDto,
} from '../../generated/generated';

const base = '/api/v1/mods';

export const RULES = ['Required', 'Allowed', 'Blocked'] as const;
export const VERSION_RULES = ['Exact', 'AtLeast', 'Any'] as const;

/** REST calls of the Mods page (docs/mod-management.md 7). Changes come back through the hub's mods topic as well. */
export const modsApi = {
  state: () => api.get<ModsStateDto>(base),
  patchPolicy: (patch: PatchModPolicyRequest) => api.patch<ModPolicyDto>(`${base}/policy`, patch),
  putEntry: (id: string, body: PutModEntryRequest) => api.put<ModEntryDto>(`${base}/entries/${encodeURIComponent(id)}`, body),
  deleteEntry: (id: string) => api.delete(`${base}/entries/${encodeURIComponent(id)}`),
  importFromAuthority: (req: ImportModsRequest) => api.post<ModPolicyDto>(`${base}/import-from-authority`, req),
  catalog: () => api.get<ModCatalogEntryDto[]>(`${base}/catalog`),
  putCatalog: (id: string, body: PutModCatalogRequest) => api.put<ModCatalogEntryDto>(`${base}/catalog/${encodeURIComponent(id)}`, body),
  rejections: () => api.get<UnboundRejectionDto[]>(`${base}/rejections`),
  playerExtensions: (playerId: number) => api.get<PlayerExtensionsDto>(`/api/v1/players/${playerId}/extensions`),
};

/** A `PutModEntryRequest` with everything unset. */
export const emptyEntryRequest: PutModEntryRequest = {
  name: null,
  rule: null,
  enabled: null,
  classOverride: null,
  versionRule: null,
  version: null,
  contentHash: null,
  nexusUrl: null,
  workshopId: null,
  notes: null,
};
