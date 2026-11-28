using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/debts")]
public sealed class DebtsController : ControllerBase
{
    private static DateTime BaghdadNow() => DateTime.UtcNow.AddHours(3);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? status = null, [FromQuery] string? type = null)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        string sql = @"
            SELECT DebtID, DebtorName, Phone, DebtType, OriginalAmount, PaidAmount, RemainingAmount,
                   Reason, DueDate, Status, CreatedBy, CreatedAt, UpdatedAt
            FROM debts
            WHERE (@status IS NULL OR @status = '' OR @status = 'All' OR Status = @status)
              AND (@type IS NULL OR @type = '' OR @type = 'All' OR DebtType = @type)
            ORDER BY FIELD(Status, 'Open', 'Partial', 'Paid'), CreatedAt DESC;";

        var list = new List<object>();
        decimal totalOriginal = 0m, totalPaid = 0m, totalRemaining = 0m;
        int openCount = 0;

        await using (var cmd = new MySqlCommand(sql, c))
        {
            cmd.Parameters.AddWithValue("@status", (object?)status ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                decimal orig = r.GetDecimal("OriginalAmount");
                decimal paid = r.GetDecimal("PaidAmount");
                decimal rem = r.GetDecimal("RemainingAmount");
                string st = r.GetString("Status");
                totalOriginal += orig;
                totalPaid += paid;
                totalRemaining += rem;
                if (st != "Paid") openCount++;

                list.Add(new
                {
                    debtID = r.GetInt32("DebtID"),
                    debtorName = r.GetString("DebtorName"),
                    phone = r.IsDBNull(r.GetOrdinal("Phone")) ? "" : r.GetString("Phone"),
                    debtType = r.GetString("DebtType"),
                    originalAmount = orig,
                    paidAmount = paid,
                    remainingAmount = rem,
                    reason = r.IsDBNull(r.GetOrdinal("Reason")) ? "" : r.GetString("Reason"),
                    dueDate = r.IsDBNull(r.GetOrdinal("DueDate")) ? (DateTime?)null : r.GetDateTime("DueDate"),
                    status = st,
                    createdBy = r.IsDBNull(r.GetOrdinal("CreatedBy")) ? "Manager" : r.GetString("CreatedBy"),
                    createdAt = r.GetDateTime("CreatedAt"),
                    updatedAt = r.GetDateTime("UpdatedAt")
                });
            }
        }

        return Ok(new
        {
            success = true,
            summary = new { totalOriginal, totalPaid, totalRemaining, openCount, totalCount = list.Count },
            debts = list
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDebtRequest req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (req == null || string.IsNullOrWhiteSpace(req.DebtorName) || req.Amount <= 0)
            return BadRequest(new { success = false, message = "يرجى إدخال اسم صاحب الدين والمبلغ بشكل صحيح." });

        string debtType = string.IsNullOrWhiteSpace(req.DebtType) ? "Customer" : req.DebtType.Trim();
        if (!new[] { "Customer", "Supplier", "Employee", "Other" }.Contains(debtType))
            debtType = "Customer";

        string actor = Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager";
        DateTime now = BaghdadNow();

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = @"
            INSERT INTO debts (DebtorName, Phone, DebtType, OriginalAmount, PaidAmount, RemainingAmount, Reason, DueDate, Status, CreatedBy, CreatedAt, UpdatedAt)
            VALUES (@name, @phone, @type, @amt, 0, @amt, @reason, @due, 'Open', @by, @now, @now);
            SELECT LAST_INSERT_ID();";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@name", req.DebtorName.Trim());
        cmd.Parameters.AddWithValue("@phone", string.IsNullOrWhiteSpace(req.Phone) ? DBNull.Value : req.Phone.Trim());
        cmd.Parameters.AddWithValue("@type", debtType);
        cmd.Parameters.AddWithValue("@amt", req.Amount);
        cmd.Parameters.AddWithValue("@reason", string.IsNullOrWhiteSpace(req.Reason) ? DBNull.Value : req.Reason.Trim());
        cmd.Parameters.AddWithValue("@due", (object?)req.DueDate ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@by", actor);
        cmd.Parameters.AddWithValue("@now", now);

        int id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return Ok(new { success = true, debtID = id, message = "تم تسجيل الدين بنجاح." });
    }

    [HttpPost("{id:int}/pay")]
    public async Task<IActionResult> RecordPayment(int id, [FromBody] DebtPaymentRequest req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (req == null || req.Amount <= 0)
            return BadRequest(new { success = false, message = "يرجى إدخال مبلغ تسديد صحيح." });

        string actor = Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager";
        DateTime now = BaghdadNow();

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();

        decimal orig = 0m, paid = 0m;
        await using (var sel = new MySqlCommand("SELECT OriginalAmount, PaidAmount FROM debts WHERE DebtID=@id LIMIT 1;", c, tx))
        {
            sel.Parameters.AddWithValue("@id", id);
            await using var r = await sel.ExecuteReaderAsync();
            if (!await r.ReadAsync())
            {
                await tx.RollbackAsync();
                return NotFound(new { success = false, message = "سجل الدين غير موجود." });
            }
            orig = r.GetDecimal("OriginalAmount");
            paid = r.GetDecimal("PaidAmount");
        }

        decimal newPaid = Math.Min(orig, paid + req.Amount);
        decimal newRem = Math.Max(0m, orig - newPaid);
        string newStatus = newRem <= 0m ? "Paid" : "Partial";

        await using (var ins = new MySqlCommand(
            "INSERT INTO debtpayments (DebtID, Amount, PaymentMethod, Notes, RecordedBy, CreatedAt) VALUES (@id, @amt, @method, @notes, @by, @now);", c, tx))
        {
            ins.Parameters.AddWithValue("@id", id);
            ins.Parameters.AddWithValue("@amt", req.Amount);
            ins.Parameters.AddWithValue("@method", string.IsNullOrWhiteSpace(req.PaymentMethod) ? "Cash" : req.PaymentMethod.Trim());
            ins.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(req.Notes) ? DBNull.Value : req.Notes.Trim());
            ins.Parameters.AddWithValue("@by", actor);
            ins.Parameters.AddWithValue("@now", now);
            await ins.ExecuteNonQueryAsync();
        }

        await using (var upd = new MySqlCommand(
            "UPDATE debts SET PaidAmount=@paid, RemainingAmount=@rem, Status=@st, UpdatedAt=@now WHERE DebtID=@id;", c, tx))
        {
            upd.Parameters.AddWithValue("@paid", newPaid);
            upd.Parameters.AddWithValue("@rem", newRem);
            upd.Parameters.AddWithValue("@st", newStatus);
            upd.Parameters.AddWithValue("@now", now);
            upd.Parameters.AddWithValue("@id", id);
            await upd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
        return Ok(new { success = true, paidAmount = newPaid, remainingAmount = newRem, status = newStatus, message = newStatus == "Paid" ? "تم تسديد الدين بالكامل." : "تم تسجيل الدفعة بنجاح." });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await using (var delP = new MySqlCommand("DELETE FROM debtpayments WHERE DebtID=@id;", c))
        {
            delP.Parameters.AddWithValue("@id", id);
            await delP.ExecuteNonQueryAsync();
        }
        await using var del = new MySqlCommand("DELETE FROM debts WHERE DebtID=@id;", c);
        del.Parameters.AddWithValue("@id", id);
        int affected = await del.ExecuteNonQueryAsync();
        return affected == 0 ? NotFound(new { success = false }) : Ok(new { success = true, message = "تم حذف سجل الدين." });
    }

    public sealed class CreateDebtRequest
    {
        public string DebtorName { get; set; } = "";
        public string? Phone { get; set; }
        public string? DebtType { get; set; } = "Customer";
        public decimal Amount { get; set; }
        public string? Reason { get; set; }
        public DateTime? DueDate { get; set; }
    }

    public sealed class DebtPaymentRequest
    {
        public decimal Amount { get; set; }
        public string? PaymentMethod { get; set; } = "Cash";
        public string? Notes { get; set; }
    }
}
