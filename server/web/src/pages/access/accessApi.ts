import { api } from '../../api/http';
import type {
  AdminUserDto,
  ApiTokenCreatedDto,
  ApiTokenDto,
  AuditEntryDto,
  CreateTokenRequest,
  CreateUserRequest,
  PatchUserRequest,
} from '../../generated/generated';

/** The roles the auth layer knows (`AdminRoles`); the server rejects anything else. */
export const ROLES = ['Admin', 'Viewer', 'ModEditor'] as const;

/** REST calls of the Access page: accounts, API tokens and the audit log. */
export const accessApi = {
  users: () => api.get<AdminUserDto[]>('/api/v1/users'),
  createUser: (body: CreateUserRequest) => api.post<AdminUserDto>('/api/v1/users', body),
  patchUser: (id: number, body: PatchUserRequest) => api.patch<AdminUserDto>(`/api/v1/users/${id}`, body),
  deleteUser: (id: number) => api.delete(`/api/v1/users/${id}`),
  tokens: () => api.get<ApiTokenDto[]>('/api/v1/tokens'),
  createToken: (body: CreateTokenRequest) => api.post<ApiTokenCreatedDto>('/api/v1/tokens', body),
  revokeToken: (id: number) => api.delete(`/api/v1/tokens/${id}`),
  audit: (limit: number) => api.get<AuditEntryDto[]>(`/api/v1/audit?limit=${limit}`),
};

/** ISO timestamps as the page shows them (empty for null). */
export function formatTime(value: string | null | undefined): string {
  if (!value) return '';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '' : date.toLocaleString();
}
