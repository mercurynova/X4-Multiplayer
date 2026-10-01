-- 0005_loans: loans (server-design 2.14, roadmap M1-E4).
-- One row per loan, rewritten in the same transaction as the ledger posting that changes it (money and state commit together).
-- The principal sits in the escrow wallet (kind 'escrow', owner = 2^40 + id) while state = 'Offered'.

CREATE TABLE loans (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  id INTEGER NOT NULL,
  lender_id INTEGER NOT NULL, borrower_id INTEGER NOT NULL,
  principal INTEGER NOT NULL CHECK (principal > 0),
  repay_total INTEGER NOT NULL CHECK (repay_total >= principal),
  outstanding INTEGER NOT NULL CHECK (outstanding >= 0),
  repaid INTEGER NOT NULL DEFAULT 0, forgiven INTEGER NOT NULL DEFAULT 0,
  interest_bp INTEGER NOT NULL DEFAULT 0,
  auto_repay_pct INTEGER NOT NULL DEFAULT 0 CHECK (auto_repay_pct BETWEEN 0 AND 100),
  due_in_s INTEGER NOT NULL DEFAULT 0,                 -- real-time seconds after acceptance; 0 = no due date
  due_at TEXT,                                         -- set at acceptance
  state TEXT NOT NULL CHECK (state IN ('Offered','Active','Overdue','Repaid','Declined','Withdrawn','Expired','Forgiven','Cancelled')),
  created_at TEXT NOT NULL, offer_expires_at TEXT NOT NULL,
  accepted_at TEXT, closed_at TEXT, close_reason TEXT, memo TEXT,
  offer_request_key TEXT NOT NULL,
  PRIMARY KEY (session_id, id));
CREATE INDEX ix_loans_state ON loans(session_id, state);
CREATE INDEX ix_loans_lender ON loans(session_id, lender_id);
CREATE INDEX ix_loans_borrower ON loans(session_id, borrower_id);
