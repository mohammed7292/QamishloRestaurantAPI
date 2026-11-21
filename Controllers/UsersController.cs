using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/users")]
public sealed class usersController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success=false });
        await using var c=DatabaseManager.GetConnection(); await c.OpenAsync();
        await using var cmd=new MySqlCommand("SELECT UserID,FullName,Username,Role,Phone,COALESCE(PinCode,'') AS PinCode,IsActive,CreatedAt FROM users ORDER BY IsActive DESC, FullName ASC",c);
        var users=new List<object>(); await using var r=await cmd.ExecuteReaderAsync();
        while(await r.ReadAsync()) users.Add(new { userID=r.GetInt32("UserID"),fullName=r.GetString("FullName"),username=r.GetString("Username"),role=r.GetString("Role"),phone=r.IsDBNull(r.GetOrdinal("Phone"))?null:r.GetString("Phone"),pinCode=r.GetString("PinCode"),isActive=r.GetBoolean("IsActive"),createdAt=r.GetDateTime("CreatedAt") });
        return Ok(new { success=true,users });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success=false });
        if(string.IsNullOrWhiteSpace(x.FullName)||string.IsNullOrWhiteSpace(x.Username)||string.IsNullOrWhiteSpace(x.Password)||!new[]{"Admin","Manager","Cashier","Kitchen"}.Contains(x.Role)) return BadRequest(new{success=false,message="بيانات المستخدم غير مكتملة."});
        await using var c=DatabaseManager.GetConnection(); await c.OpenAsync();
        await using var cmd=new MySqlCommand("INSERT INTO users(FullName,Username,Password,Role,Phone,PinCode,IsActive) VALUES(@n,@u,@p,@r,@phone,@pin,1); SELECT LAST_INSERT_ID();",c);
        cmd.Parameters.AddWithValue("@n",x.FullName.Trim());cmd.Parameters.AddWithValue("@u",x.Username.Trim());cmd.Parameters.AddWithValue("@p",x.Password);cmd.Parameters.AddWithValue("@r",x.Role);cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);cmd.Parameters.AddWithValue("@pin",string.IsNullOrWhiteSpace(x.PinCode)?DBNull.Value:x.PinCode.Trim());
        try {var id=Convert.ToInt32(await cmd.ExecuteScalarAsync()); return Ok(new{success=true,userID=id});} catch(MySqlException e) when(e.Number==1062){return Conflict(new{success=false,message="اسم المستخدم مستخدم مسبقاً."});}
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id,[FromBody] UserRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new{success=false});
        if(id<=0||string.IsNullOrWhiteSpace(x.FullName)||!new[]{"Admin","Manager","Cashier","Kitchen"}.Contains(x.Role)) return BadRequest(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        string sql=string.IsNullOrWhiteSpace(x.Password)
            ? "UPDATE users SET FullName=@n,Username=COALESCE(NULLIF(@u,''),Username),Role=@r,Phone=@phone,PinCode=COALESCE(NULLIF(@pin,''),PinCode),IsActive=@active WHERE UserID=@id"
            : "UPDATE users SET FullName=@n,Username=COALESCE(NULLIF(@u,''),Username),Role=@r,Phone=@phone,PinCode=COALESCE(NULLIF(@pin,''),PinCode),IsActive=@active,Password=@p WHERE UserID=@id";
        await using var cmd=new MySqlCommand(sql,c);cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@n",x.FullName.Trim());cmd.Parameters.AddWithValue("@u",x.Username?.Trim()??"");cmd.Parameters.AddWithValue("@r",x.Role);cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);cmd.Parameters.AddWithValue("@pin",string.IsNullOrWhiteSpace(x.PinCode)?DBNull.Value:x.PinCode.Trim());cmd.Parameters.AddWithValue("@active",x.IsActive);if(!string.IsNullOrWhiteSpace(x.Password))cmd.Parameters.AddWithValue("@p",x.Password);
        try { return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true}); }
        catch(MySqlException e) when(e.Number==1062) { return Conflict(new{success=false,message="اسم المستخدم مستخدم مسبقاً."}); }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool permanent = false)
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
        if (managerUserId is null) return Unauthorized(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();

        if (!permanent)
        {
            await using var cmd=new MySqlCommand("UPDATE users SET IsActive=0 WHERE UserID=@id",c);
            cmd.Parameters.AddWithValue("@id",id);
            return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true, message="تم تعطيل المستخدم بنجاح."});
        }

        if (id == managerUserId.Value || id == 1 || id == 2)
        {
            return BadRequest(new { success = false, message = "لا يمكنك حذف حساب المدير الحالي المستخدم في الجلسة." });
        }

        await using var tx = await c.BeginTransactionAsync();
        try
        {
            int fallbackUserId = managerUserId.Value;

            // Safely reassign any foreign keys referencing this disabled user so history is preserved
            await using (var reassignOrders = new MySqlCommand("UPDATE orders SET UserID=@fallback WHERE UserID=@id", c, tx))
            {
                reassignOrders.Parameters.AddWithValue("@fallback", fallbackUserId);
                reassignOrders.Parameters.AddWithValue("@id", id);
                await reassignOrders.ExecuteNonQueryAsync();
            }

            await using (var reassignExpenses = new MySqlCommand("UPDATE expenses SET UserID=@fallback WHERE UserID=@id", c, tx))
            {
                reassignExpenses.Parameters.AddWithValue("@fallback", fallbackUserId);
                reassignExpenses.Parameters.AddWithValue("@id", id);
                await reassignExpenses.ExecuteNonQueryAsync();
            }

            await using (var reassignPayroll = new MySqlCommand("UPDATE employeepayrolltransactions SET UserID=@fallback WHERE UserID=@id", c, tx))
            {
                reassignPayroll.Parameters.AddWithValue("@fallback", fallbackUserId);
                reassignPayroll.Parameters.AddWithValue("@id", id);
                await reassignPayroll.ExecuteNonQueryAsync();
            }

            await using (var reassignEvals = new MySqlCommand("UPDATE employeeevaluations SET UserID=@fallback WHERE UserID=@id", c, tx))
            {
                reassignEvals.Parameters.AddWithValue("@fallback", fallbackUserId);
                reassignEvals.Parameters.AddWithValue("@id", id);
                await reassignEvals.ExecuteNonQueryAsync();
            }

            await using var delCmd = new MySqlCommand("DELETE FROM users WHERE UserID=@id AND IsActive=0", c, tx);
            delCmd.Parameters.AddWithValue("@id", id);
            int deleted = await delCmd.ExecuteNonQueryAsync();
            if (deleted == 0)
            {
                await tx.RollbackAsync();
                return BadRequest(new { success = false, message = "يجب تعطيل المستخدم أولاً قبل حذفه نهائياً، أو أن المستخدم غير موجود." });
            }

            await tx.CommitAsync();
            return Ok(new { success = true, deleted = true, message = "تم حذف المستخدم المعطّل نهائياً." });
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    [HttpDelete("disabled")]
    public async Task<IActionResult> DeleteAllDisabledUsers()
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
        if (managerUserId is null) return Unauthorized(new { success = false });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        try
        {
            int fallbackUserId = managerUserId.Value;

            const string reassignOrdersSql = @"
                UPDATE orders o
                INNER JOIN users u ON u.UserID = o.UserID
                SET o.UserID = @fallback
                WHERE u.IsActive = 0 AND u.UserID <> @fallback;";
            await using (var cmd1 = new MySqlCommand(reassignOrdersSql, c, tx))
            {
                cmd1.Parameters.AddWithValue("@fallback", fallbackUserId);
                await cmd1.ExecuteNonQueryAsync();
            }

            const string reassignExpensesSql = @"
                UPDATE expenses e
                INNER JOIN users u ON u.UserID = e.UserID
                SET e.UserID = @fallback
                WHERE u.IsActive = 0 AND u.UserID <> @fallback;";
            await using (var cmd2 = new MySqlCommand(reassignExpensesSql, c, tx))
            {
                cmd2.Parameters.AddWithValue("@fallback", fallbackUserId);
                await cmd2.ExecuteNonQueryAsync();
            }

            const string reassignPayrollSql = @"
                UPDATE employeepayrolltransactions t
                INNER JOIN users u ON u.UserID = t.UserID
                SET t.UserID = @fallback
                WHERE u.IsActive = 0 AND u.UserID <> @fallback;";
            await using (var cmd3 = new MySqlCommand(reassignPayrollSql, c, tx))
            {
                cmd3.Parameters.AddWithValue("@fallback", fallbackUserId);
                await cmd3.ExecuteNonQueryAsync();
            }

            const string reassignEvalsSql = @"
                UPDATE employeeevaluations ev
                INNER JOIN users u ON u.UserID = ev.UserID
                SET ev.UserID = @fallback
                WHERE u.IsActive = 0 AND u.UserID <> @fallback;";
            await using (var cmd4 = new MySqlCommand(reassignEvalsSql, c, tx))
            {
                cmd4.Parameters.AddWithValue("@fallback", fallbackUserId);
                await cmd4.ExecuteNonQueryAsync();
            }

            await using var delCmd = new MySqlCommand("DELETE FROM users WHERE IsActive=0 AND UserID <> @fallback", c, tx);
            delCmd.Parameters.AddWithValue("@fallback", fallbackUserId);
            int deletedCount = await delCmd.ExecuteNonQueryAsync();

            await tx.CommitAsync();
            return Ok(new { success = true, deletedCount, message = $"تم حذف {deletedCount} من المستخدمين المعطّلين نهائياً." });
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    [HttpPost("pin-login")]
    public async Task<IActionResult> PinLogin([FromBody] PinLoginRequest req)
    {
        string pin = req?.PinCode?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(pin))
            return BadRequest(new { success = false, message = "يرجى إدخال الرمز السري (PIN)." });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = @"
            SELECT u.UserID, u.FullName, u.Username, u.Role, COALESCE(u.PinCode, '') AS PinCode,
                   p.CanEditPrices, p.CanManageExpenses, p.CanViewNetProfit, p.CanViewReports,
                   p.CanManageDiscounts, p.CanManageProducts, p.CanManageDebts, p.CanManageStaff,
                   p.CanManagePrinters, p.CanCloseCashShift, p.CanDeleteOrders
            FROM users u
            LEFT JOIN userpermissions p ON p.UserID = u.UserID
            WHERE u.IsActive = 1 AND u.PinCode = @pin
            LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@pin", pin);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return Unauthorized(new { success = false, message = "الرمز السري (PIN) غير صحيح." });

        string role = r.GetString("Role");
        bool isAdminOrMgr = role.Equals("Admin", StringComparison.OrdinalIgnoreCase) || role.Equals("Manager", StringComparison.OrdinalIgnoreCase);
        bool Flag(string col) => r.IsDBNull(r.GetOrdinal(col)) ? isAdminOrMgr : r.GetBoolean(col);

        return Ok(new
        {
            success = true,
            user = new
            {
                userID = r.GetInt32("UserID"),
                fullName = r.GetString("FullName"),
                username = r.GetString("Username"),
                role,
                pinCode = r.GetString("PinCode")
            },
            permissions = new
            {
                canEditPrices = Flag("CanEditPrices"),
                canManageExpenses = Flag("CanManageExpenses"),
                canViewNetProfit = Flag("CanViewNetProfit"),
                canViewReports = Flag("CanViewReports"),
                canManageDiscounts = Flag("CanManageDiscounts"),
                canManageProducts = Flag("CanManageProducts"),
                canManageDebts = Flag("CanManageDebts"),
                canManageStaff = Flag("CanManageStaff"),
                canManagePrinters = Flag("CanManagePrinters"),
                canCloseCashShift = Flag("CanCloseCashShift"),
                canDeleteOrders = Flag("CanDeleteOrders")
            }
        });
    }
}

public sealed class PinLoginRequest { public string PinCode { get; set; } = ""; }

public sealed class UserRequest { public string FullName{get;set;}=""; public string Username{get;set;}="";public string Password{get;set;}="";public string Role{get;set;}="Cashier";public string? Phone{get;set;}public string? PinCode{get;set;}public bool IsActive{get;set;}=true; }
