-- 0005_admin_security: admin API hardening (task M1-S2).
--
-- admin_users.pw_version: bumped by every password CHANGE (not by a work-factor upgrade). The cookie carries it as a claim and the
--   cookie validator rejects a cookie whose version no longer matches, so a password change signs every other browser session out.
-- admin_users.pw_changed_at: when the password last changed (audit and GUI).
-- api_tokens.owner_id: the admin user who minted the token through the API (NULL for tokens created out of band). A password
--   change revokes the tokens that user owns; ownerless tokens are not tied to anyone's password.

ALTER TABLE admin_users ADD COLUMN pw_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE admin_users ADD COLUMN pw_changed_at TEXT;
ALTER TABLE api_tokens ADD COLUMN owner_id INTEGER REFERENCES admin_users(id);
