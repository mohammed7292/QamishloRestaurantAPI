using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class deliveryworkersController : ControllerBase
    {
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


                // =================================================
                // CHECK ORDER
                // =================================================

                string checkQuery = """
                    SELECT Status
                    FROM orders
                    WHERE OrderID = @orderID
                      AND DeliveryWorkerID = @workerID
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
        // TODAY'S DELIVERY STATISTICS
        // =====================================================

        [HttpGet("{workerId}/today-stats")]
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

                string query = """
                    SELECT
                        COUNT(*) AS Deliveredorders,
                        COALESCE(
                            SUM(TotalAmount),
                            0
                        ) AS TotalAmount
                    FROM orders
                    WHERE DeliveryWorkerID = @workerId
                      AND DATE(OrderDate) = CURDATE()
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
                            DateTime.Now.ToString(
                                "yyyy-MM-dd"
                            ),

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
                        DateTime.Now.ToString(
                            "yyyy-MM-dd"
                        ),
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