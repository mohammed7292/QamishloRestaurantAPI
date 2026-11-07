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
        await using var cmd=new MySqlCommand("SELECT UserID,FullName,Username,Role,Phone,IsActive,CreatedAt FROM users ORDER BY FullName",c);
        var users=new List<object>(); await using var r=await cmd.ExecuteReaderAsync();
        while(await r.ReadAsync()) users.Add(new { userID=r.GetInt32("UserID"),fullName=r.GetString("FullName"),username=r.GetString("Username"),role=r.GetString("Role"),phone=r.IsDBNull(r.GetOrdinal("Phone"))?null:r.GetString("Phone"),isActive=r.GetBoolean("IsActive"),createdAt=r.GetDateTime("CreatedAt") });
        return Ok(new { success=true,users });
    }
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UserRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success=false });
        if(string.IsNullOrWhiteSpace(x.FullName)||string.IsNullOrWhiteSpace(x.Username)||string.IsNullOrWhiteSpace(x.Password)||!new[]{"Admin","Manager","Cashier","Kitchen"}.Contains(x.Role)) return BadRequest(new{success=false,message="بيانات المستخدم غير مكتملة."});
        await using var c=DatabaseManager.GetConnection(); await c.OpenAsync();
        await using var cmd=new MySqlCommand("INSERT INTO users(FullName,Username,Password,Role,Phone,IsActive) VALUES(@n,@u,@p,@r,@phone,1); SELECT LAST_INSERT_ID();",c);
        cmd.Parameters.AddWithValue("@n",x.FullName.Trim());cmd.Parameters.AddWithValue("@u",x.Username.Trim());cmd.Parameters.AddWithValue("@p",x.Password);cmd.Parameters.AddWithValue("@r",x.Role);cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);
        try {var id=Convert.ToInt32(await cmd.ExecuteScalarAsync()); return Ok(new{success=true,userID=id});} catch(MySqlException e) when(e.Number==1062){return Conflict(new{success=false,message="اسم المستخدم مستخدم مسبقاً."});}
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id,[FromBody] UserRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new{success=false});
        if(id<=0||string.IsNullOrWhiteSpace(x.FullName)||!new[]{"Admin","Manager","Cashier","Kitchen"}.Contains(x.Role)) return BadRequest(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        string sql=string.IsNullOrWhiteSpace(x.Password)
            ? "UPDATE users SET FullName=@n,Username=COALESCE(NULLIF(@u,''),Username),Role=@r,Phone=@phone,IsActive=@active WHERE UserID=@id"
            : "UPDATE users SET FullName=@n,Username=COALESCE(NULLIF(@u,''),Username),Role=@r,Phone=@phone,IsActive=@active,Password=@p WHERE UserID=@id";
        await using var cmd=new MySqlCommand(sql,c);cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@n",x.FullName.Trim());cmd.Parameters.AddWithValue("@u",x.Username?.Trim()??"");cmd.Parameters.AddWithValue("@r",x.Role);cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);cmd.Parameters.AddWithValue("@active",x.IsActive);if(!string.IsNullOrWhiteSpace(x.Password))cmd.Parameters.AddWithValue("@p",x.Password);
        try { return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true}); }
        catch(MySqlException e) when(e.Number==1062) { return Conflict(new{success=false,message="اسم المستخدم مستخدم مسبقاً."}); }
    }
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        await using var cmd=new MySqlCommand("UPDATE users SET IsActive=0 WHERE UserID=@id",c);cmd.Parameters.AddWithValue("@id",id);
        return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true});
    }
}
public sealed class UserRequest { public string FullName{get;set;}=""; public string Username{get;set;}="";public string Password{get;set;}="";public string Role{get;set;}="Cashier";public string? Phone{get;set;}public bool IsActive{get;set;}=true; }
