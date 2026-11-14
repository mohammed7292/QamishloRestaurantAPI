using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryworkers")]
public sealed class DeliveryWorkerLiveController : ControllerBase
{
    private readonly ILogger<DeliveryWorkerLiveController> _logger;

    public DeliveryWorkerLiveController(ILogger<DeliveryWorkerLiveController> logger)
    {
        _logger = logger;
    }
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
            !double.IsFinite(request.Latitude) || !double.IsFinite(request.Longitude) ||
            request.Latitude is < -90 or > 90 || request.Longitude is < -180 or > 180)
            return BadRequest(new { success = false, message = "إحداثيات الموقع غير صحيحة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        if (!await IsWorkerAuthenticatedAsync(connection, workerId, Request))
            return Unauthorized(new { success = false, message = "يجب تسجيل الدخول بحساب عامل التوصيل." });

        const string sql = """
            UPDATE deliveryworkers
            SET CurrentLatitude=@latitude,
                CurrentLongitude=@longitude,
                LastLocationAt=UTC_TIMESTAMP(),
                UpdatedAt=UTC_TIMESTAMP()
            WHERE DeliveryWorkerID=@id AND IsActive=1 AND IsOnShift=1;
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
        const string sql = """
            SELECT dw.DeliveryWorkerID, dw.FullName, dw.Phone,
                   dw.CurrentLatitude, dw.CurrentLongitude, dw.LastLocationAt,
                   activeOrder.ActiveOrderID,
                   activeOrderRow.Status AS ActiveOrderStatus
            FROM deliveryworkers dw
            LEFT JOIN (
                SELECT DeliveryWorkerID, MAX(OrderID) AS ActiveOrderID
                FROM orders
                WHERE IsArchived=0 AND Status IN ('Preparing','Ready','OutForDelivery')
                GROUP BY DeliveryWorkerID
            ) activeOrder ON activeOrder.DeliveryWorkerID=dw.DeliveryWorkerID
            LEFT JOIN orders activeOrderRow ON activeOrderRow.OrderID=activeOrder.ActiveOrderID
            WHERE dw.IsActive=1 AND dw.IsOnShift=1
              AND dw.CurrentLatitude IS NOT NULL AND dw.CurrentLongitude IS NOT NULL
              AND dw.LastLocationAt >= UTC_TIMESTAMP() - INTERVAL 3 MINUTE
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
                phone = reader.IsDBNull(reader.GetOrdinal("Phone")) ? null : reader.GetString("Phone"),
                latitude = reader.GetDouble("CurrentLatitude"),
                longitude = reader.GetDouble("CurrentLongitude"),
                lastLocationAt = reader.GetDateTime("LastLocationAt"),
                isOnShift = true,
                currentOrderID = reader.IsDBNull(reader.GetOrdinal("ActiveOrderID")) ? 0 : reader.GetInt32("ActiveOrderID"),
                currentOrderStatus = reader.IsDBNull(reader.GetOrdinal("ActiveOrderStatus")) ? string.Empty : reader.GetString("ActiveOrderStatus")
            });
        }
        return Ok(new { success = true, workers, serverTimeUtc = DateTime.UtcNow });
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

            // Lock the worker row while checking active work and closing the shift.
            const string lockWorkerSql = "SELECT IsActive, IsOnShift FROM deliveryworkers WHERE DeliveryWorkerID=@id FOR UPDATE;";
            await using (var lockWorker = new MySqlCommand(lockWorkerSql, connection, transaction))
            {
                lockWorker.Parameters.AddWithValue("@id", workerId);
                await using var workerReader = await lockWorker.ExecuteReaderAsync();
                if (!await workerReader.ReadAsync() || !workerReader.GetBoolean("IsActive"))
                {
                    await workerReader.CloseAsync();
                    await transaction.RollbackAsync();
                    return NotFound(new { success = false, message = "حساب العامل غير موجود أو غير نشط." });
                }
                if (!workerReader.GetBoolean("IsOnShift"))
                {
                    await workerReader.CloseAsync();
                    await transaction.RollbackAsync();
                    return Conflict(new { success = false, message = "الدوام منتهٍ مسبقاً." });
                }
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
                SET IsOnShift=0, ShiftEndedAt=UTC_TIMESTAMP(),
                    CurrentLatitude=NULL, CurrentLongitude=NULL, LastLocationAt=NULL,
                    UpdatedAt=UTC_TIMESTAMP()
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
        catch (Exception ex)
        {
            try { await transaction.RollbackAsync(); } catch { }
            _logger.LogError(ex, "Failed to end delivery shift for worker {WorkerID}.", workerId);
            return StatusCode(500, new
            {
                success = false,
                message = "تعذر إنهاء الدوام بسبب خطأ في الخادم أو قاعدة البيانات.",
                traceId = HttpContext.TraceIdentifier
            });
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
            SELECT o.OrderID, o.OrderDate, o.OrderType, o.Status,
                   o.TotalAmount, o.DeliveryAddress,
                   COALESCE(c.FullName,'') AS CustomerName,
                   COALESCE(c.Phone,'') AS CustomerPhone,
                   COALESCE(dz.ZoneName,'') AS ZoneName
            FROM orders o
            LEFT JOIN customers c ON c.CustomerID=o.CustomerID
            LEFT JOIN deliveryzones dz ON dz.DeliveryZoneID=o.DeliveryZoneID
            WHERE o.DeliveryWorkerID=@workerId
              AND o.IsArchived=0
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
