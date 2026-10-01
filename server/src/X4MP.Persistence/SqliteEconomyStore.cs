using System.Globalization;
using Microsoft.Data.Sqlite;
using X4MP.Core.Economy;

namespace X4MP.Persistence;

/// <summary>
/// The ledger in SQLite (<c>0002_economy.sql</c>). It owns a dedicated connection with <c>synchronous=FULL</c> and does
/// not use the batched <see cref="PersistenceWriter"/>: <see cref="Commit"/> returns only after the transaction is
/// durable, so an acknowledged credit operation survives a crash. Called on the session actor thread, one operation
/// at a time. Append-only rules are enforced by the database triggers, not only by this class.
/// </summary>
public sealed class SqliteEconomyStore(SqliteConnectionFactory factory, bool fullSync = true) : IEconomyStore, IDisposable
{
    private SqliteConnection? _connection;

    private SqliteConnection Db
    {
        get
        {
            if (_connection is { State: System.Data.ConnectionState.Open } open)
            {
                return open;
            }

            _connection?.Dispose();
            var connection = factory.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = fullSync ? "PRAGMA synchronous=FULL;" : "PRAGMA synchronous=NORMAL;"; // the shared factory uses NORMAL; credits must survive power loss, so FULL is the default
            pragma.ExecuteNonQuery();
            _connection = connection;
            return connection;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    public EconomyLoad Load(long sessionId)
    {
        var db = Db;
        var wallets = new List<WalletState>();
        using (var cmd = Command(db, "SELECT kind, owner_id, balance, version, frozen, frozen_reason FROM wallets WHERE session_id = $s", sessionId))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                wallets.Add(new WalletState(
                    new WalletId(ParseKind(reader.GetString(0)), reader.GetInt64(1)),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4) != 0,
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
        }

        var seqs = new Dictionary<int, ulong>();
        using (var cmd = Command(db, "SELECT player_id, last_seq FROM economy_delta_seq WHERE session_id = $s", sessionId))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                seqs[(int)reader.GetInt64(0)] = (ulong)reader.GetInt64(1);
            }
        }

        string? mode;
        using (var cmd = Command(db, "SELECT applied_mode FROM economy_state WHERE session_id = $s", sessionId))
        {
            mode = cmd.ExecuteScalar() as string;
        }

        var teams = new Dictionary<int, int?>();
        using (var cmd = Command(db, "SELECT player_id, team_id FROM economy_player_team WHERE session_id = $s", sessionId))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                teams[(int)reader.GetInt64(0)] = reader.IsDBNull(1) ? null : (int)reader.GetInt64(1);
            }
        }

        return new EconomyLoad(wallets, seqs, new EconomyLayout(mode, teams), LoadLoans(db, sessionId));
    }

    private static List<LoanRecord> LoadLoans(SqliteConnection db, long sessionId)
    {
        var loans = new List<LoanRecord>();
        using var cmd = Command(db,
            "SELECT id, lender_id, borrower_id, principal, repay_total, outstanding, repaid, forgiven, interest_bp, auto_repay_pct, due_in_s, due_at, state, " +
            "created_at, offer_expires_at, accepted_at, closed_at, close_reason, memo, offer_request_key FROM loans WHERE session_id = $s ORDER BY id", sessionId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            loans.Add(new LoanRecord(
                reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                reader.GetInt64(6), reader.GetInt64(7), (int)reader.GetInt64(8), (int)reader.GetInt64(9), (int)reader.GetInt64(10),
                reader.IsDBNull(11) ? null : ParseTime(reader.GetString(11)),
                Enum.Parse<LoanState>(reader.GetString(12)),
                ParseTime(reader.GetString(13)), ParseTime(reader.GetString(14)),
                reader.IsDBNull(15) ? null : ParseTime(reader.GetString(15)),
                reader.IsDBNull(16) ? null : ParseTime(reader.GetString(16)),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.GetString(19)));
        }

        return loans;
    }

    private static DateTimeOffset ParseTime(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void WriteLoan(SqliteConnection db, SqliteTransaction transaction, long sessionId, LoanRecord loan)
    {
        using var cmd = Command(db,
            "INSERT INTO loans (session_id, id, lender_id, borrower_id, principal, repay_total, outstanding, repaid, forgiven, interest_bp, auto_repay_pct, " +
            "due_in_s, due_at, state, created_at, offer_expires_at, accepted_at, closed_at, close_reason, memo, offer_request_key) " +
            "VALUES ($s, $id, $l, $b, $p, $rt, $o, $rp, $f, $ibp, $arp, $dis, $due, $st, $ca, $oe, $aa, $cl, $cr, $m, $k) " +
            "ON CONFLICT (session_id, id) DO UPDATE SET outstanding = excluded.outstanding, repaid = excluded.repaid, forgiven = excluded.forgiven, " +
            "due_at = excluded.due_at, state = excluded.state, accepted_at = excluded.accepted_at, closed_at = excluded.closed_at, close_reason = excluded.close_reason", sessionId);
        cmd.Transaction = transaction;
        cmd.Parameters.AddWithValue("$id", loan.Id);
        cmd.Parameters.AddWithValue("$l", loan.LenderId);
        cmd.Parameters.AddWithValue("$b", loan.BorrowerId);
        cmd.Parameters.AddWithValue("$p", loan.Principal);
        cmd.Parameters.AddWithValue("$rt", loan.RepayTotal);
        cmd.Parameters.AddWithValue("$o", loan.Outstanding);
        cmd.Parameters.AddWithValue("$rp", loan.Repaid);
        cmd.Parameters.AddWithValue("$f", loan.Forgiven);
        cmd.Parameters.AddWithValue("$ibp", loan.InterestBasisPoints);
        cmd.Parameters.AddWithValue("$arp", loan.AutoRepayPercent);
        cmd.Parameters.AddWithValue("$dis", loan.DueInSeconds);
        cmd.Parameters.AddWithValue("$due", loan.DueAt is { } due ? Iso(due) : DBNull.Value);
        cmd.Parameters.AddWithValue("$st", loan.State.ToString());
        cmd.Parameters.AddWithValue("$ca", Iso(loan.CreatedAt));
        cmd.Parameters.AddWithValue("$oe", Iso(loan.OfferExpiresAt));
        cmd.Parameters.AddWithValue("$aa", loan.AcceptedAt is { } accepted ? Iso(accepted) : DBNull.Value);
        cmd.Parameters.AddWithValue("$cl", loan.ClosedAt is { } closed ? Iso(closed) : DBNull.Value);
        cmd.Parameters.AddWithValue("$cr", (object?)loan.CloseReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$m", (object?)loan.Memo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", loan.OfferRequestKey);
        cmd.ExecuteNonQuery();
    }

    public void Commit(EconomyCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var db = Db;
        using var transaction = db.BeginTransaction(deferred: false); // BEGIN IMMEDIATE: no lock-upgrade surprises against the write-behind writer
        try
        {
            var at = Iso(commit.At);
            if (commit.Transaction is { } tx)
            {
                using (var cmd = Command(db, "INSERT INTO ledger_tx (id, session_id, ts, kind, actor, request_id, ref_type, ref_id, reverses_tx, note) " +
                    "VALUES ($id, $s, $ts, $kind, $actor, $req, $rt, $rid, $rev, $note)", commit.SessionId))
                {
                    cmd.Transaction = transaction;
                    cmd.Parameters.AddWithValue("$id", tx.Id);
                    cmd.Parameters.AddWithValue("$ts", Iso(tx.At));
                    cmd.Parameters.AddWithValue("$kind", tx.Kind.ToString());
                    cmd.Parameters.AddWithValue("$actor", tx.Actor);
                    cmd.Parameters.AddWithValue("$req", (object?)tx.RequestId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$rt", (object?)tx.RefType ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$rid", (object?)tx.RefId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$rev", (object?)tx.Reverses ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$note", (object?)tx.Note ?? DBNull.Value);
                    cmd.ExecuteNonQuery();
                }

                var seq = 0;
                foreach (var entry in tx.Entries)
                {
                    using var cmd = db.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = "INSERT INTO ledger_entries (tx_id, seq, wallet_kind, wallet_owner_id, amount, balance_after) VALUES ($tx, $seq, $k, $o, $a, $b)";
                    cmd.Parameters.AddWithValue("$tx", tx.Id);
                    cmd.Parameters.AddWithValue("$seq", seq++);
                    cmd.Parameters.AddWithValue("$k", KindName(entry.Wallet.Kind));
                    cmd.Parameters.AddWithValue("$o", entry.Wallet.OwnerId);
                    cmd.Parameters.AddWithValue("$a", entry.Amount);
                    cmd.Parameters.AddWithValue("$b", entry.BalanceAfter);
                    cmd.ExecuteNonQuery();
                }
            }

            foreach (var wallet in commit.Wallets)
            {
                using var cmd = Command(db, "INSERT INTO wallets (session_id, kind, owner_id, balance, version, frozen, frozen_reason, overdrawn, updated_at) " +
                    "VALUES ($s, $k, $o, $b, $v, $f, $fr, $od, $at) " +
                    "ON CONFLICT (session_id, kind, owner_id) DO UPDATE SET balance = excluded.balance, version = excluded.version, " +
                    "frozen = excluded.frozen, frozen_reason = excluded.frozen_reason, overdrawn = excluded.overdrawn, updated_at = excluded.updated_at", commit.SessionId);
                cmd.Transaction = transaction;
                cmd.Parameters.AddWithValue("$k", KindName(wallet.Id.Kind));
                cmd.Parameters.AddWithValue("$o", wallet.Id.OwnerId);
                cmd.Parameters.AddWithValue("$b", wallet.Balance);
                cmd.Parameters.AddWithValue("$v", wallet.Version);
                cmd.Parameters.AddWithValue("$f", wallet.Frozen ? 1 : 0);
                cmd.Parameters.AddWithValue("$fr", (object?)wallet.FrozenReason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$od", wallet.Overdrawn ? 1 : 0);
                cmd.Parameters.AddWithValue("$at", at);
                cmd.ExecuteNonQuery();
            }

            if (commit.Request is { } request)
            {
                using var cmd = Command(db, "INSERT INTO economy_requests (session_id, player_id, request_id, type, payload_hash, result_json, ts) " +
                    "VALUES ($s, $p, $r, $t, $h, $j, $ts)", commit.SessionId);
                cmd.Transaction = transaction;
                cmd.Parameters.AddWithValue("$p", request.PlayerId);
                cmd.Parameters.AddWithValue("$r", request.RequestId);
                cmd.Parameters.AddWithValue("$t", request.Type);
                cmd.Parameters.AddWithValue("$h", request.PayloadHash);
                cmd.Parameters.AddWithValue("$j", request.ResultJson);
                cmd.Parameters.AddWithValue("$ts", Iso(request.At));
                cmd.ExecuteNonQuery();
            }

            if (commit.DeltaSeq is { } delta)
            {
                using var cmd = Command(db, "INSERT INTO economy_delta_seq (session_id, player_id, last_seq) VALUES ($s, $p, $q) " +
                    "ON CONFLICT (session_id, player_id) DO UPDATE SET last_seq = excluded.last_seq", commit.SessionId);
                cmd.Transaction = transaction;
                cmd.Parameters.AddWithValue("$p", delta.PlayerId);
                cmd.Parameters.AddWithValue("$q", (long)delta.Seq);
                cmd.ExecuteNonQuery();
            }

            if (commit.Layout is { } layout)
            {
                if (layout.AppliedMode is not null)
                {
                    using var cmd = Command(db, "INSERT INTO economy_state (session_id, applied_mode) VALUES ($s, $m) " +
                        "ON CONFLICT (session_id) DO UPDATE SET applied_mode = excluded.applied_mode", commit.SessionId);
                    cmd.Transaction = transaction;
                    cmd.Parameters.AddWithValue("$m", layout.AppliedMode);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = Command(db, "DELETE FROM economy_player_team WHERE session_id = $s", commit.SessionId))
                {
                    cmd.Transaction = transaction;
                    cmd.ExecuteNonQuery();
                }

                foreach (var (player, team) in layout.PlayerTeams)
                {
                    using var cmd = Command(db, "INSERT INTO economy_player_team (session_id, player_id, team_id) VALUES ($s, $p, $t)", commit.SessionId);
                    cmd.Transaction = transaction;
                    cmd.Parameters.AddWithValue("$p", player);
                    cmd.Parameters.AddWithValue("$t", team is { } t ? t : DBNull.Value);
                    cmd.ExecuteNonQuery();
                }
            }

            foreach (var loan in commit.Loans ?? [])
            {
                WriteLoan(db, transaction, commit.SessionId, loan);
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public EconomyRequestRecord? FindRequest(long sessionId, int playerId, string requestId)
    {
        using var cmd = Command(Db, "SELECT type, payload_hash, result_json, ts FROM economy_requests WHERE session_id = $s AND player_id = $p AND request_id = $r", sessionId);
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$r", requestId);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new EconomyRequestRecord(sessionId, playerId, requestId, reader.GetString(0), (byte[])reader["payload_hash"], reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture))
            : null;
    }

    public long SumInflow(long sessionId, TxKind kind, string actor, DateTimeOffset since)
    {
        using var cmd = Command(Db,
            "SELECT COALESCE(SUM(e.amount), 0) FROM ledger_entries e JOIN ledger_tx t ON t.id = e.tx_id " +
            "WHERE t.session_id = $s AND t.kind = $k AND t.actor = $a AND t.ts >= $since AND e.amount > 0", sessionId);
        cmd.Parameters.AddWithValue("$k", kind.ToString());
        cmd.Parameters.AddWithValue("$a", actor);
        cmd.Parameters.AddWithValue("$since", Iso(since));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public LedgerAuditData ReadAuditData(long sessionId)
    {
        var db = Db;
        var unbalanced = new List<string>();
        using (var cmd = Command(db,
            "SELECT t.id FROM ledger_tx t LEFT JOIN ledger_entries e ON e.tx_id = t.id WHERE t.session_id = $s " +
            "GROUP BY t.id HAVING COUNT(e.tx_id) < 2 OR SUM(e.amount) <> 0 LIMIT 100", sessionId))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                unbalanced.Add(reader.GetString(0));
            }
        }

        var sums = new Dictionary<WalletId, long>();
        using (var cmd = Command(db,
            "SELECT e.wallet_kind, e.wallet_owner_id, SUM(e.amount) FROM ledger_entries e JOIN ledger_tx t ON t.id = e.tx_id " +
            "WHERE t.session_id = $s GROUP BY e.wallet_kind, e.wallet_owner_id", sessionId))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                sums[new WalletId(ParseKind(reader.GetString(0)), reader.GetInt64(1))] = reader.GetInt64(2);
            }
        }

        var balances = new Dictionary<WalletId, long>();
        using (var cmd = Command(db, "SELECT kind, owner_id, balance FROM wallets WHERE session_id = $s", sessionId))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                balances[new WalletId(ParseKind(reader.GetString(0)), reader.GetInt64(1))] = reader.GetInt64(2);
            }
        }

        return new LedgerAuditData(unbalanced, sums, balances);
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, long sessionId)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$s", sessionId);
        return cmd;
    }

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string KindName(WalletKind kind) => kind switch
    {
        WalletKind.Player => "player",
        WalletKind.TeamShared => "team_shared",
        WalletKind.TeamPool => "team_pool",
        WalletKind.Escrow => "escrow",
        WalletKind.World => "world",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static WalletKind ParseKind(string name) => name switch
    {
        "player" => WalletKind.Player,
        "team_shared" => WalletKind.TeamShared,
        "team_pool" => WalletKind.TeamPool,
        "escrow" => WalletKind.Escrow,
        "world" => WalletKind.World,
        _ => throw new InvalidOperationException($"Unknown wallet kind '{name}' in the database."),
    };
}
