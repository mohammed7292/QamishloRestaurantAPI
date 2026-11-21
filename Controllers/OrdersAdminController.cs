using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/orders")]
public sealed class OrdersAdminController : ControllerBase
{
    private object? orderId;

    // Manager-created order (dine-in, takeaway, or delivery). Customer identity is optional.
    [HttpPost("manager")]
    public async Task<IActionResult> CreateManagerOrder([FromBody] ManagerCreateOrderRequest request)
    {
        int? managerUserId = await ManagerCredentialValidator.GetManagerUserIdAsync(Request);
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
            int? customerId = null;
            string customerName = request.CustomerName?.Trim() ?? "";
            string customerPhone = request.CustomerPhone?.Trim() ?? "";

            if (!string.IsNullOrWhiteSpace(customerPhone))
            {
                await using (var findCmd = new MySqlCommand("SELECT CustomerID FROM customers WHERE Phone=@Phone LIMIT 1", connection, transaction))
                {
                    findCmd.Parameters.AddWithValue("@Phone", customerPhone);
                    var found = await findCmd.ExecuteScalarAsync();
                    if (found != null && found != DBNull.Value)
                        customerId = Convert.ToInt32(found);
                }

                if (!customerId.HasValue)
                {
                    await using var insertCust = new MySqlCommand(
                        "INSERT INTO customers(FullName,Phone,Address,Notes,IsActive) VALUES(@FullName,@Phone,@Address,@Notes,1); SELECT LAST_INSERT_ID();",
                        connection, transaction);
                    insertCust.Parameters.AddWithValue("@FullName", string.IsNullOrWhiteSpace(customerName) ? "زبون مباشر" : customerName);
                    insertCust.Parameters.AddWithValue("@Phone", customerPhone);
                    insertCust.Parameters.AddWithValue("@Address", string.IsNullOrWhiteSpace(request.Address) ? DBNull.Value : request.Address.Trim());
                    insertCust.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                    customerId = Convert.ToInt32(await insertCust.ExecuteScalarAsync());
                }
            }
            else if (!string.IsNullOrWhiteSpace(customerName))
            {
                await using var insertCust = new MySqlCommand(
                    "INSERT INTO customers(FullName,Phone,Address,Notes,IsActive) VALUES(@FullName,NULL,@Address,@Notes,1); SELECT LAST_INSERT_ID();",
                    connection, transaction);
                insertCust.Parameters.AddWithValue("@FullName", customerName);
                insertCust.Parameters.AddWithValue("@Address", string.IsNullOrWhiteSpace(request.Address) ? DBNull.Value : request.Address.Trim());
                insertCust.Parameters.AddWithValue("@Notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                customerId = Convert.ToInt32(await insertCust.ExecuteScalarAsync());
            }

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
            var lines = new List<(int ProductId, int Quantity, string Name, decimal Price, string? Notes)>();
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
                lines.Add((item.ProductID, item.Quantity, name, price, item.Notes));
                subtotal += price * item.Quantity;
            }

            DateTime now = ExpensesController.BaghdadNow();
            DateTime businessDate = ExpensesController.BusinessDate(now);

            int resolvedUserId = await ManagerCredentialValidator.ResolveValidOrderUserIdAsync(connection, transaction, managerUserId);

            int dailyOrderNumber = 1;
            await using (var nextNumCmd = new MySqlCommand(@"
                SELECT COUNT(*) + 1 FROM orders WHERE COALESCE(BusinessDate, DATE(OrderDate)) = @business AND IsArchived = 0;", connection, transaction))
            {
                nextNumCmd.Parameters.AddWithValue("@business", businessDate);
                var nextVal = await nextNumCmd.ExecuteScalarAsync();
                if (nextVal != null && nextVal != DBNull.Value)
                    dailyOrderNumber = Math.Max(1, Convert.ToInt32(nextVal));
            }

            const string insertOrder = @"INSERT INTO orders
                (CustomerID, UserID, DeliveryWorkerID, DeliveryZoneID, OrderDate, BusinessDate, OrderType, Status,
                 Subtotal, DeliveryFee, DiscountAmount, TotalAmount, DeliveryAddress, CustomerLatitude, CustomerLongitude, Notes)
                VALUES (@customer, @user, NULL, @zone, @date, @business, @type, 'New', @subtotal, @fee, 0, @total, @address, NULL, NULL, @notes);
                SELECT LAST_INSERT_ID();";
            await using var orderCommand = new MySqlCommand(insertOrder, connection, transaction);
            orderCommand.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
            orderCommand.Parameters.AddWithValue("@user", resolvedUserId);
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
                lineCommand.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(line.Notes) ? DBNull.Value : line.Notes.Trim());
                await lineCommand.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Ok(new
            {
                success = true,
                orderId,
                dailyOrderNumber,
                orderNumber = $"Q-{dailyOrderNumber}",
                subtotal,
                deliveryFee,
                total = subtotal + deliveryFee,
                orderType
            });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // End the current accounting day: archive completed/closed orders (keeping them in DB for reports)
    // so new orders for the new day/shift start from #1 (Q-1).
    [HttpPost("end-accounting-day")]
    public async Task<IActionResult> EndAccountingDay([FromQuery] bool forceAll = false)
    {
        // Allow authenticated manager or read-only summary fetch

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            const string activeCountSql = @"
                SELECT COUNT(*)
                FROM orders
                WHERE IsArchived = 0
                  AND Status IN ('New', 'Preparing', 'Ready', 'OutForDelivery');";
            await using (var activeCmd = new MySqlCommand(activeCountSql, connection, transaction))
            {
                int activeCount = Convert.ToInt32(await activeCmd.ExecuteScalarAsync());
                if (activeCount > 0 && !forceAll)
                {
                    await transaction.RollbackAsync();
                    return Conflict(new
                    {
                        success = false,
                        activeCount,
                        message = $"يوجد حالياً {activeCount} طلب قيد التنفيذ لم يكتمل بعد. أكمل تسليمها أو اختر أرشفة كافة الطلبات لبدء يوم جديد من رقم 1."
                    });
                }
            }

            string archiveSql = forceAll
                ? "UPDATE orders SET IsArchived = 1, UpdatedAt = UTC_TIMESTAMP() WHERE IsArchived = 0;"
                : "UPDATE orders SET IsArchived = 1, UpdatedAt = UTC_TIMESTAMP() WHERE IsArchived = 0 AND Status IN ('Delivered', 'Cancelled', 'Rejected');";
            await using var archiveCmd = new MySqlCommand(archiveSql, connection, transaction);
            int archivedCount = await archiveCmd.ExecuteNonQueryAsync();

            await transaction.CommitAsync();
            return Ok(new
            {
                success = true,
                archivedCount,
                nextOrderNumber = 1,
                message = $"تم حفظ وأرشفة {archivedCount} طلب من اليوم السابق في السجل والتقارير، وستبدأ الطلبات الجديدة من رقم Q-1."
            });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // Dedicated endpoint for the new "طلبات التوصيل" (Delivery Orders Summary) page:
    // Shows each delivery worker, how many orders they took, and the full list of those orders & items.
    [HttpGet("delivery-summary")]
    public async Task<IActionResult> GetDeliveryWorkersSummary([FromQuery] string period = "today")
    {
        // Allow authenticated manager or read-only summary fetch

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();

        DateTime currentBusinessDate = ExpensesController.BusinessDate(ExpensesController.BaghdadNow());
        bool filterToday = string.Equals(period, "today", StringComparison.OrdinalIgnoreCase);

        const string workersSql = @"
            SELECT DeliveryWorkerID, FullName, COALESCE(Phone, '') AS Phone, IsActive, IsOnShift
            FROM deliveryworkers
            ORDER BY IsActive DESC, IsOnShift DESC, FullName ASC;";

        var workersMap = new Dictionary<int, DeliveryWorkerSummaryDto>();
        await using (var wCmd = new MySqlCommand(workersSql, connection))
        await using (var wReader = await wCmd.ExecuteReaderAsync())
        {
            while (await wReader.ReadAsync())
            {
                int wId = wReader.GetInt32("DeliveryWorkerID");
                workersMap[wId] = new DeliveryWorkerSummaryDto
                {
                    DeliveryWorkerID = wId,
                    FullName = wReader.GetString("FullName"),
                    Phone = wReader.GetString("Phone"),
                    IsActive = wReader.GetBoolean("IsActive"),
                    IsOnShift = wReader.GetBoolean("IsOnShift")
                };
            }
        }

        string ordersSql = @"
            SELECT
                o.OrderID,
                (SELECT COUNT(*) FROM orders o2 WHERE COALESCE(o2.BusinessDate, DATE(o2.OrderDate)) = COALESCE(o.BusinessDate, DATE(o.OrderDate)) AND o2.OrderID <= o.OrderID) AS DailyOrderNumber,
                o.DeliveryWorkerID,
                COALESCE(dw.FullName, 'غير مسند') AS DeliveryWorkerName,
                COALESCE(c.FullName, 'زبون توصيل') AS CustomerName,
                COALESCE(c.Phone, '') AS CustomerPhone,
                COALESCE(dz.ZoneName, '') AS ZoneName,
                COALESCE(o.DeliveryAddress, '') AS DeliveryAddress,
                o.Status,
                o.Subtotal,
                o.DeliveryFee,
                o.DiscountAmount,
                o.TotalAmount,
                o.OrderDate,
                DATE_FORMAT(COALESCE(o.BusinessDate, DATE(o.OrderDate)), '%Y-%m-%d') AS BusinessDateStr,
                COALESCE(o.Notes, '') AS Notes
            FROM orders o
            LEFT JOIN deliveryworkers dw ON dw.DeliveryWorkerID = o.DeliveryWorkerID
            LEFT JOIN customers c ON c.CustomerID = o.CustomerID
            LEFT JOIN deliveryzones dz ON dz.DeliveryZoneID = o.DeliveryZoneID
            WHERE (o.OrderType = 'Delivery' OR o.DeliveryWorkerID IS NOT NULL)
              AND o.Status NOT IN ('Cancelled', 'Rejected')
        ";

        if (filterToday)
        {
            ordersSql += " AND COALESCE(o.BusinessDate, DATE(DATE_SUB(o.OrderDate, INTERVAL 2 HOUR))) = @businessDate ";
        }

        ordersSql += " ORDER BY o.OrderID DESC LIMIT 500;";

        var allOrders = new List<DeliveryOrderEntryDto>();
        var orderById = new Dictionary<int, DeliveryOrderEntryDto>();

        await using (var oCmd = new MySqlCommand(ordersSql, connection))
        {
            if (filterToday)
                oCmd.Parameters.AddWithValue("@businessDate", currentBusinessDate);

            await using var oReader = await oCmd.ExecuteReaderAsync();
            while (await oReader.ReadAsync())
            {
                int orderId = oReader.GetInt32("OrderID");
                int dailyNum = oReader.GetInt32("DailyOrderNumber");
                int? workerId = oReader.IsDBNull(oReader.GetOrdinal("DeliveryWorkerID"))
                    ? null
                    : oReader.GetInt32("DeliveryWorkerID");

                var dto = new DeliveryOrderEntryDto
                {
                    OrderID = orderId,
                    DailyOrderNumber = dailyNum,
                    OrderNumber = $"Q-{dailyNum}",
                    DeliveryWorkerID = workerId,
                    DeliveryWorkerName = oReader.GetString("DeliveryWorkerName"),
                    CustomerName = oReader.GetString("CustomerName"),
                    CustomerPhone = oReader.GetString("CustomerPhone"),
                    ZoneName = oReader.GetString("ZoneName"),
                    DeliveryAddress = oReader.GetString("DeliveryAddress"),
                    Status = oReader.GetString("Status"),
                    Subtotal = oReader.GetDecimal("Subtotal"),
                    DeliveryFee = oReader.GetDecimal("DeliveryFee"),
                    DiscountAmount = oReader.GetDecimal("DiscountAmount"),
                    TotalAmount = oReader.GetDecimal("TotalAmount"),
                    OrderDate = oReader.GetDateTime("OrderDate"),
                    BusinessDate = oReader.GetString("BusinessDateStr"),
                    Notes = oReader.GetString("Notes")
                };

                allOrders.Add(dto);
                orderById[orderId] = dto;
            }
        }

        if (orderById.Count > 0)
        {
            string idList = string.Join(",", orderById.Keys);
            string detailsSql = $@"
                SELECT
                    od.OrderID,
                    od.ProductID,
                    CASE WHEN COALESCE(od.AddonsText, '') <> '' THEN CONCAT(COALESCE(p.ProductName, CONCAT('منتج #', od.ProductID)), ' (+ ', od.AddonsText, ')') ELSE COALESCE(p.ProductName, CONCAT('منتج #', od.ProductID)) END AS ProductName,
                    od.Quantity,
                    od.UnitPrice,
                    od.TotalPrice,
                    COALESCE(od.Notes, '') AS Notes
                FROM orderdetails od
                LEFT JOIN products p ON p.ProductID = od.ProductID
                WHERE od.OrderID IN ({idList})
                ORDER BY od.OrderDetailID ASC;";

            await using var dCmd = new MySqlCommand(detailsSql, connection);
            await using var dReader = await dCmd.ExecuteReaderAsync();
            while (await dReader.ReadAsync())
            {
                int ordId = dReader.GetInt32("OrderID");
                if (orderById.TryGetValue(ordId, out var ord))
                {
                    ord.Items.Add(new DeliveryOrderItemDto
                    {
                        ProductID = dReader.GetInt32("ProductID"),
                        ProductName = dReader.GetString("ProductName"),
                        Quantity = dReader.GetInt32("Quantity"),
                        UnitPrice = dReader.GetDecimal("UnitPrice"),
                        TotalPrice = dReader.GetDecimal("TotalPrice"),
                        Notes = dReader.GetString("Notes")
                    });
                }
            }
        }

        foreach (var ord in allOrders)
        {
            ord.ItemsSummary = ord.Items.Count > 0
                ? string.Join(" ، ", ord.Items.Select(i => $"{i.ProductName} × {i.Quantity}"))
                : "بدون تفاصيل منتجات";

            if (ord.DeliveryWorkerID.HasValue)
            {
                if (!workersMap.TryGetValue(ord.DeliveryWorkerID.Value, out var workerSummary))
                {
                    workerSummary = new DeliveryWorkerSummaryDto
                    {
                        DeliveryWorkerID = ord.DeliveryWorkerID.Value,
                        FullName = ord.DeliveryWorkerName,
                        IsActive = false,
                        IsOnShift = false
                    };
                    workersMap[ord.DeliveryWorkerID.Value] = workerSummary;
                }

                workerSummary.Orders.Add(ord);
                workerSummary.TotalOrdersCount++;
                workerSummary.TotalAmount += ord.TotalAmount;
                workerSummary.TotalDeliveryFees += ord.DeliveryFee;
                if (string.Equals(ord.Status, "Delivered", StringComparison.OrdinalIgnoreCase))
                {
                    workerSummary.DeliveredOrdersCount++;
                    workerSummary.DeliveredAmount += ord.TotalAmount;
                }
                else
                {
                    workerSummary.ActiveOrdersCount++;
                }
            }
        }

        return Ok(new
        {
            success = true,
            period,
            businessDate = currentBusinessDate.ToString("yyyy-MM-dd"),
            totalDeliveryOrders = allOrders.Count,
            totalDeliveryAmount = allOrders.Sum(o => o.TotalAmount),
            workers = workersMap.Values.OrderByDescending(w => w.TotalOrdersCount).ThenBy(w => w.FullName).ToList()
        });
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
            await using(var exists=new MySqlCommand("SELECT COUNT(*) FROM orders WHERE OrderID=@id AND IsArchived=0 AND Status NOT IN ('Delivered','Cancelled','Rejected')",c,tx)){exists.Parameters.AddWithValue("@id",id);if(Convert.ToInt32(await exists.ExecuteScalarAsync())==0){await tx.RollbackAsync();return NotFound(new{success=false,message="Order not found or already closed."});}}
            await using(var del=new MySqlCommand("DELETE FROM orderdetails WHERE OrderID=@id",c,tx)){del.Parameters.AddWithValue("@id",id);await del.ExecuteNonQueryAsync();}
            foreach(var item in request.Items)
            {
                await using var p=new MySqlCommand("SELECT ProductName,Price FROM products WHERE ProductID=@id",c,tx);p.Parameters.AddWithValue("@id",item.ProductID);await using var r=await p.ExecuteReaderAsync();await r.ReadAsync();var name=r.GetString("ProductName");var price=r.GetDecimal("Price");await r.CloseAsync();
                await using var insert=new MySqlCommand("INSERT INTO orderdetails(OrderID,ProductID,Quantity,UnitPrice,TotalPrice,Notes) VALUES(@order,@product,@qty,@price,@total,@notes)",c,tx);insert.Parameters.AddWithValue("@order",id);insert.Parameters.AddWithValue("@product",item.ProductID);insert.Parameters.AddWithValue("@qty",item.Quantity);insert.Parameters.AddWithValue("@price",price);insert.Parameters.AddWithValue("@total",price*item.Quantity);insert.Parameters.AddWithValue("@notes",(object?)item.Notes??DBNull.Value);await insert.ExecuteNonQueryAsync();
            }
            decimal delivery=request.DeliveryFee<0?0:request.DeliveryFee; decimal discount=Math.Clamp(request.DiscountAmount,0m,subtotal+delivery); decimal total=Math.Max(0,subtotal+delivery-discount);
            await using(var update=new MySqlCommand("UPDATE orders SET Subtotal=@subtotal,DeliveryFee=@fee,DiscountAmount=@discount,TotalAmount=@total,Notes=@notes,UpdatedAt=NOW() WHERE OrderID=@id",c,tx)){update.Parameters.AddWithValue("@subtotal",subtotal);update.Parameters.AddWithValue("@fee",delivery);update.Parameters.AddWithValue("@discount",discount);update.Parameters.AddWithValue("@total",total);update.Parameters.AddWithValue("@notes",(object?)request.Notes??DBNull.Value);update.Parameters.AddWithValue("@id",id);await update.ExecuteNonQueryAsync();}
            await tx.CommitAsync();
            if (!string.IsNullOrWhiteSpace(request.ClientOrderGuid))
            {
                try
                {
                    await using var ins = new MySqlCommand("INSERT IGNORE INTO offlinesyncorders (ClientOrderGuid, OrderID) VALUES (@g, @oid);", c);
                    ins.Parameters.AddWithValue("@g", request.ClientOrderGuid.Trim());
                    ins.Parameters.AddWithValue("@oid", orderId);
                    await ins.ExecuteNonQueryAsync();
                }
                catch { }
            }return Ok(new{success=true,orderID=id,subtotal,deliveryFee=delivery,discountAmount=discount,total});
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

public sealed class EditOrderRequest { public List<EditOrderItem> Items{get;set;}=new();public decimal DeliveryFee{get;set;}public decimal DiscountAmount{get;set;}public string? Notes{get;set;}
    public string? ClientOrderGuid { get; internal set; }
}
public sealed class EditOrderItem { public int ProductID{get;set;}public int Quantity{get;set;}public string? Notes{get;set;} }
public sealed class ManagerCreateOrderRequest
{
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string OrderType { get; set; } = "Dine In";
    public string? Address { get; set; }
    public int? DeliveryZoneID { get; set; }
    public string? Notes { get; set; }
    public string? ClientOrderGuid { get; set; }
    public List<EditOrderItem> Items { get; set; } = new();
}

public sealed class DeliveryWorkerSummaryDto
{
    public int DeliveryWorkerID { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool IsActive { get; set; }
    public bool IsOnShift { get; set; }
    public int TotalOrdersCount { get; set; }
    public int DeliveredOrdersCount { get; set; }
    public int ActiveOrdersCount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DeliveredAmount { get; set; }
    public decimal TotalDeliveryFees { get; set; }
    public List<DeliveryOrderEntryDto> Orders { get; set; } = new();
}

public sealed class DeliveryOrderEntryDto
{
    public int OrderID { get; set; }
    public int DailyOrderNumber { get; set; }
    public string OrderNumber { get; set; } = "";
    public int? DeliveryWorkerID { get; set; }
    public string DeliveryWorkerName { get; set; } = "";
    public string CustomerName { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public string DeliveryAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal DeliveryFee { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime OrderDate { get; set; }
    public string BusinessDate { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ItemsSummary { get; set; } = "";
    public List<DeliveryOrderItemDto> Items { get; set; } = new();
}

public sealed class DeliveryOrderItemDto
{
    public int ProductID { get; set; }
    public string ProductName { get; set; } = "";
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public string Notes { get; set; } = "";
}
