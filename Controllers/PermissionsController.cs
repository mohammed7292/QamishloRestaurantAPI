using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/permissions")]
public sealed class PermissionsController : ControllerBase
{
    private static DateTime BaghdadNow() => DateTime.UtcNow.AddHours(3);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        const string sql = @"
            SELECT u.UserID, u.FullName, u.Username, u.Role, u.IsActive,
                   p.CanEditPrices, p.CanManageExpenses, p.CanViewNetProfit, p.CanViewReports,
                   p.CanManageDiscounts, p.CanManageProducts, p.CanManageDebts, p.CanManageStaff,
                   p.CanManagePrinters, p.CanCloseCashShift, p.CanDeleteOrders
            FROM users u
            LEFT JOIN userpermissions p ON p.UserID = u.UserID
            WHERE u.IsActive = 1
            ORDER BY FIELD(u.Role, 'Admin', 'Manager', 'Cashier', 'Kitchen'), u.FullName ASC;";

        var list = new List<object>();
        await using var cmd = new MySqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            string role = r.GetString("Role");
            bool isAdminOrMgr = role.Equals("Admin", StringComparison.OrdinalIgnoreCase) || role.Equals("Manager", StringComparison.OrdinalIgnoreCase);
            bool GetFlag(string col, bool fallback) => r.IsDBNull(r.GetOrdinal(col)) ? fallback : r.GetBoolean(col);

            list.Add(new
            {
                userID = r.GetInt32("UserID"),
                fullName = r.GetString("FullName"),
                username = r.GetString("Username"),
                role,
                canEditPrices = GetFlag("CanEditPrices", isAdminOrMgr),
                canManageExpenses = GetFlag("CanManageExpenses", isAdminOrMgr),
                canViewNetProfit = GetFlag("CanViewNetProfit", isAdminOrMgr),
                canViewReports = GetFlag("CanViewReports", isAdminOrMgr),
                canManageDiscounts = GetFlag("CanManageDiscounts", isAdminOrMgr),
                canManageProducts = GetFlag("CanManageProducts", isAdminOrMgr),
                canManageDebts = GetFlag("CanManageDebts", isAdminOrMgr),
                canManageStaff = GetFlag("CanManageStaff", isAdminOrMgr),
                canManagePrinters = GetFlag("CanManagePrinters", isAdminOrMgr),
                canCloseCashShift = GetFlag("CanCloseCashShift", isAdminOrMgr),
                canDeleteOrders = GetFlag("CanDeleteOrders", isAdminOrMgr)
            });
        }

        return Ok(new { success = true, permissions = list });
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMine([FromQuery] string? username = null)
    {
        string user = !string.IsNullOrWhiteSpace(username)
            ? username.Trim()
            : (Request.Headers["X-Manager-Username"].FirstOrDefault() ?? Request.Headers["X-Username"].FirstOrDefault() ?? "").Trim();

        if (string.IsNullOrWhiteSpace(user))
            return Ok(new { success = true, isAdmin = true, permissions = new { canEditPrices = true, canManageExpenses = true, canViewNetProfit = true, canViewReports = true, canManageDiscounts = true, canManageProducts = true, canManageDebts = true, canManageStaff = true, canManagePrinters = true, canCloseCashShift = true, canDeleteOrders = true } });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = @"
            SELECT u.UserID, u.FullName, u.Username, u.Role,
                   p.CanEditPrices, p.CanManageExpenses, p.CanViewNetProfit, p.CanViewReports,
                   p.CanManageDiscounts, p.CanManageProducts, p.CanManageDebts, p.CanManageStaff,
                   p.CanManagePrinters, p.CanCloseCashShift, p.CanDeleteOrders
            FROM users u
            LEFT JOIN userpermissions p ON p.UserID = u.UserID
            WHERE LOWER(u.Username) = LOWER(@u) AND u.IsActive = 1
            LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@u", user);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return NotFound(new { success = false, message = "المستخدم غير موجود." });

        string role = r.GetString("Role");
        bool isAdminOrMgr = role.Equals("Admin", StringComparison.OrdinalIgnoreCase) || role.Equals("Manager", StringComparison.OrdinalIgnoreCase);
        bool GetFlag(string col, bool fallback) => r.IsDBNull(r.GetOrdinal(col)) ? fallback : r.GetBoolean(col);

        return Ok(new
        {
            success = true,
            userID = r.GetInt32("UserID"),
            fullName = r.GetString("FullName"),
            role,
            permissions = new
            {
                canEditPrices = GetFlag("CanEditPrices", isAdminOrMgr),
                canManageExpenses = GetFlag("CanManageExpenses", isAdminOrMgr),
                canViewNetProfit = GetFlag("CanViewNetProfit", isAdminOrMgr),
                canViewReports = GetFlag("CanViewReports", isAdminOrMgr),
                canManageDiscounts = GetFlag("CanManageDiscounts", isAdminOrMgr),
                canManageProducts = GetFlag("CanManageProducts", isAdminOrMgr),
                canManageDebts = GetFlag("CanManageDebts", isAdminOrMgr),
                canManageStaff = GetFlag("CanManageStaff", isAdminOrMgr),
                canManagePrinters = GetFlag("CanManagePrinters", isAdminOrMgr),
                canCloseCashShift = GetFlag("CanCloseCashShift", isAdminOrMgr),
                canDeleteOrders = GetFlag("CanDeleteOrders", isAdminOrMgr)
            }
        });
    }

    [HttpPut("{userId:int}")]
    public async Task<IActionResult> Save(int userId, [FromBody] UserPermissionsRequest req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (userId <= 0 || req == null)
            return BadRequest(new { success = false, message = "بيانات الصلاحيات غير صالحة." });

        string actor = Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager";
        DateTime now = BaghdadNow();

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = @"
            INSERT INTO userpermissions
                (UserID, CanEditPrices, CanManageExpenses, CanViewNetProfit, CanViewReports,
                 CanManageDiscounts, CanManageProducts, CanManageDebts, CanManageStaff,
                 CanManagePrinters, CanCloseCashShift, CanDeleteOrders, UpdatedBy, UpdatedAt)
            VALUES
                (@uid, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @by, @now)
            ON DUPLICATE KEY UPDATE
                CanEditPrices = VALUES(CanEditPrices),
                CanManageExpenses = VALUES(CanManageExpenses),
                CanViewNetProfit = VALUES(CanViewNetProfit),
                CanViewReports = VALUES(CanViewReports),
                CanManageDiscounts = VALUES(CanManageDiscounts),
                CanManageProducts = VALUES(CanManageProducts),
                CanManageDebts = VALUES(CanManageDebts),
                CanManageStaff = VALUES(CanManageStaff),
                CanManagePrinters = VALUES(CanManagePrinters),
                CanCloseCashShift = VALUES(CanCloseCashShift),
                CanDeleteOrders = VALUES(CanDeleteOrders),
                UpdatedBy = VALUES(UpdatedBy),
                UpdatedAt = VALUES(UpdatedAt);";

        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@uid", userId);
        cmd.Parameters.AddWithValue("@p1", req.CanEditPrices ? 1 : 0);
        cmd.Parameters.AddWithValue("@p2", req.CanManageExpenses ? 1 : 0);
        cmd.Parameters.AddWithValue("@p3", req.CanViewNetProfit ? 1 : 0);
        cmd.Parameters.AddWithValue("@p4", req.CanViewReports ? 1 : 0);
        cmd.Parameters.AddWithValue("@p5", req.CanManageDiscounts ? 1 : 0);
        cmd.Parameters.AddWithValue("@p6", req.CanManageProducts ? 1 : 0);
        cmd.Parameters.AddWithValue("@p7", req.CanManageDebts ? 1 : 0);
        cmd.Parameters.AddWithValue("@p8", req.CanManageStaff ? 1 : 0);
        cmd.Parameters.AddWithValue("@p9", req.CanManagePrinters ? 1 : 0);
        cmd.Parameters.AddWithValue("@p10", req.CanCloseCashShift ? 1 : 0);
        cmd.Parameters.AddWithValue("@p11", req.CanDeleteOrders ? 1 : 0);
        cmd.Parameters.AddWithValue("@by", actor);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync();

        try
        {
            await using var audit = new MySqlCommand(
                "INSERT INTO auditlogs (ActorUsername, ActorRole, Action, EntityType, EntityID, Details, CreatedAt) VALUES (@by, 'Manager', 'UpdatePermissions', 'User', @uid, @det, @now);", c);
            audit.Parameters.AddWithValue("@by", actor);
            audit.Parameters.AddWithValue("@uid", userId.ToString());
            audit.Parameters.AddWithValue("@det", $"Prices={req.CanEditPrices}, Expenses={req.CanManageExpenses}, NetProfit={req.CanViewNetProfit}, Reports={req.CanViewReports}, Debts={req.CanManageDebts}");
            audit.Parameters.AddWithValue("@now", now);
            await audit.ExecuteNonQueryAsync();
        }
        catch { }

        return Ok(new { success = true, message = "تم حفظ صلاحيات المستخدم بنجاح." });
    }

    public sealed class UserPermissionsRequest
    {
        public bool CanEditPrices { get; set; }
        public bool CanManageExpenses { get; set; }
        public bool CanViewNetProfit { get; set; }
        public bool CanViewReports { get; set; }
        public bool CanManageDiscounts { get; set; }
        public bool CanManageProducts { get; set; }
        public bool CanManageDebts { get; set; }
        public bool CanManageStaff { get; set; }
        public bool CanManagePrinters { get; set; }
        public bool CanCloseCashShift { get; set; }
        public bool CanDeleteOrders { get; set; }
    }
}
