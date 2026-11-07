using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryworkers/{workerId:int}/orders/{orderId:int}/transfer")]
public sealed class DeliveryWorkerTransferController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Transfer(int workerId,int orderId,[FromBody] TransferDeliveryRequest request)
    {
        if(workerId<=0||orderId<=0||request.NewWorkerID<=0||string.IsNullOrWhiteSpace(request.Username)||string.IsNullOrWhiteSpace(request.Password))return BadRequest(new{success=false,message="Worker credentials and valid order/worker IDs are required."});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();await using var tx=await c.BeginTransactionAsync();
        try
        {
            await using(var sourceAuth=new MySqlCommand("SELECT COUNT(*) FROM deliveryworkers WHERE DeliveryWorkerID=@id AND Username=@username AND Password=@password AND IsActive=1",c,tx)){sourceAuth.Parameters.AddWithValue("@id",workerId);sourceAuth.Parameters.AddWithValue("@username",request.Username.Trim());sourceAuth.Parameters.AddWithValue("@password",request.Password);if(Convert.ToInt32(await sourceAuth.ExecuteScalarAsync())==0){await tx.RollbackAsync();return Unauthorized(new{success=false,message="Invalid source worker credentials."});}}
            await using(var worker=new MySqlCommand("SELECT COUNT(*) FROM deliveryworkers WHERE DeliveryWorkerID=@id AND IsActive=1",c,tx)){worker.Parameters.AddWithValue("@id",request.NewWorkerID);if(Convert.ToInt32(await worker.ExecuteScalarAsync())==0){await tx.RollbackAsync();return BadRequest(new{success=false,message="The receiving delivery worker is not active."});}}
            await using(var check=new MySqlCommand("SELECT CustomerID FROM Orders WHERE OrderID=@order AND DeliveryWorkerID=@from AND Status NOT IN ('Delivered','Cancelled') FOR UPDATE",c,tx)){check.Parameters.AddWithValue("@order",orderId);check.Parameters.AddWithValue("@from",workerId);var customer=await check.ExecuteScalarAsync();if(customer is null||customer==DBNull.Value){await tx.RollbackAsync();return NotFound(new{success=false,message="Order is not currently assigned to this worker."});}}
            await using(var update=new MySqlCommand("UPDATE Orders SET DeliveryWorkerID=@to,UpdatedAt=NOW() WHERE OrderID=@order",c,tx)){update.Parameters.AddWithValue("@to",request.NewWorkerID);update.Parameters.AddWithValue("@order",orderId);await update.ExecuteNonQueryAsync();}
            await using(var history=new MySqlCommand("INSERT INTO DeliveryWorkerTransfers(OrderID,FromWorkerID,ToWorkerID,Notes) VALUES(@order,@from,@to,@notes)",c,tx)){history.Parameters.AddWithValue("@order",orderId);history.Parameters.AddWithValue("@from",workerId);history.Parameters.AddWithValue("@to",request.NewWorkerID);history.Parameters.AddWithValue("@notes",(object?)request.Notes??DBNull.Value);await history.ExecuteNonQueryAsync();}
            await tx.CommitAsync();return Ok(new{success=true,orderID=orderId,fromWorkerID=workerId,toWorkerID=request.NewWorkerID});
        }
        catch{await tx.RollbackAsync();throw;}
    }
}
public sealed class TransferDeliveryRequest { public int NewWorkerID{get;set;}public string Username{get;set;}="";public string Password{get;set;}="";public string? Notes{get;set;} }
