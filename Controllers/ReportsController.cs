using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    [HttpGet("daily")]
    public Task<IActionResult> Daily([FromQuery] string? date = null) => BuildAsync(false, date, null, null);

    [HttpGet("monthly")]
    public Task<IActionResult> Monthly([FromQuery] int? year = null, [FromQuery] int? month = null) => BuildAsync(true, null, year, month);

    private async Task<IActionResult> BuildAsync(bool monthly, string? date, int? year, int? month)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        await using var connection = DatabaseManager.GetConnection(); await connection.OpenAsync();
        DateTime businessDate = DateTime.TryParse(date, out var parsed) ? parsed.Date : ExpensesController.BusinessDate(ExpensesController.BaghdadNow());
        decimal sales, expenses; int orderCount;
        if (monthly)
        {
            int y = year ?? ExpensesController.BaghdadNow().Year, m = month ?? ExpensesController.BaghdadNow().Month;
            const string salesSql = "SELECT COALESCE(SUM(TotalAmount),0),COUNT(*) FROM orders WHERE Status <> 'Cancelled' AND YEAR(BusinessDate)=@y AND MONTH(BusinessDate)=@m";
            await using var sc = new MySqlCommand(salesSql, connection); sc.Parameters.AddWithValue("@y",y); sc.Parameters.AddWithValue("@m",m);
            await using (var sr=await sc.ExecuteReaderAsync()) { await sr.ReadAsync(); sales=sr.GetDecimal(0); orderCount=sr.GetInt32(1); }
            await using var ec = new MySqlCommand("SELECT COALESCE(SUM(Amount),0) FROM expenses WHERE YEAR(BusinessDate)=@y AND MONTH(BusinessDate)=@m",connection); ec.Parameters.AddWithValue("@y",y); ec.Parameters.AddWithValue("@m",m); expenses=Convert.ToDecimal(await ec.ExecuteScalarAsync());
            return Ok(new { success=true, period="monthly", year=y, month=m, sales, expenses, difference=sales-expenses, orderCount, currency="IQD", businessDayStartsAt="02:00" });
        }
        else
        {
            const string salesSql = "SELECT COALESCE(SUM(TotalAmount),0),COUNT(*) FROM orders WHERE Status <> 'Cancelled' AND BusinessDate=@d";
            await using var sc = new MySqlCommand(salesSql, connection); sc.Parameters.AddWithValue("@d",businessDate);
            await using (var sr=await sc.ExecuteReaderAsync()) { await sr.ReadAsync(); sales=sr.GetDecimal(0); orderCount=sr.GetInt32(1); }
            await using var ec = new MySqlCommand("SELECT COALESCE(SUM(Amount),0) FROM expenses WHERE BusinessDate=@d",connection); ec.Parameters.AddWithValue("@d",businessDate); expenses=Convert.ToDecimal(await ec.ExecuteScalarAsync());
            return Ok(new { success=true, period="daily", date=businessDate, sales, expenses, difference=sales-expenses, orderCount, currency="IQD", businessDayStartsAt="02:00" });
        }
    }
}
