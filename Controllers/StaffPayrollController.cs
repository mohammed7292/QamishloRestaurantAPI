using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/staff")]
public sealed class StaffPayrollController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetEmployees()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = @"
            SELECT EmployeeID, FullName, JobTitleEN, JobTitleAR, JobTitleKU,
                   PhotoURL, Salary, IsActive, ShowInCustomerApp
            FROM restaurantemployees
            ORDER BY FullName";
        await using var cmd = new MySqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<object>();
        while (await r.ReadAsync())
        {
            list.Add(new
            {
                employeeID = r.GetInt32("EmployeeID"),
                fullName = r.GetString("FullName"),
                jobTitleEN = r.IsDBNull(r.GetOrdinal("JobTitleEN")) ? null : r.GetString("JobTitleEN"),
                jobTitleAR = r.IsDBNull(r.GetOrdinal("JobTitleAR")) ? null : r.GetString("JobTitleAR"),
                jobTitleKU = r.IsDBNull(r.GetOrdinal("JobTitleKU")) ? null : r.GetString("JobTitleKU"),
                photoURL = r.IsDBNull(r.GetOrdinal("PhotoURL")) ? null : r.GetString("PhotoURL"),
                salary = r.GetDecimal("Salary"),
                isActive = r.GetBoolean("IsActive"),
                showInCustomerApp = r.GetBoolean("ShowInCustomerApp")
            });
        }
        return Ok(new { success = true, employees = list });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] StaffRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        if (string.IsNullOrWhiteSpace(x.FullName) || x.Salary < 0) return BadRequest(new { success = false });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        string? normalizedPhoto = await EmployeeManagementController.NormalizeAndPersistPhotoUrlAsync(c, x.PhotoURL);

        const string sql = @"
            INSERT INTO restaurantemployees
                (FullName, JobTitleEN, JobTitleAR, JobTitleKU, DescriptionEN, DescriptionAR, DescriptionKU, PhotoURL, Salary, IsActive, ShowInCustomerApp)
            VALUES
                (@name, @en, @ar, @ku, @den, @dar, @dku, @photo, @salary, @active, @show);
            SELECT LAST_INSERT_ID();";
        await using var cmd = new MySqlCommand(sql, c);
        string defaultTitleAr = string.IsNullOrWhiteSpace(x.JobTitleAR) ? "موظف مطعم قامشلو" : x.JobTitleAR.Trim();
        string defaultTitleEn = string.IsNullOrWhiteSpace(x.JobTitleEN) ? defaultTitleAr : x.JobTitleEN.Trim();
        string defaultTitleKu = string.IsNullOrWhiteSpace(x.JobTitleKU) ? defaultTitleAr : x.JobTitleKU.Trim();

        cmd.Parameters.AddWithValue("@name", x.FullName.Trim());
        cmd.Parameters.AddWithValue("@en", defaultTitleEn);
        cmd.Parameters.AddWithValue("@ar", defaultTitleAr);
        cmd.Parameters.AddWithValue("@ku", defaultTitleKu);
        cmd.Parameters.AddWithValue("@den", (object?)x.DescriptionEN ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dar", (object?)x.DescriptionAR ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dku", (object?)x.DescriptionKU ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@photo", string.IsNullOrWhiteSpace(normalizedPhoto) ? DBNull.Value : normalizedPhoto);
        cmd.Parameters.AddWithValue("@salary", x.Salary);
        cmd.Parameters.AddWithValue("@active", x.IsActive);
        cmd.Parameters.AddWithValue("@show", x.ShowInCustomerApp);
        return Ok(new { success = true, employeeID = Convert.ToInt32(await cmd.ExecuteScalarAsync()), photoURL = normalizedPhoto });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] StaffRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        if (id <= 0 || string.IsNullOrWhiteSpace(x.FullName) || x.Salary < 0) return BadRequest(new { success = false });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        string? normalizedPhoto = await EmployeeManagementController.NormalizeAndPersistPhotoUrlAsync(c, x.PhotoURL);

        const string sql = @"
            UPDATE restaurantemployees
            SET FullName=@name,
                JobTitleEN=COALESCE(@en, JobTitleEN),
                JobTitleAR=COALESCE(@ar, JobTitleAR),
                JobTitleKU=COALESCE(@ku, JobTitleKU),
                PhotoURL=COALESCE(@photo, PhotoURL),
                Salary=@salary,
                IsActive=@active,
                ShowInCustomerApp=@show,
                UpdatedAt=CURRENT_TIMESTAMP
            WHERE EmployeeID=@id";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@name", x.FullName.Trim());
        cmd.Parameters.AddWithValue("@en", string.IsNullOrWhiteSpace(x.JobTitleEN) ? DBNull.Value : x.JobTitleEN.Trim());
        cmd.Parameters.AddWithValue("@ar", string.IsNullOrWhiteSpace(x.JobTitleAR) ? DBNull.Value : x.JobTitleAR.Trim());
        cmd.Parameters.AddWithValue("@ku", string.IsNullOrWhiteSpace(x.JobTitleKU) ? DBNull.Value : x.JobTitleKU.Trim());
        cmd.Parameters.AddWithValue("@photo", string.IsNullOrWhiteSpace(normalizedPhoto) ? DBNull.Value : normalizedPhoto);
        cmd.Parameters.AddWithValue("@salary", x.Salary);
        cmd.Parameters.AddWithValue("@active", x.IsActive);
        cmd.Parameters.AddWithValue("@show", x.ShowInCustomerApp);
        return await cmd.ExecuteNonQueryAsync() == 0 ? NotFound(new { success = false }) : Ok(new { success = true, photoURL = normalizedPhoto });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        if (id <= 0) return BadRequest(new { success = false, message = "معرّف الموظف غير صحيح." });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await using var cmd = new MySqlCommand("DELETE FROM restaurantemployees WHERE EmployeeID=@id", c);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() == 0
            ? NotFound(new { success = false, message = "الموظف غير موجود." })
            : Ok(new { success = true, deleted = true });
    }

    [HttpPost("{id:int}/transactions")]
    public async Task<IActionResult> AddTransaction(int id, [FromBody] PayrollTransactionRequest x)
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
        if (managerUserId is null) return Unauthorized(new { success = false });
        if (id <= 0 || x.Amount <= 0 || !new[] { "Bonus", "Advance", "Deduction", "Payment" }.Contains(x.TransactionType)) return BadRequest(new { success = false });
        var month = string.IsNullOrWhiteSpace(x.PayrollMonth) ? ExpensesController.BaghdadNow().ToString("yyyy-MM") : x.PayrollMonth;
        if (!DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _)) return BadRequest(new { success = false, message = "PayrollMonth must be yyyy-MM." });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = "INSERT INTO employeepayrolltransactions(EmployeeID,UserID,TransactionType,Amount,PayrollMonth,Notes) VALUES(@employee,@user,@type,@amount,@month,@notes); SELECT LAST_INSERT_ID();";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@employee", id);
        cmd.Parameters.AddWithValue("@user", managerUserId.Value);
        cmd.Parameters.AddWithValue("@type", x.TransactionType);
        cmd.Parameters.AddWithValue("@amount", x.Amount);
        cmd.Parameters.AddWithValue("@month", month);
        cmd.Parameters.AddWithValue("@notes", (object?)x.Notes ?? DBNull.Value);
        return Ok(new { success = true, transactionID = Convert.ToInt64(await cmd.ExecuteScalarAsync()) });
    }

    [HttpGet("{id:int}/payroll")]
    public async Task<IActionResult> GetPayroll(int id, [FromQuery] string? month = null)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false });
        month = string.IsNullOrWhiteSpace(month) ? ExpensesController.BaghdadNow().ToString("yyyy-MM") : month;
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = "SELECT e.EmployeeID,e.FullName,e.Salary,COALESCE(SUM(CASE WHEN t.TransactionType='Bonus' THEN t.Amount ELSE 0 END),0) Bonus,COALESCE(SUM(CASE WHEN t.TransactionType IN ('Advance','Deduction') THEN t.Amount ELSE 0 END),0) Deductions,COALESCE(SUM(CASE WHEN t.TransactionType='Payment' THEN t.Amount ELSE 0 END),0) Paid FROM restaurantemployees e LEFT JOIN employeepayrolltransactions t ON t.EmployeeID=e.EmployeeID AND t.PayrollMonth=@month WHERE e.EmployeeID=@id GROUP BY e.EmployeeID,e.FullName,e.Salary";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@month", month);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return NotFound(new { success = false });
        decimal salary = r.GetDecimal("Salary"), bonus = r.GetDecimal("Bonus"), deductions = r.GetDecimal("Deductions"), paid = r.GetDecimal("Paid");
        return Ok(new { success = true, employeeID = id, fullName = r.GetString("FullName"), month, baseSalary = salary, bonuses = bonus, deductions, netSalary = salary + bonus - deductions, paid, remaining = salary + bonus - deductions - paid });
    }

    [HttpPost("{id:int}/evaluations")]
    public async Task<IActionResult> Evaluate(int id, [FromBody] EvaluationRequest x)
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
        if (managerUserId is null) return Unauthorized(new { success = false });
        if (id <= 0 || x.Score < 0 || x.Score > 10) return BadRequest(new { success = false });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await using var cmd = new MySqlCommand("INSERT INTO employeeevaluations(EmployeeID,UserID,EvaluationDate,Score,Notes) VALUES(@id,@user,@date,@score,@notes)", c);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@user", managerUserId.Value);
        cmd.Parameters.AddWithValue("@date", x.EvaluationDate ?? ExpensesController.BaghdadNow().Date);
        cmd.Parameters.AddWithValue("@score", x.Score);
        cmd.Parameters.AddWithValue("@notes", (object?)x.Notes ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }
}

public sealed class StaffRequest
{
    public string FullName { get; set; } = "";
    public string? JobTitleEN { get; set; }
    public string? JobTitleAR { get; set; }
    public string? JobTitleKU { get; set; }
    public string? DescriptionEN { get; set; }
    public string? DescriptionAR { get; set; }
    public string? DescriptionKU { get; set; }
    public string? PhotoURL { get; set; }
    public decimal Salary { get; set; }
    public bool IsActive { get; set; } = true;
    public bool ShowInCustomerApp { get; set; } = true;
}

public sealed class PayrollTransactionRequest
{
    public string TransactionType { get; set; } = "Bonus";
    public decimal Amount { get; set; }
    public string? PayrollMonth { get; set; }
    public string? Notes { get; set; }
}

public sealed class EvaluationRequest
{
    public decimal Score { get; set; }
    public DateTime? EvaluationDate { get; set; }
    public string? Notes { get; set; }
}
