using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersAdminController : ControllerBase
{
    // Replaces order lines transactionally. Only manager/admin credentials are accepted.
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] EditOrderRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        if(id<=0||request.Items is null||request.Items.Count==0||request.Items.Any(x=>x.ProductID<=0||x.Quantity<1||x.Quantity>100))return BadRequest(new{success=false,message="Invalid order items."});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();await using var tx=await c.BeginTransactionAsync();
        try
        {
            decimal subtotal=0;
            foreach(var item in request.Items)
            {
                await using var p=new MySqlCommand("SELECT Price FROM Products WHERE ProductID=@id AND IsAvailable=1",c,tx);p.Parameters.AddWithValue("@id",item.ProductID);var value=await p.ExecuteScalarAsync();if(value is null||value==DBNull.Value){await tx.RollbackAsync();return BadRequest(new{success=false,message=$"Product {item.ProductID} unavailable."});}subtotal+=Convert.ToDecimal(value)*item.Quantity;
            }
            await using(var exists=new MySqlCommand("SELECT COUNT(*) FROM Orders WHERE OrderID=@id AND Status NOT IN ('Delivered','Cancelled')",c,tx)){exists.Parameters.AddWithValue("@id",id);if(Convert.ToInt32(await exists.ExecuteScalarAsync())==0){await tx.RollbackAsync();return NotFound(new{success=false,message="Order not found or already closed."});}}
            await using(var del=new MySqlCommand("DELETE FROM OrderDetails WHERE OrderID=@id",c,tx)){del.Parameters.AddWithValue("@id",id);await del.ExecuteNonQueryAsync();}
            foreach(var item in request.Items)
            {
                await using var p=new MySqlCommand("SELECT ProductName,Price FROM Products WHERE ProductID=@id",c,tx);p.Parameters.AddWithValue("@id",item.ProductID);await using var r=await p.ExecuteReaderAsync();await r.ReadAsync();var name=r.GetString("ProductName");var price=r.GetDecimal("Price");await r.CloseAsync();
                await using var insert=new MySqlCommand("INSERT INTO OrderDetails(OrderID,ProductID,Quantity,UnitPrice,TotalPrice,Notes) VALUES(@order,@product,@qty,@price,@total,@notes)",c,tx);insert.Parameters.AddWithValue("@order",id);insert.Parameters.AddWithValue("@product",item.ProductID);insert.Parameters.AddWithValue("@qty",item.Quantity);insert.Parameters.AddWithValue("@price",price);insert.Parameters.AddWithValue("@total",price*item.Quantity);insert.Parameters.AddWithValue("@notes",(object?)item.Notes??DBNull.Value);await insert.ExecuteNonQueryAsync();
            }
            decimal delivery=request.DeliveryFee<0?0:request.DeliveryFee; decimal discount=Math.Clamp(request.DiscountAmount,0m,subtotal+delivery); decimal total=Math.Max(0,subtotal+delivery-discount);
            await using(var update=new MySqlCommand("UPDATE Orders SET Subtotal=@subtotal,DeliveryFee=@fee,DiscountAmount=@discount,TotalAmount=@total,Notes=@notes,UpdatedAt=NOW() WHERE OrderID=@id",c,tx)){update.Parameters.AddWithValue("@subtotal",subtotal);update.Parameters.AddWithValue("@fee",delivery);update.Parameters.AddWithValue("@discount",discount);update.Parameters.AddWithValue("@total",total);update.Parameters.AddWithValue("@notes",(object?)request.Notes??DBNull.Value);update.Parameters.AddWithValue("@id",id);await update.ExecuteNonQueryAsync();}
            await tx.CommitAsync();return Ok(new{success=true,orderID=id,subtotal,deliveryFee=delivery,discountAmount=discount,total});
        }
        catch{await tx.RollbackAsync();throw;}
    }

    // Soft delete: preserve order history and accounting; cancellation removes it from sales reports.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        const string sql="UPDATE Orders SET Status='Cancelled',UpdatedAt=NOW() WHERE OrderID=@id AND Status NOT IN ('Delivered','Cancelled')";
        await using var cmd=new MySqlCommand(sql,c);cmd.Parameters.AddWithValue("@id",id);if(await cmd.ExecuteNonQueryAsync()==0)return Conflict(new{success=false,message="Order does not exist or is already closed."});return Ok(new{success=true,message="Order cancelled; audit history retained."});
    }
}
public sealed class EditOrderRequest { public List<EditOrderItem> Items{get;set;}=new();public decimal DeliveryFee{get;set;}public decimal DiscountAmount{get;set;}public string? Notes{get;set;} }
public sealed class EditOrderItem { public int ProductID{get;set;}public int Quantity{get;set;}public string? Notes{get;set;} }
