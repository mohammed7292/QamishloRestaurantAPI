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
        public async Task<IActionResult> Login(
            [FromBody] DeliveryLoginRequest request)
        {
            try
            {
                if (
                    string.IsNullOrWhiteSpace(request.Username) ||
                    string.IsNullOrWhiteSpace(request.Password)
                )
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Username and password are required."
                    });
                }

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                string query = """
                    SELECT
                        DeliveryWorkerID,
                        FullName,
                        Username,
                        Phone,
                        IsActive
                    FROM deliveryworkers
                    WHERE Username = @Username
                      AND Password = @Password
                    LIMIT 1;
                    """;

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection
                    );

                command.Parameters.AddWithValue(
                    "@Username",
                    request.Username.Trim()
                );

                command.Parameters.AddWithValue(
                    "@Password",
                    request.Password
                );

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message =
                            "Invalid username or password."
                    });
                }

                bool isActive =
                    Convert.ToBoolean(
                        reader["IsActive"]
                    );

                if (!isActive)
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message =
                            "This delivery worker account is inactive."
                    });
                }

                return Ok(new
                {
                    success = true,

                    deliveryWorkerID =
                        Convert.ToInt32(
                            reader["DeliveryWorkerID"]
                        ),

                    fullName =
                        reader["FullName"]?.ToString(),

                    username =
                        reader["Username"]?.ToString(),

                    phone =
                        reader["Phone"] == DBNull.Value
                            ? null
                            : reader["Phone"]?.ToString(),

                    message =
                        "Login successful."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Database error.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    }
                );
            }
        }


        // =====================================================
        // GET WORKER orders
        // =====================================================

        [HttpGet("{workerId}/orders")]
        public async Task<IActionResult> GetWorkerorders(
            int workerId)
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

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                string query = """
                    SELECT
                        o.OrderID,
                        o.CustomerID,

                        COALESCE(
                            c.FullName,
                            'Walk-in Customer'
                        ) AS CustomerName,

                        COALESCE(
                            c.Phone,
                            ''
                        ) AS CustomerPhone,

                        o.DeliveryWorkerID,
                        o.DeliveryZoneID,

                        COALESCE(
                            dz.ZoneName,
                            ''
                        ) AS ZoneName,

                        o.OrderDate,
                        o.OrderType,
                        o.Status,
                        o.Subtotal,
                        o.DeliveryFee,
                        o.DiscountAmount,
                        o.TotalAmount,
                        o.DeliveryAddress,
                        o.Notes

                    FROM orders o

                    LEFT JOIN customers c
                        ON o.CustomerID =
                           c.CustomerID

                    LEFT JOIN deliveryzones dz
                        ON o.DeliveryZoneID =
                           dz.DeliveryZoneID

                    WHERE o.DeliveryWorkerID =
                          @workerId
                      AND o.IsArchived = 0
                      AND o.Status NOT IN
                          ('Delivered', 'Cancelled')

                    ORDER BY
                        o.OrderID DESC;
                    """;

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection
                    );

                command.Parameters.AddWithValue(
                    "@workerId",
                    workerId
                );

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                List<object> orders =
                    new List<object>();

                while (await reader.ReadAsync())
                {
                    orders.Add(new
                    {
                        OrderID =
                            Convert.ToInt32(
                                reader["OrderID"]
                            ),

                        CustomerID =
                            reader["CustomerID"] ==
                            DBNull.Value
                                ? (int?)null
                                : Convert.ToInt32(
                                    reader["CustomerID"]
                                ),

                        CustomerName =
                            reader["CustomerName"]?
                                .ToString() ?? "",

                        CustomerPhone =
                            reader["CustomerPhone"]?
                                .ToString() ?? "",

                        DeliveryWorkerID =
                            reader["DeliveryWorkerID"] ==
                            DBNull.Value
                                ? (int?)null
                                : Convert.ToInt32(
                                    reader[
                                        "DeliveryWorkerID"
                                    ]
                                ),

                        DeliveryZoneID =
                            reader["DeliveryZoneID"] ==
                            DBNull.Value
                                ? (int?)null
                                : Convert.ToInt32(
                                    reader[
                                        "DeliveryZoneID"
                                    ]
                                ),

                        ZoneName =
                            reader["ZoneName"]?
                                .ToString() ?? "",

                        OrderDate =
                            Convert.ToDateTime(
                                reader["OrderDate"]
                            ),

                        OrderType =
                            reader["OrderType"]?
                                .ToString() ?? "",

                        Status =
                            reader["Status"]?
                                .ToString() ?? "",

                        Subtotal =
                            Convert.ToDecimal(
                                reader["Subtotal"]
                            ),

                        DeliveryFee =
                            Convert.ToDecimal(
                                reader["DeliveryFee"]
                            ),

                        DiscountAmount =
                            Convert.ToDecimal(
                                reader["DiscountAmount"]
                            ),

                        TotalAmount =
                            Convert.ToDecimal(
                                reader["TotalAmount"]
                            ),

                        DeliveryAddress =
                            reader["DeliveryAddress"] ==
                            DBNull.Value
                                ? ""
                                : reader[
                                    "DeliveryAddress"
                                  ]?.ToString() ?? "",

                        Notes =
                            reader["Notes"] ==
                            DBNull.Value
                                ? ""
                                : reader[
                                    "Notes"
                                  ]?.ToString() ?? ""
                    });
                }

                return Ok(new
                {
                    success = true,
                    workerID = workerId,
                    count = orders.Count,
                    orders = orders
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            "Could not load worker orders.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    }
                );
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
                if (
                    workerId <= 0 ||
                    orderId <= 0
                )
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Invalid worker or order ID."
                    });
                }

                string status =
                    request.Status?.Trim() ?? "";

                string[] allowedStatuses =
                {
                    "Preparing",
                    "Ready",
                    "OutForDelivery",
                    "Delivered"
                };

                if (
                    !allowedStatuses.Contains(
                        status,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Invalid order status."
                    });
                }

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                // =================================================
                // CHECK ORDER
                // =================================================

                string checkQuery = """
                    SELECT Status
                    FROM orders
                    WHERE OrderID = @orderID
                      AND DeliveryWorkerID = @workerID
                      AND IsArchived = 0
                    LIMIT 1;
                    """;

                using MySqlCommand checkCommand =
                    new MySqlCommand(
                        checkQuery,
                        connection
                    );

                checkCommand.Parameters.AddWithValue(
                    "@orderID",
                    orderId
                );

                checkCommand.Parameters.AddWithValue(
                    "@workerID",
                    workerId
                );

                object? currentResult =
                    await checkCommand.ExecuteScalarAsync();

                if (currentResult == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message =
                            "This order is not assigned to this worker."
                    });
                }

                string currentStatus =
                    currentResult.ToString() ?? "";


                // =================================================
                // VALIDATE STATUS FLOW
                // =================================================

                bool validFlow =
                    (
                        currentStatus == "New" &&
                        status == "Preparing"
                    )
                    ||
                    (
                        currentStatus == "Preparing" &&
                        status == "Ready"
                    )
                    ||
                    (
                        currentStatus == "Ready" &&
                        (status == "OutForDelivery" || status == "Delivered")
                    )
                    ||
                    (
                        currentStatus == "OutForDelivery" &&
                        status == "Delivered"
                    );

                if (!validFlow)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            $"Cannot change order from {currentStatus} to {status}."
                    });
                }


                // =================================================
                // UPDATE STATUS
                // =================================================

                string updateQuery = """
                    UPDATE orders
                    SET
                        Status = @status,
                        UpdatedAt = NOW()
                    WHERE OrderID = @orderID
                      AND DeliveryWorkerID = @workerID;
                    """;

                using MySqlCommand updateCommand =
                    new MySqlCommand(
                        updateQuery,
                        connection
                    );

                updateCommand.Parameters.AddWithValue(
                    "@status",
                    status
                );

                updateCommand.Parameters.AddWithValue(
                    "@orderID",
                    orderId
                );

                updateCommand.Parameters.AddWithValue(
                    "@workerID",
                    workerId
                );

                int affected =
                    await updateCommand.ExecuteNonQueryAsync();

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
                    }
                }

                if (affected == 0)
                {
                    return NotFound(new
                    {
                        success = false,
                        message =
                            "Order could not be updated."
                    });
                }

                return Ok(new
                {
                    success = true,
                    orderID = orderId,
                    workerID = workerId,
                    status = status,
                    message =
                        "Order status updated successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            "Could not update order status.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    }
                );
            }
        }


        // =====================================================
        // DELIVERED ORDER HISTORY
        // GET: api/deliveryworkers/{workerId}/history
        // =====================================================

        [HttpGet("{workerId:int}/history")]
        public async Task<IActionResult> GetDeliveredHistory(
            int workerId,
            [FromQuery] string? from = null,
            [FromQuery] string? to = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            if (workerId <= 0)
                return BadRequest(new { success = false, message = "Invalid delivery worker ID." });

            page = Math.Max(1, page);
            pageSize = Math.Clamp(pageSize, 1, 100);

            DateTime? fromDate = DateTime.TryParse(from, out var parsedFrom) ? parsedFrom.Date : null;
            DateTime? toDate = DateTime.TryParse(to, out var parsedTo) ? parsedTo.Date : null;
            if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
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
                      AND (@fromDate IS NULL OR BusinessDate >= @fromDate)
                      AND (@toDate IS NULL OR BusinessDate <= @toDate);
                    """;
                int deliveredCount;
                decimal totalAmount;
                decimal averageAmount;
                await using (var summary = new MySqlCommand(summarySql, connection))
                {
                    summary.Parameters.AddWithValue("@workerId", workerId);
                    summary.Parameters.AddWithValue("@fromDate", (object?)fromDate ?? DBNull.Value);
                    summary.Parameters.AddWithValue("@toDate", (object?)toDate ?? DBNull.Value);
                    await using var r = await summary.ExecuteReaderAsync();
                    await r.ReadAsync();
                    deliveredCount = Convert.ToInt32(r["DeliveredCount"]);
                    totalAmount = Convert.ToDecimal(r["TotalAmount"]);
                    averageAmount = Convert.ToDecimal(r["AverageAmount"]);
                }

                const string ordersSql = """
                    SELECT o.OrderID, o.CustomerID,
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
                      AND (@fromDate IS NULL OR o.BusinessDate >= @fromDate)
                      AND (@toDate IS NULL OR o.BusinessDate <= @toDate)
                    ORDER BY o.OrderDate DESC, o.OrderID DESC
                    LIMIT @limit OFFSET @offset;
                    """;
                var orders = new List<object>();
                await using (var command = new MySqlCommand(ordersSql, connection))
                {
                    command.Parameters.AddWithValue("@workerId", workerId);
                    command.Parameters.AddWithValue("@fromDate", (object?)fromDate ?? DBNull.Value);
                    command.Parameters.AddWithValue("@toDate", (object?)toDate ?? DBNull.Value);
                    command.Parameters.AddWithValue("@limit", pageSize);
                    command.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
                    await using var reader = await command.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        orders.Add(new
                        {
                            orderID = Convert.ToInt32(reader["OrderID"]),
                            customerID = reader["CustomerID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["CustomerID"]),
                            customerName = reader["CustomerName"]?.ToString(),
                            customerPhone = reader["CustomerPhone"]?.ToString(),
                            deliveryWorkerID = Convert.ToInt32(reader["DeliveryWorkerID"]),
                            deliveryZoneID = reader["DeliveryZoneID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DeliveryZoneID"]),
                            zoneName = reader["ZoneName"]?.ToString(),
                            orderDate = Convert.ToDateTime(reader["OrderDate"]),
                            orderType = reader["OrderType"]?.ToString(),
                            status = reader["Status"]?.ToString(),
                            subtotal = Convert.ToDecimal(reader["Subtotal"]),
                            deliveryFee = Convert.ToDecimal(reader["DeliveryFee"]),
                            discountAmount = Convert.ToDecimal(reader["DiscountAmount"]),
                            totalAmount = Convert.ToDecimal(reader["TotalAmount"]),
                            deliveryAddress = reader["DeliveryAddress"] == DBNull.Value ? null : reader["DeliveryAddress"]?.ToString(),
                            notes = reader["Notes"] == DBNull.Value ? null : reader["Notes"]?.ToString()
                        });
                    }
                }

                return Ok(new
                {
                    success = true,
                    workerID = workerId,
                    from = fromDate,
                    to = toDate,
                    page,
                    pageSize,
                    deliveredCount,
                    totalAmount,
                    averageAmount,
                    currency = "IQD",
                    orders
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
        public async Task<IActionResult> GetTodayStats(
            int workerId)
        {
            try
            {
                if (workerId <= 0)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message =
                            "Invalid delivery worker ID."
                    });
                }

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                if (!await IsAuthenticatedWorkerAsync(connection, workerId, Request))
                    return Unauthorized(new { success = false, message = "Worker login is required or expired." });

                string query = """
                    SELECT
                        COUNT(*) AS Deliveredorders,
                        COALESCE(
                            SUM(TotalAmount),
                            0
                        ) AS TotalAmount
                    FROM orders
                    WHERE DeliveryWorkerID = @workerId
                      AND BusinessDate = @businessDate
                      AND Status = 'Delivered';
                    """;

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection
                    );

                command.Parameters.AddWithValue(
                    "@workerId",
                    workerId
                );
                command.Parameters.AddWithValue(
                    "@businessDate",
                    ExpensesController.BusinessDate(ExpensesController.BaghdadNow())
                );

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    int deliveredorders =
                        Convert.ToInt32(
                            reader["Deliveredorders"]
                        );

                    decimal totalAmount =
                        Convert.ToDecimal(
                            reader["TotalAmount"]
                        );

                    return Ok(new
                    {
                        success = true,

                        workerID =
                            workerId,

                        date =
                            ExpensesController.BusinessDate(ExpensesController.BaghdadNow()).ToString("yyyy-MM-dd"),

                        deliveredorders =
                            deliveredorders,

                        totalAmount =
                            totalAmount
                    });
                }

                return Ok(new
                {
                    success = true,
                    workerID = workerId,
                    date =
                        ExpensesController.BusinessDate(ExpensesController.BaghdadNow()).ToString("yyyy-MM-dd"),
                    deliveredorders = 0,
                    totalAmount = 0
                });
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message =
                            "Could not load today's statistics.",
                        error = "تعذر إكمال العملية بسبب خطأ داخلي."
                    }
                );
            }
        }
    }


    // =========================================================
    // LOGIN REQUEST
    // =========================================================

    public class DeliveryLoginRequest
    {
        public string Username { get; set; } = "";

        public string Password { get; set; } = "";
    }


    // =========================================================
    // STATUS REQUEST
    // =========================================================

    public class DeliveryStatusRequest
    {
        public string Status { get; set; } = "";
    }
}