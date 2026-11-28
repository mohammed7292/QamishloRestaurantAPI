using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class deliveryworkersController : ControllerBase
    {
        private static async Task<bool> IsAuthenticatedWorkerAsync(
            MySqlConnection connection,
            int workerId,
            HttpRequest request)
        {
            string username = request.Headers["X-Delivery-Worker-Username"].ToString();
            string password = request.Headers["X-Delivery-Worker-Password"].ToString();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return false;

            const string sql = """
                SELECT COUNT(*)
                FROM deliveryworkers
                WHERE DeliveryWorkerID = @id
                  AND Username = @username
                  AND Password = @password
                  AND IsActive = 1;
                """;
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@id", workerId);
            command.Parameters.AddWithValue("@username", username.Trim());
            command.Parameters.AddWithValue("@password", password);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }

        // =====================================================
        // LOGIN
        // =====================================================

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] DeliveryLoginRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                    return BadRequest(new { success = false, message = "اسم المستخدم وكلمة المرور مطلوبان." });

                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    SELECT DeliveryWorkerID, FullName, Username, Phone, IsActive
                    FROM deliveryworkers
                    WHERE Username=@Username AND Password=@Password
                    LIMIT 1;
                    """;

                int workerId;
                string fullName;
                string username;
                string? phone;
                bool isActive;

                await using (var command = new MySqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Username", request.Username.Trim());
                    command.Parameters.AddWithValue("@Password", request.Password);
                    await using var reader = await command.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                        return Unauthorized(new { success = false, message = "اسم المستخدم أو كلمة المرور غير صحيحة." });

                    workerId = reader.GetInt32("DeliveryWorkerID");
                    fullName = reader.GetString("FullName");
                    username = reader.GetString("Username");
                    phone = reader.IsDBNull(reader.GetOrdinal("Phone")) ? null : reader.GetString("Phone");
                    isActive = reader.GetBoolean("IsActive");
                }

                if (!isActive)
                    return Unauthorized(new { success = false, message = "هذا الحساب غير نشط." });

                const string updateWorkerSql = """
                    UPDATE deliveryworkers
                    SET IsOnShift=1, ShiftStartedAt=UTC_TIMESTAMP(), ShiftEndedAt=NULL,
                        UpdatedAt=UTC_TIMESTAMP()
                    WHERE DeliveryWorkerID=@id AND IsActive=1;
                    """;
                await using (var updateWorker = new MySqlCommand(updateWorkerSql, connection))
                {
                    updateWorker.Parameters.AddWithValue("@id", workerId);
                    await updateWorker.ExecuteNonQueryAsync();
                }

                const string insertShiftSql = """
                    INSERT INTO deliveryworkershifts(DeliveryWorkerID, StartedAt)
                    SELECT @id, UTC_TIMESTAMP()
                    WHERE NOT EXISTS (
                        SELECT 1 FROM deliveryworkershifts
                        WHERE DeliveryWorkerID=@id AND EndedAt IS NULL
                    );
                    """;
                await using (var insertShift = new MySqlCommand(insertShiftSql, connection))
                {
                    insertShift.Parameters.AddWithValue("@id", workerId);
                    await insertShift.ExecuteNonQueryAsync();
                }

                return Ok(new
                {
                    success = true,
                    deliveryWorkerID = workerId,
                    fullName,
                    username,
                    phone,
                    isOnShift = true,
                    message = "Login successful."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "تعذر تسجيل الدخول حالياً. تحقق من اتصال الخادم وحاول مجدداً.",
                    error = "Database operation failed."
                });
            }
        }

        // =====================================================
        // GET WORKER ORDERS
        // =====================================================

        [HttpGet("{workerId}/orders")]
        public async Task<IActionResult> GetWorkerorders(int workerId)
        {
            try
            {
                if (workerId <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid worker ID."
                    });
                }

                await using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                string query = """
                    SELECT
                        o.OrderID,
                        (SELECT COUNT(*) FROM orders o2 WHERE COALESCE(o2.BusinessDate, DATE(o2.OrderDate)) = COALESCE(o.BusinessDate, DATE(o.OrderDate)) AND o2.OrderID <= o.OrderID) AS DailyOrderNumber,
                        o.CustomerID,
                        COALESCE(c.FullName, 'Walk-in Customer') AS CustomerName,
                        COALESCE(c.Phone, '') AS CustomerPhone,
                        o.DeliveryWorkerID,
                        o.DeliveryZoneID,
                        COALESCE(dz.ZoneName, '') AS ZoneName,
                        o.OrderDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        o.CustomerLatitude,
                        o.CustomerLongitude,
                        o.Notes
                    FROM orders o
                    LEFT JOIN customers c ON o.CustomerID = c.CustomerID
                    LEFT JOIN deliveryzones dz ON o.DeliveryZoneID = dz.DeliveryZoneID
                    WHERE o.DeliveryWorkerID = @workerId
                      AND o.IsArchived = 0
                      AND o.Status NOT IN ('Delivered', 'Cancelled', 'Rejected')
                    ORDER BY o.OrderID DESC;
                    """;

                var orderList = new List<WorkerOrderDto>();
                var orderMap = new Dictionary<int, WorkerOrderDto>();

                await using (MySqlCommand command = new MySqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@workerId", workerId);
                    await using MySqlDataReader reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        int orderId = Convert.ToInt32(reader["OrderID"]);
                        int dailyNum = Convert.ToInt32(reader["DailyOrderNumber"]);
                        var dto = new WorkerOrderDto
                        {
                            OrderID = orderId,
                            DailyOrderNumber = dailyNum,
                            OrderNumber = $"Q-{dailyNum}",
                            CustomerID = reader["CustomerID"] == DBNull.Value ? null : Convert.ToInt32(reader["CustomerID"]),
                            CustomerName = reader["CustomerName"]?.ToString() ?? "",
                            CustomerPhone = reader["CustomerPhone"]?.ToString() ?? "",
                            DeliveryWorkerID = reader["DeliveryWorkerID"] == DBNull.Value ? null : Convert.ToInt32(reader["DeliveryWorkerID"]),
                            DeliveryZoneID = reader["DeliveryZoneID"] == DBNull.Value ? null : Convert.ToInt32(reader["DeliveryZoneID"]),
                            ZoneName = reader["ZoneName"]?.ToString() ?? "",
                            OrderDate = Convert.ToDateTime(reader["OrderDate"]),
                            OrderType = reader["OrderType"]?.ToString() ?? "",
                            Status = reader["Status"]?.ToString() ?? "",
                            Subtotal = Convert.ToDecimal(reader["Subtotal"]),
                            DeliveryFee = Convert.ToDecimal(reader["DeliveryFee"]),
                            DiscountAmount = Convert.ToDecimal(reader["DiscountAmount"]),
                            TotalAmount = Convert.ToDecimal(reader["TotalAmount"]),
                            DeliveryAddress = reader["DeliveryAddress"] == DBNull.Value ? "" : reader["DeliveryAddress"]?.ToString() ?? "",
                            CustomerLatitude = reader["CustomerLatitude"] == DBNull.Value ? null : Convert.ToDouble(reader["CustomerLatitude"]),
                            CustomerLongitude = reader["CustomerLongitude"] == DBNull.Value ? null : Convert.ToDouble(reader["CustomerLongitude"]),
                            Notes = reader["Notes"] == DBNull.Value ? "" : reader["Notes"]?.ToString() ?? ""
                        };
                        orderList.Add(dto);
                        orderMap[orderId] = dto;
                    }
                }

                await PopulateWorkerOrderItemsAsync(connection, orderMap);

                return Ok(new
                {
                    success = true,
                    workerID = workerId,
                    count = orderList.Count,
                    orders = orderList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not load worker orders.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        private static async Task PopulateWorkerOrderItemsAsync(
            MySqlConnection connection,
            Dictionary<int, WorkerOrderDto> orderMap)
        {
            if (orderMap.Count == 0)
                return;

            string ids = string.Join(",", orderMap.Keys);
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
                WHERE od.OrderID IN ({ids})
                ORDER BY od.OrderDetailID ASC;";

            await using var cmd = new MySqlCommand(detailsSql, connection);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                int orderId = reader.GetInt32("OrderID");
                if (orderMap.TryGetValue(orderId, out var dto))
                {
                    dto.Items.Add(new WorkerOrderItemDto
                    {
                        ProductID = reader.GetInt32("ProductID"),
                        ProductName = reader.GetString("ProductName"),
                        Quantity = reader.GetInt32("Quantity"),
                        UnitPrice = reader.GetDecimal("UnitPrice"),
                        TotalPrice = reader.GetDecimal("TotalPrice"),
                        Notes = reader.GetString("Notes")
                    });
                }
            }
        }

        // =====================================================
        // UPDATE ORDER STATUS
        // =====================================================

        [HttpPost("{workerId}/orders/{orderId}/status")]
        public async Task<IActionResult> Updateorderstatus(
            int workerId,
            int orderId,
            [FromBody] DeliveryStatusRequest request)
        {
            try
            {
                if (workerId <= 0 || orderId <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid worker or order ID."
                    });
                }

                string status = request.Status?.Trim() ?? "";

                string[] allowedStatuses =
                {
                    "Preparing",
                    "Ready",
                    "OutForDelivery",
                    "Delivered"
                };

                if (!allowedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid order status."
                    });
                }

                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                string checkQuery = """
                    SELECT Status
                    FROM orders
                    WHERE OrderID = @orderID
                      AND DeliveryWorkerID = @workerID
                      AND IsArchived = 0
                    LIMIT 1;
                    """;

                using MySqlCommand checkCommand = new MySqlCommand(checkQuery, connection);
                checkCommand.Parameters.AddWithValue("@orderID", orderId);
                checkCommand.Parameters.AddWithValue("@workerID", workerId);

                object? currentResult = await checkCommand.ExecuteScalarAsync();

                if (currentResult == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "This order is not assigned to this worker."
                    });
                }

                string currentStatus = currentResult.ToString() ?? "";

                bool validFlow =
                    (currentStatus == "Preparing" && (status == "Ready" || status == "OutForDelivery" || status == "Delivered"))
                    || (currentStatus == "Ready" && (status == "OutForDelivery" || status == "Delivered"))
                    || (currentStatus == "OutForDelivery" && status == "Delivered");

                if (!validFlow)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = $"Cannot change order from {currentStatus} to {status}."
                    });
                }

                string updateQuery = """
                    UPDATE orders
                    SET
                        Status = @status,
                        PickedUpAt = CASE WHEN @status='OutForDelivery' AND PickedUpAt IS NULL THEN UTC_TIMESTAMP() ELSE PickedUpAt END,
                        UpdatedAt = UTC_TIMESTAMP()
                    WHERE OrderID = @orderID
                      AND DeliveryWorkerID = @workerID;
                    """;

                using MySqlCommand updateCommand = new MySqlCommand(updateQuery, connection);
                updateCommand.Parameters.AddWithValue("@status", status);
                updateCommand.Parameters.AddWithValue("@orderID", orderId);
                updateCommand.Parameters.AddWithValue("@workerID", workerId);

                int affected = await updateCommand.ExecuteNonQueryAsync();

                if (affected > 0)
                {
                    await using var notificationLookup = new MySqlCommand(
                        "SELECT CustomerID FROM orders WHERE OrderID=@orderID LIMIT 1", connection);
                    notificationLookup.Parameters.AddWithValue("@orderID", orderId);
                    var customerValue = await notificationLookup.ExecuteScalarAsync();
                    if (customerValue != null && customerValue != DBNull.Value)
                    {
                        await using var notificationTx = await connection.BeginTransactionAsync();
                        await NotificationsController.AddOrderStatusNotificationAsync(
                            connection, notificationTx, Convert.ToInt32(customerValue), orderId, status);
                        await notificationTx.CommitAsync();
                        if (string.Equals(status, "Delivered", StringComparison.OrdinalIgnoreCase))
                        {
                            await LoyaltyController.TryAwardCoinsForOrderAsync(connection, orderId);
                        }
                        var fcmSvc = HttpContext.RequestServices.GetService<QamishloRestaurantAPI.Services.FirebaseNotificationService>();
                        if (fcmSvc != null)
                        {
                            string arStatus = status switch
                            {
                                "Preparing" => "قيد التحضير 🍳",
                                "Ready" => "جاهز للتسليم ✅",
                                "OutForDelivery" => "بالطريق إليك الآن 🛵",
                                "Delivered" => "تم التوصيل بنجاح 🎉 (أُضيفت قامشلو كوينز لحسابك!)",
                                _ => status
                            };
                            await fcmSvc.SendToAudienceAsync("customer", Convert.ToInt32(customerValue), "مطعم قامشلو 🍔", $"تحديث طلبك #{orderId}: {arStatus}");
                        }
                    }
                }

                if (affected == 0)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Order could not be updated."
                    });
                }

                return Ok(new
                {
                    success = true,
                    orderID = orderId,
                    workerID = workerId,
                    status = status,
                    message = "Order status updated successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not update order status.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // =====================================================
        // DELIVERED ORDER HISTORY (supports both /history and /delivered-orders)
        // GET: api/deliveryworkers/{workerId}/history
        // GET: api/deliveryworkers/{workerId}/delivered-orders
        // =====================================================

        [HttpGet("{workerId:int}/history")]
        [HttpGet("{workerId:int}/delivered-orders")]
        public async Task<IActionResult> GetDeliveredHistory(
            int workerId,
            [FromQuery] string? from = null,
            [FromQuery] string? to = null,
            [FromQuery] string? fromDate = null,
            [FromQuery] string? toDate = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            if (workerId <= 0)
                return BadRequest(new { success = false, message = "Invalid delivery worker ID." });

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 200);

            string? rawFrom = !string.IsNullOrWhiteSpace(from) ? from : fromDate;
            string? rawTo = !string.IsNullOrWhiteSpace(to) ? to : toDate;

            DateTime? parsedFromDate = DateTime.TryParse(rawFrom, out var pf) ? pf.Date : null;
            DateTime? parsedToDate = DateTime.TryParse(rawTo, out var pt) ? pt.Date : null;
            if (parsedFromDate.HasValue && parsedToDate.HasValue && parsedFromDate > parsedToDate)
                return BadRequest(new { success = false, message = "The from date must be before the to date." });

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();
                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                const string summarySql = """
                    SELECT COUNT(*) AS DeliveredCount,
                           COALESCE(SUM(TotalAmount), 0) AS TotalAmount,
                           COALESCE(AVG(TotalAmount), 0) AS AverageAmount
                    FROM orders
                    WHERE DeliveryWorkerID = @workerId
                      AND Status = 'Delivered'
                      AND (@fromDate IS NULL OR COALESCE(BusinessDate, DATE(OrderDate)) >= @fromDate)
                      AND (@toDate IS NULL OR COALESCE(BusinessDate, DATE(OrderDate)) <= @toDate);
                    """;
                int deliveredCount;
                decimal totalAmount;
                decimal averageAmount;
                await using (var summary = new MySqlCommand(summarySql, connection))
                {
                    summary.Parameters.AddWithValue("@workerId", workerId);
                    summary.Parameters.AddWithValue("@fromDate", (object?)parsedFromDate ?? DBNull.Value);
                    summary.Parameters.AddWithValue("@toDate", (object?)parsedToDate ?? DBNull.Value);
                    await using var r = await summary.ExecuteReaderAsync();
                    await r.ReadAsync();
                    deliveredCount = Convert.ToInt32(r["DeliveredCount"]);
                    totalAmount = Convert.ToDecimal(r["TotalAmount"]);
                    averageAmount = Convert.ToDecimal(r["AverageAmount"]);
                }

                const string ordersSql = """
                    SELECT o.OrderID,
                           (SELECT COUNT(*) FROM orders o2 WHERE COALESCE(o2.BusinessDate, DATE(o2.OrderDate)) = COALESCE(o.BusinessDate, DATE(o.OrderDate)) AND o2.OrderID <= o.OrderID) AS DailyOrderNumber,
                           o.CustomerID,
                           COALESCE(c.FullName, 'Walk-in Customer') AS CustomerName,
                           COALESCE(c.Phone, '') AS CustomerPhone,
                           o.DeliveryWorkerID, o.DeliveryZoneID,
                           COALESCE(dz.ZoneName, '') AS ZoneName,
                           o.OrderDate, o.OrderType, o.Status,
                           o.Subtotal, o.DeliveryFee, o.DiscountAmount, o.TotalAmount,
                           o.DeliveryAddress, o.Notes
                    FROM orders o
                    LEFT JOIN customers c ON c.CustomerID = o.CustomerID
                    LEFT JOIN deliveryzones dz ON dz.DeliveryZoneID = o.DeliveryZoneID
                    WHERE o.DeliveryWorkerID = @workerId
                      AND o.Status = 'Delivered'
                      AND (@fromDate IS NULL OR COALESCE(o.BusinessDate, DATE(o.OrderDate)) >= @fromDate)
                      AND (@toDate IS NULL OR COALESCE(o.BusinessDate, DATE(o.OrderDate)) <= @toDate)
                    ORDER BY o.OrderDate DESC, o.OrderID DESC
                    LIMIT @limit OFFSET @offset;
                    """;
                var orderList = new List<WorkerOrderDto>();
                var orderMap = new Dictionary<int, WorkerOrderDto>();
                await using (var command = new MySqlCommand(ordersSql, connection))
                {
                    command.Parameters.AddWithValue("@workerId", workerId);
                    command.Parameters.AddWithValue("@fromDate", (object?)parsedFromDate ?? DBNull.Value);
                    command.Parameters.AddWithValue("@toDate", (object?)parsedToDate ?? DBNull.Value);
                    command.Parameters.AddWithValue("@limit", pageSize);
                    command.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        int orderId = Convert.ToInt32(reader["OrderID"]);
                        int dailyNum = Convert.ToInt32(reader["DailyOrderNumber"]);
                        var dto = new WorkerOrderDto
                        {
                            OrderID = orderId,
                            DailyOrderNumber = dailyNum,
                            OrderNumber = $"Q-{dailyNum}",
                            CustomerID = reader["CustomerID"] == DBNull.Value ? null : Convert.ToInt32(reader["CustomerID"]),
                            CustomerName = reader["CustomerName"]?.ToString() ?? "",
                            CustomerPhone = reader["CustomerPhone"]?.ToString() ?? "",
                            DeliveryWorkerID = Convert.ToInt32(reader["DeliveryWorkerID"]),
                            DeliveryZoneID = reader["DeliveryZoneID"] == DBNull.Value ? null : Convert.ToInt32(reader["DeliveryZoneID"]),
                            ZoneName = reader["ZoneName"]?.ToString() ?? "",
                            OrderDate = Convert.ToDateTime(reader["OrderDate"]),
                            OrderType = reader["OrderType"]?.ToString() ?? "",
                            Status = reader["Status"]?.ToString() ?? "",
                            Subtotal = Convert.ToDecimal(reader["Subtotal"]),
                            DeliveryFee = Convert.ToDecimal(reader["DeliveryFee"]),
                            DiscountAmount = Convert.ToDecimal(reader["DiscountAmount"]),
                            TotalAmount = Convert.ToDecimal(reader["TotalAmount"]),
                            DeliveryAddress = reader["DeliveryAddress"] == DBNull.Value ? "" : reader["DeliveryAddress"]?.ToString() ?? "",
                            Notes = reader["Notes"] == DBNull.Value ? "" : reader["Notes"]?.ToString() ?? ""
                        };
                        orderList.Add(dto);
                        orderMap[orderId] = dto;
                    }
                }

                await PopulateWorkerOrderItemsAsync(connection, orderMap);

                return Ok(new
                {
                    success = true,
                    workerID = workerId,
                    from = parsedFromDate,
                    to = parsedToDate,
                    page,
                    pageSize,
                    deliveredCount,
                    totalAmount,
                    averageAmount,
                    currency = "IQD",
                    orders = orderList
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not load the delivered-order history.",
                    error = "تعذر تحميل سجل الطلبات المسلّمة بسبب خطأ داخلي."
                });
            }
        }

        // =====================================================
        // TODAY'S DELIVERY STATISTICS
        // =====================================================

        [HttpGet("{workerId:int}/today-stats")]
        public async Task<IActionResult> GetTodayStats(int workerId)
        {
            try
            {
                if (workerId <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Invalid delivery worker ID."
                    });
                }

                using MySqlConnection connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                string query = """
                    SELECT
                        COUNT(*) AS Deliveredorders,
                        COALESCE(SUM(TotalAmount), 0) AS TotalAmount
                    FROM orders
                    WHERE DeliveryWorkerID = @workerId
                      AND BusinessDate = @businessDate
                      AND Status = 'Delivered';
                    """;

                using MySqlCommand command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@workerId", workerId);
                command.Parameters.AddWithValue(
                    "@businessDate",
                    ExpensesController.BusinessDate(ExpensesController.BaghdadNow()));

                using MySqlDataReader reader = await command.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    int deliveredorders = Convert.ToInt32(reader["Deliveredorders"]);
                    decimal totalAmount = Convert.ToDecimal(reader["TotalAmount"]);

                    return Ok(new
                    {
                        success = true,
                        workerID = workerId,
                        date = ExpensesController.BusinessDate(ExpensesController.BaghdadNow()).ToString("yyyy-MM-dd"),
                        deliveredorders = deliveredorders,
                        totalAmount = totalAmount
                    });
                }

                return Ok(new
                {
                    success = true,
                    workerID = workerId,
                    date = ExpensesController.BusinessDate(ExpensesController.BaghdadNow()).ToString("yyyy-MM-dd"),
                    deliveredorders = 0,
                    totalAmount = 0
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not load today's statistics.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }
    }

    public class DeliveryLoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class DeliveryStatusRequest
    {
        public string Status { get; set; } = "";
    }

    public class WorkerOrderDto
    {
        public int OrderID { get; set; }
        public int DailyOrderNumber { get; set; }
        public string OrderNumber { get; set; } = "";
        public int? CustomerID { get; set; }
        public string CustomerName { get; set; } = "";
        public string CustomerPhone { get; set; } = "";
        public int? DeliveryWorkerID { get; set; }
        public int? DeliveryZoneID { get; set; }
        public string ZoneName { get; set; } = "";
        public DateTime OrderDate { get; set; }
        public string OrderType { get; set; } = "";
        public string Status { get; set; } = "";
        public decimal Subtotal { get; set; }
        public decimal DeliveryFee { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal TotalAmount { get; set; }
        public string DeliveryAddress { get; set; } = "";
        public double? CustomerLatitude { get; set; }
        public double? CustomerLongitude { get; set; }
        public string Notes { get; set; } = "";
        public List<WorkerOrderItemDto> Items { get; set; } = new();
    }

    public class WorkerOrderItemDto
    {
        public int ProductID { get; set; }
        public string ProductName { get; set; } = "";
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalPrice { get; set; }
        public string Notes { get; set; } = "";
    }
}