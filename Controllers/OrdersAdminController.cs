using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersAdminController : ControllerBase
{
    // Manager-created order (dine-in, takeaway, or delivery). Customer identity is optional.
    [HttpPost("manager")]
    public async Task<IActionResult> CreateManagerOrder([FromBody] ManagerCreateOrderRequest request)
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
        if (managerUserId is null) return Unauthorized(new { success = false, message = "يلزم تسجيل دخول المدير." });
        if (request.Items is null || request.Items.Count == 0 ||
            request.Items.Any(x => x.ProductID <= 0 || x.Quantity < 1 || x.Quantity > 100))
            return BadRequest(new { success = false, message = "أضف منتجات بكميات صحيحة إلى الطلب." });

        string orderType = request.OrderType switch
        {
            "Dine In" or "داخل المطعم" => "Dine In",
            "Takeaway" or "سفري" => "Takeaway",
            "Delivery" or "توصيل" => "Delivery",
            _ => ""
        };
        if (string.IsNullOrEmpty(orderType))
            return BadRequest(new { success = false, message = "نوع الطلب غير صحيح." });
        if (orderType == "Delivery" && (!request.DeliveryZoneID.HasValue || request.DeliveryZoneID.Value <= 0 || string.IsNullOrWhiteSpace(request.Address)))
            return BadRequest(new { success = false, message = "لطلب التوصيل أدخل العنوان واختر منطقة توصيل." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            decimal subtotal = 0m;
            decimal deliveryFee = 0m;
            if (orderType == "Delivery")
            {
                await using var zoneCommand = new MySqlCommand("SELECT DeliveryFee FROM deliveryzones WHERE DeliveryZoneID=@zone AND IsActive=1", connection, transaction);
                zoneCommand.Parameters.AddWithValue("@zone", request.DeliveryZoneID!.Value);
                var feeValue = await zoneCommand.ExecuteScalarAsync();
                if (feeValue is null || feeValue == DBNull.Value)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = "منطقة التوصيل غير متاحة حالياً." });
                }
                deliveryFee = Convert.ToDecimal(feeValue);
            }
            var lines = new List<(int ProductId, int Quantity, string Name, decimal Price)>();
            foreach (var item in request.Items)
            {
                await using var product = new MySqlCommand(
                    "SELECT ProductName, Price FROM products WHERE ProductID=@id AND (IsAvailable=1 OR (AvailableUntil IS NOT NULL AND AvailableUntil <= UTC_TIMESTAMP()))", connection, transaction);
                product.Parameters.AddWithValue("@id", item.ProductID);
                await using var reader = await product.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await reader.CloseAsync();
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = $"المنتج رقم {item.ProductID} غير متاح حالياً." });
                }
                string name = reader.GetString("ProductName");
                decimal price = reader.GetDecimal("Price");
                await reader.CloseAsync();
                lines.Add((item.ProductID, item.Quantity, name, price));
                subtotal += price * item.Quantity;
            }

            DateTime now = ExpensesController.BaghdadNow();
            DateTime businessDate = ExpensesController.BusinessDate(now);
            const string insertOrder = @"INSERT INTO orders
                (CustomerID, UserID, DeliveryWorkerID, DeliveryZoneID, OrderDate, BusinessDate, OrderType, Status,
                 Subtotal, DeliveryFee, DiscountAmount, TotalAmount, DeliveryAddress, CustomerLatitude, CustomerLongitude, Notes)
                VALUES (NULL, @user, NULL, @zone, @date, @business, @type, 'New', @subtotal, @fee, 0, @total, @address, NULL, NULL, @notes);
                SELECT LAST_INSERT_ID();";
            await using var orderCommand = new MySqlCommand(insertOrder, connection, transaction);
            orderCommand.Parameters.AddWithValue("@user", managerUserId.Value);
            orderCommand.Parameters.AddWithValue("@date", now);
            orderCommand.Parameters.AddWithValue("@business", businessDate);
            orderCommand.Parameters.AddWithValue("@type", orderType);
            orderCommand.Parameters.AddWithValue("@zone", orderType == "Delivery" ? (object)request.DeliveryZoneID!.Value : DBNull.Value);
            orderCommand.Parameters.AddWithValue("@subtotal", subtotal);
            orderCommand.Parameters.AddWithValue("@fee", deliveryFee);
            orderCommand.Parameters.AddWithValue("@total", subtotal + deliveryFee);
            orderCommand.Parameters.AddWithValue("@address", orderType == "Delivery" ? (object)request.Address!.Trim() : DBNull.Value);
            orderCommand.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
            int orderId = Convert.ToInt32(await orderCommand.ExecuteScalarAsync());

            foreach (var line in lines)
            {
                await using var lineCommand = new MySqlCommand(
                    "INSERT INTO orderdetails(OrderID,ProductID,Quantity,UnitPrice,TotalPrice,Notes) VALUES(@order,@product,@qty,@price,@total,@notes)", connection, transaction);
                lineCommand.Parameters.AddWithValue("@order", orderId);
                lineCommand.Parameters.AddWithValue("@product", line.ProductId);
                lineCommand.Parameters.AddWithValue("@qty", line.Quantity);
                lineCommand.Parameters.AddWithValue("@price", line.Price);
                lineCommand.Parameters.AddWithValue("@total", line.Price * line.Quantity);
                lineCommand.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
                await lineCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Ok(new { success = true, orderId, subtotal, deliveryFee, total = subtotal + deliveryFee, orderType });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

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
                await using var p=new MySqlCommand("SELECT Price FROM products WHERE ProductID=@id AND (IsAvailable=1 OR (AvailableUntil IS NOT NULL AND AvailableUntil <= UTC_TIMESTAMP()))",c,tx);p.Parameters.AddWithValue("@id",item.ProductID);var value=await p.ExecuteScalarAsync();if(value is null||value==DBNull.Value){await tx.RollbackAsync();return BadRequest(new{success=false,message=$"Product {item.ProductID} unavailable."});}subtotal+=Convert.ToDecimal(value)*item.Quantity;
            }
            await using(var exists=new MySqlCommand("SELECT COUNT(*) FROM orders WHERE OrderID=@id AND IsArchived=0 AND Status NOT IN ('Delivered','Cancelled')",c,tx)){exists.Parameters.AddWithValue("@id",id);if(Convert.ToInt32(await exists.ExecuteScalarAsync())==0){await tx.RollbackAsync();return NotFound(new{success=false,message="Order not found or already closed."});}}
            await using(var del=new MySqlCommand("DELETE FROM orderdetails WHERE OrderID=@id",c,tx)){del.Parameters.AddWithValue("@id",id);await del.ExecuteNonQueryAsync();}
            foreach(var item in request.Items)
            {
                await using var p=new MySqlCommand("SELECT ProductName,Price FROM products WHERE ProductID=@id",c,tx);p.Parameters.AddWithValue("@id",item.ProductID);await using var r=await p.ExecuteReaderAsync();await r.ReadAsync();var name=r.GetString("ProductName");var price=r.GetDecimal("Price");await r.CloseAsync();
                await using var insert=new MySqlCommand("INSERT INTO orderdetails(OrderID,ProductID,Quantity,UnitPrice,TotalPrice,Notes) VALUES(@order,@product,@qty,@price,@total,@notes)",c,tx);insert.Parameters.AddWithValue("@order",id);insert.Parameters.AddWithValue("@product",item.ProductID);insert.Parameters.AddWithValue("@qty",item.Quantity);insert.Parameters.AddWithValue("@price",price);insert.Parameters.AddWithValue("@total",price*item.Quantity);insert.Parameters.AddWithValue("@notes",(object?)item.Notes??DBNull.Value);await insert.ExecuteNonQueryAsync();
            }
            decimal delivery=request.DeliveryFee<0?0:request.DeliveryFee; decimal discount=Math.Clamp(request.DiscountAmount,0m,subtotal+delivery); decimal total=Math.Max(0,subtotal+delivery-discount);
            await using(var update=new MySqlCommand("UPDATE orders SET Subtotal=@subtotal,DeliveryFee=@fee,DiscountAmount=@discount,TotalAmount=@total,Notes=@notes,UpdatedAt=NOW() WHERE OrderID=@id",c,tx)){update.Parameters.AddWithValue("@subtotal",subtotal);update.Parameters.AddWithValue("@fee",delivery);update.Parameters.AddWithValue("@discount",discount);update.Parameters.AddWithValue("@total",total);update.Parameters.AddWithValue("@notes",(object?)request.Notes??DBNull.Value);update.Parameters.AddWithValue("@id",id);await update.ExecuteNonQueryAsync();}
            await tx.CommitAsync();return Ok(new{success=true,orderID=id,subtotal,deliveryFee=delivery,discountAmount=discount,total});
        }
        catch{await tx.RollbackAsync();throw;}
    }

    // Archive an order from the restaurant management list without changing its
    // business status or removing it from financial reports/customer history.
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id)
    {
        if (id <= 0) return BadRequest(new { success = false, message = "Invalid order ID." });
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = "UPDATE orders SET IsArchived=1, UpdatedAt=NOW() WHERE OrderID=@id AND IsArchived=0";
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        if (await command.ExecuteNonQueryAsync() == 0)
        {
            await using var exists = new MySqlCommand("SELECT COUNT(*) FROM orders WHERE OrderID=@id", connection);
            exists.Parameters.AddWithValue("@id", id);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                return NotFound(new { success = false, message = "Order not found." });
            return Conflict(new { success = false, message = "Order is already archived." });
        }

        return Ok(new { success = true, orderID = id, isArchived = true, message = "Order archived. Sales history was preserved." });
    }
}
public sealed class EditOrderRequest { public List<EditOrderItem> Items{get;set;}=new();public decimal DeliveryFee{get;set;}public decimal DiscountAmount{get;set;}public string? Notes{get;set;} }
public sealed class EditOrderItem { public int ProductID{get;set;}public int Quantity{get;set;}public string? Notes{get;set;} }

public sealed class ManagerCreateOrderRequest { public string OrderType { get; set; } = "Dine In"; public string? Address { get; set; } public int? DeliveryZoneID { get; set; } public string? Notes { get; set; } public List<EditOrderItem> Items { get; set; } = new(); }
