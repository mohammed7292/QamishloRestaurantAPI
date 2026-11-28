using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/finance")]
public sealed class FinanceController : ControllerBase
{
    private static async Task<(decimal CashSales, decimal CashIn, decimal CashOut, decimal Expenses, decimal Refunds)> ComputeSessionTotalsAsync(MySqlConnection conn, long sessionId, DateTime openedAt)
    {
        decimal cashSales = 0, cashIn = 0, cashOut = 0, expenses = 0, refunds = 0;

        await using (var sCmd = new MySqlCommand(@"
            SELECT COALESCE(SUM(TotalAmount), 0)
            FROM orders
            WHERE Status NOT IN ('Cancelled','Rejected') AND OrderDate >= @since;", conn))
        {
            sCmd.Parameters.AddWithValue("@since", openedAt);
            cashSales = Convert.ToDecimal(await sCmd.ExecuteScalarAsync() ?? 0m);
        }

        await using (var mCmd = new MySqlCommand(@"
            SELECT MovementType, COALESCE(SUM(Amount), 0)
            FROM cashmovements
            WHERE CashSessionID = @sid
            GROUP BY MovementType;", conn))
        {
            mCmd.Parameters.AddWithValue("@sid", sessionId);
            await using var r = await mCmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string mt = r.GetString(0);
                decimal amt = Convert.ToDecimal(r.GetValue(1));
                if (mt is "CashIn" or "Deposit") cashIn += amt;
                else if (mt is "CashOut" or "Withdrawal") cashOut += amt;
                else if (mt == "Expense") expenses += amt;
                else if (mt == "Refund") refunds += amt;
            }
        }

        try
        {
            await using var eCmd = new MySqlCommand("SELECT COALESCE(SUM(Amount), 0) FROM expenses WHERE ExpenseDate >= @since;", conn);
            eCmd.Parameters.AddWithValue("@since", openedAt);
            expenses += Convert.ToDecimal(await eCmd.ExecuteScalarAsync() ?? 0m);
        }
        catch { }

        return (cashSales, cashIn, cashOut, expenses, refunds);
    }

    [HttpGet("cash-session/current")]
    public async Task<IActionResult> GetCurrentCashSession()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT CashSessionID, OpenedBy, OpenedAt, OpeningBalance, Notes FROM cashsessions WHERE Status = 'Open' ORDER BY CashSessionID DESC LIMIT 1;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return Ok(new { success = true, isOpen = false });

        long sid = r.GetInt64("CashSessionID");
        string openedBy = r.GetString("OpenedBy");
        DateTime openedAt = r.GetDateTime("OpenedAt");
        decimal opening = r.GetDecimal("OpeningBalance");
        string notes = r.IsDBNull(r.GetOrdinal("Notes")) ? "" : r.GetString("Notes");
        await r.CloseAsync();

        var totals = await ComputeSessionTotalsAsync(conn, sid, openedAt);
        decimal expected = opening + totals.CashSales + totals.CashIn - totals.CashOut - totals.Expenses - totals.Refunds;

        return Ok(new
        {
            success = true,
            isOpen = true,
            cashSessionID = sid,
            openedBy,
            openedAt,
            openingBalance = opening,
            cashSales = totals.CashSales,
            cashIn = totals.CashIn,
            cashOut = totals.CashOut,
            expenses = totals.Expenses,
            refunds = totals.Refunds,
            expectedCash = expected,
            notes
        });
    }

    [HttpPost("cash-session/open")]
    public async Task<IActionResult> OpenCashSession([FromBody] OpenCashSessionDto req)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using (var chk = new MySqlCommand("SELECT COUNT(*) FROM cashsessions WHERE Status = 'Open';", conn))
            if (Convert.ToInt64(await chk.ExecuteScalarAsync()) > 0)
                return Conflict(new { success = false, message = "يوجد صندوق مفتوح حالياً. يرجى إغلاقه قبل فتح وردية جديدة." });

        string actor = string.IsNullOrWhiteSpace(req.OpenedBy) ? (Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager") : req.OpenedBy.Trim();
        await using var cmd = new MySqlCommand(@"
            INSERT INTO cashsessions (OpenedBy, OpenedAt, OpeningBalance, ExpectedCash, Status, Notes)
            VALUES (@by, NOW(), @bal, @bal, 'Open', @notes);
            SELECT LAST_INSERT_ID();", conn);
        cmd.Parameters.AddWithValue("@by", actor);
        cmd.Parameters.AddWithValue("@bal", Math.Max(0m, req.OpeningBalance));
        cmd.Parameters.AddWithValue("@notes", (object?)req.Notes ?? DBNull.Value);
        long sid = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        await AuditController.WriteLogAsync(conn, actor, "Manager", "OpenCashSession", "CashSession", sid.ToString(), $"OpeningBalance={req.OpeningBalance:N0}");
        return Ok(new { success = true, cashSessionID = sid, message = "تم فتح الصندوق بنجاح." });
    }

    [HttpPost("cash-session/movement")]
    public async Task<IActionResult> AddCashMovement([FromBody] CashMovementDto req)
    {
        if (req.Amount <= 0) return BadRequest(new { success = false, message = "المبلغ يجب أن يكون أكبر من صفر." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();

        long sid = req.CashSessionID ?? 0;
        if (sid <= 0)
        {
            await using var getOpen = new MySqlCommand("SELECT CashSessionID FROM cashsessions WHERE Status = 'Open' ORDER BY CashSessionID DESC LIMIT 1;", conn);
            var obj = await getOpen.ExecuteScalarAsync();
            if (obj == null || obj == DBNull.Value)
                return BadRequest(new { success = false, message = "لا يوجد صندوق مفتوح حالياً." });
            sid = Convert.ToInt64(obj);
        }

        string actor = string.IsNullOrWhiteSpace(req.CreatedBy) ? (Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager") : req.CreatedBy.Trim();
        string mType = string.IsNullOrWhiteSpace(req.MovementType) ? "CashIn" : req.MovementType.Trim();

        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new MySqlCommand(@"
            INSERT INTO cashmovements (CashSessionID, MovementType, Amount, Description, CreatedBy, CreatedAt)
            VALUES (@sid, @mt, @amt, @desc, @by, NOW());", conn, tx))
        {
            cmd.Parameters.AddWithValue("@sid", sid);
            cmd.Parameters.AddWithValue("@mt", mType);
            cmd.Parameters.AddWithValue("@amt", req.Amount);
            cmd.Parameters.AddWithValue("@desc", (object?)req.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@by", actor);
            await cmd.ExecuteNonQueryAsync();
        }
        bool isIn = mType is "CashIn" or "Deposit";
        await using (var led = new MySqlCommand(@"
            INSERT INTO financialledger (TransactionType, DebitAccount, CreditAccount, Amount, ReferenceID, ReferenceType, Description, CreatedBy, AccountingDate, CashSessionID)
            VALUES (@tt, @dr, @cr, @amt, @sid, 'CashMovement', @desc, @by, CURDATE(), @sid);", conn, tx))
        {
            led.Parameters.AddWithValue("@tt", mType);
            led.Parameters.AddWithValue("@dr", isIn ? "Cash" : "OwnerDrawOrExpense");
            led.Parameters.AddWithValue("@cr", isIn ? "OwnerDeposit" : "Cash");
            led.Parameters.AddWithValue("@amt", req.Amount);
            led.Parameters.AddWithValue("@sid", sid);
            led.Parameters.AddWithValue("@desc", (object?)req.Description ?? mType);
            led.Parameters.AddWithValue("@by", actor);
            await led.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        await AuditController.WriteLogAsync(conn, actor, "Manager", "CashMovement", "CashSession", sid.ToString(), $"{mType}: {req.Amount:N0}");
        return Ok(new { success = true, message = "تم تسجيل الحركة المالية في الصندوق والدفتر." });
    }

    [HttpPost("cash-session/close")]
    public async Task<IActionResult> CloseCashSession([FromBody] CloseCashSessionDto req)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var getCmd = new MySqlCommand("SELECT CashSessionID, OpenedAt, OpeningBalance, OpenedBy FROM cashsessions WHERE Status = 'Open' ORDER BY CashSessionID DESC LIMIT 1;", conn);
        await using var r = await getCmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return BadRequest(new { success = false, message = "لا يوجد صندوق مفتوح لإغلاقه." });

        long sid = r.GetInt64(0);
        DateTime openedAt = r.GetDateTime(1);
        decimal opening = r.GetDecimal(2);
        string openedBy = r.GetString(3);
        await r.CloseAsync();

        var totals = await ComputeSessionTotalsAsync(conn, sid, openedAt);
        decimal expected = opening + totals.CashSales + totals.CashIn - totals.CashOut - totals.Expenses - totals.Refunds;
        decimal diff = req.ActualCash - expected;
        string actor = string.IsNullOrWhiteSpace(req.ClosedBy) ? (Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager") : req.ClosedBy.Trim();

        await using var upd = new MySqlCommand(@"
            UPDATE cashsessions
            SET ExpectedCash = @exp, ActualCash = @act, CashDifference = @diff, ClosedBy = @by, ClosedAt = NOW(), Status = 'Closed', Notes = COALESCE(@notes, Notes)
            WHERE CashSessionID = @sid;", conn);
        upd.Parameters.AddWithValue("@exp", expected);
        upd.Parameters.AddWithValue("@act", req.ActualCash);
        upd.Parameters.AddWithValue("@diff", diff);
        upd.Parameters.AddWithValue("@by", actor);
        upd.Parameters.AddWithValue("@notes", (object?)req.Notes ?? DBNull.Value);
        upd.Parameters.AddWithValue("@sid", sid);
        await upd.ExecuteNonQueryAsync();

        await AuditController.WriteLogAsync(conn, actor, "Manager", "CloseCashSession", "CashSession", sid.ToString(), $"Expected={expected:N0}, Actual={req.ActualCash:N0}, Diff={diff:N0}");
        return Ok(new
        {
            success = true,
            cashSessionID = sid,
            openedBy,
            closedBy = actor,
            openingBalance = opening,
            cashSales = totals.CashSales,
            expenses = totals.Expenses,
            cashIn = totals.CashIn,
            cashOut = totals.CashOut,
            expectedCash = expected,
            actualCash = req.ActualCash,
            cashDifference = diff,
            message = "تم إغلاق الصندوق وإصدار تقرير المطابقة النقدية بنجاح."
        });
    }

    [HttpGet("cash-sessions")]
    public async Task<IActionResult> GetCashSessions()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var list = new List<object>();
        await using var cmd = new MySqlCommand("SELECT CashSessionID, OpenedBy, OpenedAt, OpeningBalance, ExpectedCash, ActualCash, CashDifference, ClosedBy, ClosedAt, Status FROM cashsessions ORDER BY CashSessionID DESC LIMIT 40;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new
            {
                cashSessionID = r.GetInt64(0),
                openedBy = r.GetString(1),
                openedAt = r.GetDateTime(2),
                openingBalance = r.GetDecimal(3),
                expectedCash = r.GetDecimal(4),
                actualCash = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5),
                cashDifference = r.IsDBNull(6) ? (decimal?)null : r.GetDecimal(6),
                closedBy = r.IsDBNull(7) ? "" : r.GetString(7),
                closedAt = r.IsDBNull(8) ? (DateTime?)null : r.GetDateTime(8),
                status = r.GetString(9)
            });
        }
        return Ok(new { success = true, sessions = list });
    }

    [HttpGet("profit-loss")]
    public async Task<IActionResult> GetRealProfitLoss([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();

        decimal grossSales = 0, discounts = 0, refunds = 0, netSales = 0, cogs = 0, opExpenses = 0;
        int ordersCount = 0;

        await using (var cmd = new MySqlCommand(@"
            SELECT
                COUNT(CASE WHEN Status NOT IN ('Cancelled','Rejected') THEN 1 END),
                COALESCE(SUM(CASE WHEN Status NOT IN ('Cancelled','Rejected') THEN Subtotal + DeliveryFee ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN Status NOT IN ('Cancelled','Rejected') THEN DiscountAmount ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN Status IN ('Cancelled','Rejected') THEN TotalAmount ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN Status NOT IN ('Cancelled','Rejected') THEN TotalAmount ELSE 0 END), 0)
            FROM orders
            WHERE OrderDate >= DATE_SUB(NOW(), INTERVAL @d DAY);", conn))
        {
            cmd.Parameters.AddWithValue("@d", days);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                ordersCount = Convert.ToInt32(r.GetValue(0));
                grossSales = Convert.ToDecimal(r.GetValue(1));
                discounts = Convert.ToDecimal(r.GetValue(2));
                refunds = Convert.ToDecimal(r.GetValue(3));
                netSales = Convert.ToDecimal(r.GetValue(4));
            }
        }

        try
        {
            await using var cogsCmd = new MySqlCommand(@"
                SELECT COALESCE(SUM(TotalCOGS), 0)
                FROM inventoryorderdeductions
                WHERE IsReversed = 0 AND DeductedAt >= DATE_SUB(NOW(), INTERVAL @d DAY);", conn);
            cogsCmd.Parameters.AddWithValue("@d", days);
            cogs = Convert.ToDecimal(await cogsCmd.ExecuteScalarAsync() ?? 0m);
        }
        catch { }

        try
        {
            await using var expCmd = new MySqlCommand("SELECT COALESCE(SUM(Amount), 0) FROM expenses WHERE ExpenseDate >= DATE_SUB(NOW(), INTERVAL @d DAY);", conn);
            expCmd.Parameters.AddWithValue("@d", days);
            opExpenses = Convert.ToDecimal(await expCmd.ExecuteScalarAsync() ?? 0m);
        }
        catch { }

        decimal grossProfit = netSales - cogs;
        decimal netOperatingProfit = grossProfit - opExpenses;

        return Ok(new
        {
            success = true,
            days,
            ordersCount,
            grossSales,
            discounts,
            refunds,
            netSales,
            costOfGoodsSold = cogs,
            grossProfit,
            operatingExpenses = opExpenses,
            netOperatingProfit
        });
    }

    [HttpPost("ledger/reverse/{transactionId:long}")]
    public async Task<IActionResult> ReverseLedgerEntry(long transactionId, [FromBody] ReverseLedgerDto req)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var get = new MySqlCommand("SELECT TransactionType, DebitAccount, CreditAccount, Amount, CashSessionID FROM financialledger WHERE TransactionID = @id LIMIT 1;", conn);
        get.Parameters.AddWithValue("@id", transactionId);
        await using var r = await get.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return NotFound(new { success = false, message = "الحركة المالية غير موجودة." });
        string tt = r.GetString(0), dr = r.GetString(1), cr = r.GetString(2);
        decimal amt = r.GetDecimal(3);
        long? sid = r.IsDBNull(4) ? null : r.GetInt64(4);
        await r.CloseAsync();

        string actor = Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager";
        await using var ins = new MySqlCommand(@"
            INSERT INTO financialledger (TransactionType, DebitAccount, CreditAccount, Amount, ReferenceID, ReferenceType, ReversesTransactionID, Description, CreatedBy, AccountingDate, CashSessionID)
            VALUES ('Reversal', @cr, @dr, @amt, @orig, 'LedgerReversal', @orig, @desc, @by, CURDATE(), @sid);", conn);
        ins.Parameters.AddWithValue("@cr", cr);
        ins.Parameters.AddWithValue("@dr", dr);
        ins.Parameters.AddWithValue("@amt", amt);
        ins.Parameters.AddWithValue("@orig", transactionId);
        ins.Parameters.AddWithValue("@desc", string.IsNullOrWhiteSpace(req.Reason) ? $"عكس قيد #{transactionId} ({tt})" : req.Reason.Trim());
        ins.Parameters.AddWithValue("@by", actor);
        ins.Parameters.AddWithValue("@sid", (object?)sid ?? DBNull.Value);
        await ins.ExecuteNonQueryAsync();

        await AuditController.WriteLogAsync(conn, actor, "Manager", "ReverseLedgerEntry", "FinancialLedger", transactionId.ToString(), req.Reason);
        return Ok(new { success = true, message = "تم إنشاء قيد عكسي موثق بنجاح دون تعديل السجل التاريخي." });
    }
}

public sealed class OpenCashSessionDto { public decimal OpeningBalance { get; set; } public string? OpenedBy { get; set; } public string? Notes { get; set; } }
public sealed class CashMovementDto { public long? CashSessionID { get; set; } public string MovementType { get; set; } = "CashIn"; public decimal Amount { get; set; } public string? Description { get; set; } public string? CreatedBy { get; set; } }
public sealed class CloseCashSessionDto { public decimal ActualCash { get; set; } public string? ClosedBy { get; set; } public string? Notes { get; set; } }
public sealed class ReverseLedgerDto { public string? Reason { get; set; } }
