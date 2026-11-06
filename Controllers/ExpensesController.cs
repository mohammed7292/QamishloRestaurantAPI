using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/expenses")]
public sealed class ExpensesController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string period = "daily", [FromQuery] string? date = null, [FromQuery] int? year = null, [FromQuery] int? month = null)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        await using var connection = DatabaseManager.GetConnection(); await connection.OpenAsync();
        string where; var cmd = new MySqlCommand(); cmd.Connection = connection;
        if (period.Equals("monthly", StringComparison.OrdinalIgnoreCase))
        {
            var y = year ?? BaghdadNow().Year; var m = month ?? BaghdadNow().Month;
            where = "YEAR(BusinessDate)=@year AND MONTH(BusinessDate)=@month";
            cmd.Parameters.AddWithValue("@year", y); cmd.Parameters.AddWithValue("@month", m);
        }
        else
        {
            DateTime businessDate = DateTime.TryParse(date, out var parsed) ? parsed.Date : BusinessDate(BaghdadNow());
            where = "BusinessDate=@date"; cmd.Parameters.AddWithValue("@date", businessDate);
        }
        cmd.CommandText = $"SELECT ExpenseID,UserID,ExpenseType,ExpenseName,Quantity,UnitPrice,Amount,ExpenseDate,PaymentMethod,ReferenceNumber,Notes,BusinessDate FROM Expenses WHERE {where} ORDER BY ExpenseDate DESC";
        var items = new List<object>(); decimal total = 0;
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var amount = r.GetDecimal("Amount"); total += amount;
                items.Add(new { expenseID=r.GetInt32("ExpenseID"), userID=r.GetInt32("UserID"), expenseType=r.GetString("ExpenseType"), expenseName=r.GetString("ExpenseName"), quantity=r.GetDecimal("Quantity"), unitPrice=r.GetDecimal("UnitPrice"), amount, expenseDate=r.GetDateTime("ExpenseDate"), paymentMethod=r.GetString("PaymentMethod"), referenceNumber=r.IsDBNull(r.GetOrdinal("ReferenceNumber"))?null:r.GetString("ReferenceNumber"), notes=r.IsDBNull(r.GetOrdinal("Notes"))?null:r.GetString("Notes"), businessDate=r.GetDateTime("BusinessDate") });
            }
        }
        return Ok(new { success = true, period, total, count = items.Count, expenses = items });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ExpenseRequest request)
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
        if (managerUserId is null) return Unauthorized(new { success = false });
        if (string.IsNullOrWhiteSpace(request.ExpenseName) || request.Quantity <= 0 || request.UnitPrice < 0 || request.Amount <= 0) return BadRequest(new { success = false, message = "بيانات المصروف غير صحيحة." });
        await using var connection = DatabaseManager.GetConnection(); await connection.OpenAsync();
        const string sql = "INSERT INTO Expenses(UserID,ExpenseType,ExpenseName,Quantity,UnitPrice,Amount,ExpenseDate,BusinessDate,PaymentMethod,ReferenceNumber,Notes) VALUES(@user,@type,@name,@qty,@price,@amount,@date,@business,@method,@reference,@notes); SELECT LAST_INSERT_ID();";
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@user", managerUserId.Value); cmd.Parameters.AddWithValue("@type", request.ExpenseType); cmd.Parameters.AddWithValue("@name", request.ExpenseName.Trim()); cmd.Parameters.AddWithValue("@qty", request.Quantity); cmd.Parameters.AddWithValue("@price", request.UnitPrice); cmd.Parameters.AddWithValue("@amount", request.Amount); var dt = request.ExpenseDate is DateTime suppliedDate ? ToBaghdadTime(suppliedDate) : BaghdadNow(); cmd.Parameters.AddWithValue("@date", dt); cmd.Parameters.AddWithValue("@business", BusinessDate(dt)); cmd.Parameters.AddWithValue("@method", request.PaymentMethod); cmd.Parameters.AddWithValue("@reference", (object?)request.ReferenceNumber ?? DBNull.Value); cmd.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync()); return Ok(new { success = true, expenseID = id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] ExpenseRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        if (id <= 0 || string.IsNullOrWhiteSpace(request.ExpenseName) || request.Quantity <= 0 || request.UnitPrice < 0 || request.Amount <= 0) return BadRequest(new { success = false });
        await using var connection = DatabaseManager.GetConnection(); await connection.OpenAsync();
        const string sql = "UPDATE Expenses SET ExpenseType=@type,ExpenseName=@name,Quantity=@qty,UnitPrice=@price,Amount=@amount,ExpenseDate=@date,BusinessDate=@business,PaymentMethod=@method,ReferenceNumber=@reference,Notes=@notes WHERE ExpenseID=@id";
        await using var cmd = new MySqlCommand(sql, connection); cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@type", request.ExpenseType); cmd.Parameters.AddWithValue("@name", request.ExpenseName.Trim()); cmd.Parameters.AddWithValue("@qty", request.Quantity); cmd.Parameters.AddWithValue("@price", request.UnitPrice); cmd.Parameters.AddWithValue("@amount", request.Amount); var dt=request.ExpenseDate is DateTime suppliedDate ? ToBaghdadTime(suppliedDate) : BaghdadNow(); cmd.Parameters.AddWithValue("@date",dt); cmd.Parameters.AddWithValue("@business",BusinessDate(dt)); cmd.Parameters.AddWithValue("@method",request.PaymentMethod); cmd.Parameters.AddWithValue("@reference",(object?)request.ReferenceNumber??DBNull.Value); cmd.Parameters.AddWithValue("@notes",(object?)request.Notes??DBNull.Value);
        return await cmd.ExecuteNonQueryAsync() == 0 ? NotFound(new { success = false }) : Ok(new { success = true });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        await using var connection=DatabaseManager.GetConnection(); await connection.OpenAsync(); await using var cmd=new MySqlCommand("DELETE FROM Expenses WHERE ExpenseID=@id",connection); cmd.Parameters.AddWithValue("@id",id);
        return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true});
    }

    public static DateTime BaghdadNow()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Arabic Standard Time"); }
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
    }

    private static DateTime ToBaghdadTime(DateTime value)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Baghdad"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Arabic Standard Time"); }
        if (value.Kind == DateTimeKind.Utc) return TimeZoneInfo.ConvertTimeFromUtc(value, zone);
        if (value.Kind == DateTimeKind.Local) return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.Local, zone);
        return value; // Unspecified values from a manager are treated as Baghdad wall-clock time.
    }

    public static DateTime BusinessDate(DateTime value) => value.TimeOfDay < TimeSpan.FromHours(2) ? value.Date.AddDays(-1) : value.Date;
}

public sealed class ExpenseRequest
{
    public int UserID { get; set; } = 1;
    public string ExpenseType { get; set; } = "Other";
    public string ExpenseName { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
    public DateTime? ExpenseDate { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public string? ReferenceNumber { get; set; }
    public string? Notes { get; set; }
}
