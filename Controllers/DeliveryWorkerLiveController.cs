using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryworkers")]
public sealed class DeliveryWorkerLiveController : ControllerBase
{
    private static async Task<bool> IsWorkerAuthenticatedAsync(
        MySqlConnection connection,
        int workerId,
        HttpRequest request,
        MySqlTransaction? transaction = null)
    {
        string username = request.Headers["X-Delivery-Worker-Username"].ToString();
        string password = request.Headers["X-Delivery-Worker-Password"].ToString();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return false;

        const string sql = """
            SELECT COUNT(*) FROM deliveryworkers
            WHERE DeliveryWorkerID=@id AND Username=@username
              AND Password=@password AND IsActive=1;
            """;
        await using var command = transaction is null
            ? new MySqlCommand(sql, connection)
            : new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@id", workerId);
        command.Parameters.AddWithValue("@username", username.Trim());
        command.Parameters.AddWithValue("@password", password);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    [HttpPost("{workerId:int}/location")]
    public async Task<IActionResult> UpdateLocation(int workerId, [FromBody] WorkerLocationRequest request)
    {
        if (workerId <= 0 || request is null ||
            request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return BadRequest(new { success = false, message = "إحداثيات الموقع غير صحيحة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        if (!await IsWorkerAuthenticatedAsync(connection, workerId, Request))
            return Unauthorized(new { success = false, message = "يجب تسجيل الدخول بحساب عامل التوصيل." });

        const string sql = """
            UPDATE deliveryworkers
            SET IsOnShift=1, CurrentLatitude=@latitude,
                CurrentLongitude=@longitude,
                LastLocationAt=UTC_TIMESTAMP(),
                UpdatedAt=UTC_TIMESTAMP()
            WHERE DeliveryWorkerID=@id AND IsActive=1;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@latitude", request.Latitude);
        command.Parameters.AddWithValue("@longitude", request.Longitude);
        command.Parameters.AddWithValue("@id", workerId);
        int affected = await command.ExecuteNonQueryAsync();
        return affected == 0
            ? Conflict(new { success = false, message = "انتهى دوام العامل أو الحساب غير نشط." })
            : Ok(new { success = true, workerID = workerId, receivedAt = DateTime.UtcNow });
    }

    [HttpGet("active-locations")]
    public async Task<IActionResult> GetActiveLocations()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();

        // Fetch restaurant coordinates (defaulting to Qamishlo Restaurant Daratoo, Erbil: 36.120858, 44.061717)
        double restaurantLat = 36.120858;
        double restaurantLng = 44.061717;
        string restaurantName = "مطعم قامشلو";
        const string settingsSql = """
            SELECT RestaurantName, RestaurantLatitude, RestaurantLongitude
            FROM restaurantsettings
            ORDER BY SettingID
            LIMIT 1;
            """;
        await using (var settingsCmd = new MySqlCommand(settingsSql, connection))
        await using (var settingsReader = await settingsCmd.ExecuteReaderAsync())
        {
            if (await settingsReader.ReadAsync())
            {
                if (!settingsReader.IsDBNull(0) && !string.IsNullOrWhiteSpace(settingsReader.GetString(0)))
                    restaurantName = settingsReader.GetString(0);
                if (!settingsReader.IsDBNull(1))
                    restaurantLat = Convert.ToDouble(settingsReader.GetValue(1));
                if (!settingsReader.IsDBNull(2))
                    restaurantLng = Convert.ToDouble(settingsReader.GetValue(2));
            }
        }

        const string sql = """
            SELECT
                dw.DeliveryWorkerID,
                dw.FullName,
                dw.Phone,
                dw.IsOnShift,
                COALESCE(dw.CurrentLatitude, 36.120858) AS CurrentLatitude,
                COALESCE(dw.CurrentLongitude, 44.061717) AS CurrentLongitude,
                dw.LastLocationAt,
                ao.OrderID AS CurrentOrderID,
                COALESCE(
                    (SELECT COUNT(*) FROM orders o2 WHERE COALESCE(o2.BusinessDate, DATE(o2.OrderDate)) = COALESCE(ao.BusinessDate, DATE(ao.OrderDate)) AND o2.OrderID <= ao.OrderID),
                    ao.OrderID
                ) AS CurrentDailyOrderNumber,
                ao.Status AS CurrentOrderStatus,
                ao.CustomerLatitude AS CustomerLatitude,
                ao.CustomerLongitude AS CustomerLongitude,
                ao.DeliveryAddress AS CurrentDeliveryAddress,
                COALESCE(c.FullName, '') AS CurrentCustomerName
            FROM deliveryworkers dw
            LEFT JOIN orders ao ON ao.OrderID = (
                SELECT o.OrderID
                FROM orders o
                WHERE o.DeliveryWorkerID = dw.DeliveryWorkerID
                  AND o.IsArchived = 0
                  AND o.Status IN ('Preparing', 'Ready', 'OutForDelivery')
                ORDER BY FIELD(o.Status, 'OutForDelivery', 'Ready', 'Preparing'), o.OrderID DESC
                LIMIT 1
            )
            LEFT JOIN customers c ON c.CustomerID = ao.CustomerID
            WHERE dw.IsActive = 1
              AND (dw.IsOnShift = 1 OR (dw.CurrentLatitude IS NOT NULL AND dw.CurrentLongitude IS NOT NULL AND dw.LastLocationAt >= UTC_TIMESTAMP() - INTERVAL 120 MINUTE))
            ORDER BY dw.FullName;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var workers = new List<object>();
        while (await reader.ReadAsync())
        {
            workers.Add(new
            {
                deliveryWorkerID = reader.GetInt32("DeliveryWorkerID"),
                fullName = reader.GetString("FullName"),
                phone = reader.IsDBNull(reader.GetOrdinal("Phone")) ? "" : reader.GetString("Phone"),
                isOnShift = reader.GetBoolean("IsOnShift"),
                latitude = reader.GetDouble("CurrentLatitude"),
                longitude = reader.GetDouble("CurrentLongitude"),
                lastLocationAt = reader.IsDBNull(reader.GetOrdinal("LastLocationAt")) ? DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") : reader.GetDateTime("LastLocationAt").ToString("yyyy-MM-dd HH:mm:ss"),
                currentOrderID = reader.IsDBNull(reader.GetOrdinal("CurrentOrderID")) ? 0 : reader.GetInt32("CurrentOrderID"),
                currentDailyOrderNumber = reader.IsDBNull(reader.GetOrdinal("CurrentDailyOrderNumber")) ? 0 : reader.GetInt32("CurrentDailyOrderNumber"),
                currentOrderStatus = reader.IsDBNull(reader.GetOrdinal("CurrentOrderStatus")) ? "" : reader.GetString("CurrentOrderStatus"),
                customerLatitude = reader.IsDBNull(reader.GetOrdinal("CustomerLatitude")) ? (double?)null : Convert.ToDouble(reader["CustomerLatitude"]),
                customerLongitude = reader.IsDBNull(reader.GetOrdinal("CustomerLongitude")) ? (double?)null : Convert.ToDouble(reader["CustomerLongitude"]),
                currentDeliveryAddress = reader.IsDBNull(reader.GetOrdinal("CurrentDeliveryAddress")) ? "" : reader.GetString("CurrentDeliveryAddress"),
                currentCustomerName = reader.IsDBNull(reader.GetOrdinal("CurrentCustomerName")) ? "" : reader.GetString("CurrentCustomerName"),
                restaurantLatitude = restaurantLat,
                restaurantLongitude = restaurantLng,
                restaurantName
            });
        }
        return Ok(new
        {
            success = true,
            restaurantLatitude = restaurantLat,
            restaurantLongitude = restaurantLng,
            restaurantName,
            workers,
            serverTimeUtc = DateTime.UtcNow
        });
    }

    [HttpPost("{workerId:int}/end-shift")]
    public async Task<IActionResult> EndShift(int workerId)
    {
        if (workerId <= 0)
            return BadRequest(new { success = false, message = "معرّف العامل غير صحيح." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            if (!await IsWorkerAuthenticatedAsync(connection, workerId, Request, transaction))
            {
                await transaction.RollbackAsync();
                return Unauthorized(new { success = false, message = "جلسة العامل غير صالحة." });
            }

            const string activeOrdersSql = """
                SELECT COUNT(*) FROM orders
                WHERE DeliveryWorkerID=@id AND IsArchived=0
                  AND Status IN ('Preparing','Ready','OutForDelivery');
                """;
            await using (var activeOrders = new MySqlCommand(activeOrdersSql, connection, transaction))
            {
                activeOrders.Parameters.AddWithValue("@id", workerId);
                int activeCount = Convert.ToInt32(await activeOrders.ExecuteScalarAsync());
                if (activeCount > 0)
                {
                    await transaction.RollbackAsync();
                    return Conflict(new { success = false, message = "لا يمكن إنهاء الدوام قبل تسليم الطلبات النشطة أو تحويلها إلى عامل آخر.", activeOrders = activeCount });
                }
            }

            const string closeShiftSql = """
                UPDATE deliveryworkershifts
                SET EndedAt=UTC_TIMESTAMP(),
                    EndLatitude=(SELECT CurrentLatitude FROM deliveryworkers WHERE DeliveryWorkerID=@id),
                    EndLongitude=(SELECT CurrentLongitude FROM deliveryworkers WHERE DeliveryWorkerID=@id)
                WHERE DeliveryWorkerID=@id AND EndedAt IS NULL;
                """;
            await using (var close = new MySqlCommand(closeShiftSql, connection, transaction))
            {
                close.Parameters.AddWithValue("@id", workerId);
                await close.ExecuteNonQueryAsync();
            }

            const string workerSql = """
                UPDATE deliveryworkers
                SET IsOnShift=0, ShiftEndedAt=UTC_TIMESTAMP(), UpdatedAt=UTC_TIMESTAMP()
                WHERE DeliveryWorkerID=@id AND IsActive=1;
                """;
            await using (var update = new MySqlCommand(workerSql, connection, transaction))
            {
                update.Parameters.AddWithValue("@id", workerId);
                if (await update.ExecuteNonQueryAsync() == 0)
                {
                    await transaction.RollbackAsync();
                    return NotFound(new { success = false, message = "حساب العامل غير موجود أو غير نشط." });
                }
            }

            await transaction.CommitAsync();
            return Ok(new { success = true, workerID = workerId, isOnShift = false, endedAtUtc = DateTime.UtcNow });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    [HttpPost("{workerId:int}/start-shift")]
    public async Task<IActionResult> StartShift(int workerId)
    {
        if (workerId <= 0)
            return BadRequest(new { success = false });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (!await IsWorkerAuthenticatedAsync(connection, workerId, Request, transaction))
        {
            await transaction.RollbackAsync();
            return Unauthorized(new { success = false });
        }

        const string updateSql = """
            UPDATE deliveryworkers
            SET IsOnShift=1, ShiftStartedAt=UTC_TIMESTAMP(), ShiftEndedAt=NULL, UpdatedAt=UTC_TIMESTAMP()
            WHERE DeliveryWorkerID=@id AND IsActive=1;
            """;
        await using (var update = new MySqlCommand(updateSql, connection, transaction))
        {
            update.Parameters.AddWithValue("@id", workerId);
            await update.ExecuteNonQueryAsync();
        }

        const string insertSql = """
            INSERT INTO deliveryworkershifts(DeliveryWorkerID, StartedAt)
            SELECT @id, UTC_TIMESTAMP()
            WHERE NOT EXISTS (
                SELECT 1 FROM deliveryworkershifts
                WHERE DeliveryWorkerID=@id AND EndedAt IS NULL
            );
            """;
        await using (var insert = new MySqlCommand(insertSql, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", workerId);
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return Ok(new { success = true, workerID = workerId, isOnShift = true });
    }

    [HttpGet("{workerId:int}/today-orders")]
    public async Task<IActionResult> GetTodayOrders(int workerId)
    {
        if (workerId <= 0)
            return BadRequest(new { success = false, message = "Invalid worker ID." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        if (!await IsWorkerAuthenticatedAsync(connection, workerId, Request))
            return Unauthorized(new { success = false, message = "Worker login is required." });

        DateTime businessDate = ExpensesController.BusinessDate(ExpensesController.BaghdadNow());
        const string sql = """
            SELECT o.OrderID, COALESCE((SELECT COUNT(*) FROM orders o2 WHERE COALESCE(o2.BusinessDate, DATE(o2.OrderDate)) = COALESCE(o.BusinessDate, DATE(o.OrderDate)) AND o2.OrderID <= o.OrderID) AS DailyOrderNumber, o.OrderID) AS DailyOrderNumber,
                   o.OrderDate, o.OrderType, o.Status,
                   o.TotalAmount, o.DeliveryAddress,
                   COALESCE(c.FullName,'') AS CustomerName,
                   COALESCE(c.Phone,'') AS CustomerPhone,
                   COALESCE(dz.ZoneName,'') AS ZoneName
            FROM orders o
            LEFT JOIN customers c ON c.CustomerID=o.CustomerID
            LEFT JOIN deliveryzones dz ON dz.DeliveryZoneID=o.DeliveryZoneID
            WHERE o.DeliveryWorkerID=@workerId
              AND (
                    COALESCE(o.BusinessDate, DATE(DATE_ADD(o.OrderDate, INTERVAL 3 HOUR)))=@businessDate
                    OR DATE(DATE_ADD(o.DeliveryAssignedAt, INTERVAL 3 HOUR))=@businessDate
                    OR DATE(DATE_ADD(o.PickedUpAt, INTERVAL 3 HOUR))=@businessDate
                    OR (o.Status='Delivered' AND DATE(DATE_ADD(o.UpdatedAt, INTERVAL 3 HOUR))=@businessDate)
                  )
            ORDER BY o.OrderDate DESC, o.OrderID DESC;
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@workerId", workerId);
        command.Parameters.AddWithValue("@businessDate", businessDate);
        await using var reader = await command.ExecuteReaderAsync();
        var orders = new List<object>();
        while (await reader.ReadAsync())
        {
            orders.Add(new
            {
                orderID = reader.GetInt32("OrderID"),
                dailyOrderNumber = reader.GetInt32("DailyOrderNumber"),
                orderDate = reader.GetDateTime("OrderDate"),
                orderType = reader.GetString("OrderType"),
                status = reader.GetString("Status"),
                totalAmount = reader.GetDecimal("TotalAmount"),
                customerName = reader.GetString("CustomerName"),
                customerPhone = reader.GetString("CustomerPhone"),
                zoneName = reader.GetString("ZoneName"),
                deliveryAddress = reader.IsDBNull(reader.GetOrdinal("DeliveryAddress")) ? null : reader.GetString("DeliveryAddress")
            });
        }
        return Ok(new { success = true, workerID = workerId, businessDate = businessDate.ToString("yyyy-MM-dd"), orders });
    }
}

public sealed class WorkerLocationRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}
