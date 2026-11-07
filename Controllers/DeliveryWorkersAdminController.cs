using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryworkers/admin")]
public sealed class deliveryworkersAdminController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if(!await ManagerCredentialValidator.IsManagerAsync(Request))return Unauthorized(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();await using var cmd=new MySqlCommand("SELECT DeliveryWorkerID,FullName,Username,Phone,IsActive,CreatedAt FROM deliveryworkers ORDER BY FullName",c);await using var r=await cmd.ExecuteReaderAsync();var workers=new List<object>();
        while(await r.ReadAsync())workers.Add(new{deliveryWorkerID=r.GetInt32("DeliveryWorkerID"),fullName=r.GetString("FullName"),username=r.GetString("Username"),phone=r.IsDBNull(r.GetOrdinal("Phone"))?null:r.GetString("Phone"),isActive=r.GetBoolean("IsActive"),createdAt=r.GetDateTime("CreatedAt")});
        return Ok(new{success=true,workers});
    }
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeliveryWorkerAdminRequest x)
    {
        if(!await ManagerCredentialValidator.IsManagerAsync(Request))return Unauthorized(new{success=false});
        if(string.IsNullOrWhiteSpace(x.FullName)||string.IsNullOrWhiteSpace(x.Username)||string.IsNullOrWhiteSpace(x.Password))return BadRequest(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();await using var cmd=new MySqlCommand("INSERT INTO deliveryworkers(FullName,Username,Password,Phone,IsActive) VALUES(@n,@u,@p,@phone,1); SELECT LAST_INSERT_ID();",c);cmd.Parameters.AddWithValue("@n",x.FullName.Trim());cmd.Parameters.AddWithValue("@u",x.Username.Trim());cmd.Parameters.AddWithValue("@p",x.Password);cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);
        try{return Ok(new{success=true,deliveryWorkerID=Convert.ToInt32(await cmd.ExecuteScalarAsync())});}catch(MySqlException e)when(e.Number==1062){return Conflict(new{success=false,message="اسم المستخدم موجود."});}
    }
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id,[FromBody] DeliveryWorkerAdminRequest x)
    {
        if(!await ManagerCredentialValidator.IsManagerAsync(Request))return Unauthorized(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();string sql=string.IsNullOrWhiteSpace(x.Password)?"UPDATE deliveryworkers SET FullName=@n,Phone=@phone,IsActive=@active WHERE DeliveryWorkerID=@id":"UPDATE deliveryworkers SET FullName=@n,Phone=@phone,IsActive=@active,Password=@p WHERE DeliveryWorkerID=@id";await using var cmd=new MySqlCommand(sql,c);cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@n",x.FullName.Trim());cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);cmd.Parameters.AddWithValue("@active",x.IsActive);if(!string.IsNullOrWhiteSpace(x.Password))cmd.Parameters.AddWithValue("@p",x.Password);return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true});
    }
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if(!await ManagerCredentialValidator.IsManagerAsync(Request))return Unauthorized(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();await using var cmd=new MySqlCommand("UPDATE deliveryworkers SET IsActive=0 WHERE DeliveryWorkerID=@id",c);cmd.Parameters.AddWithValue("@id",id);return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true});
    }
}
public sealed class DeliveryWorkerAdminRequest { public string FullName{get;set;}="";public string Username{get;set;}="";public string Password{get;set;}="";public string? Phone{get;set;}public bool IsActive{get;set;}=true; }
