using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryworkers/admin")]
public sealed class deliveryworkersAdminController : ControllerBase
{
    private readonly ILogger<deliveryworkersAdminController> _logger;

    public deliveryworkersAdminController(ILogger<deliveryworkersAdminController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = """
            SELECT DeliveryWorkerID, FullName, Username, Phone, IsActive, IsOnShift,
                   ShiftStartedAt, ShiftEndedAt, CreatedAt
            FROM deliveryworkers
            ORDER BY FullName;
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
                username = reader.GetString("Username"),
                phone = reader.IsDBNull(reader.GetOrdinal("Phone")) ? null : reader.GetString("Phone"),
                isActive = reader.GetBoolean("IsActive"),
                isOnShift = reader.GetBoolean("IsOnShift"),
                shiftStartedAt = reader.IsDBNull(reader.GetOrdinal("ShiftStartedAt")) ? (DateTime?)null : reader.GetDateTime("ShiftStartedAt"),
                shiftEndedAt = reader.IsDBNull(reader.GetOrdinal("ShiftEndedAt")) ? (DateTime?)null : reader.GetDateTime("ShiftEndedAt"),
                createdAt = reader.GetDateTime("CreatedAt")
            });
        }

        return Ok(new { success = true, workers });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeliveryWorkerAdminRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (request is null || string.IsNullOrWhiteSpace(request.FullName) ||
            string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new { success = false, message = "الاسم واسم المستخدم وكلمة المرور مطلوبة." });
        if (request.FullName.Trim().Length > 100 || request.Username.Trim().Length > 50 ||
            request.Password.Length > 255 || (request.Phone?.Length ?? 0) > 20)
            return BadRequest(new { success = false, message = "تجاوز أحد الحقول الحد الأقصى المسموح به." });

        try
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            const string insertSql = """
                INSERT INTO deliveryworkers(FullName, Username, Password, Phone, IsActive, IsOnShift)
                VALUES(@name, @username, @password, @phone, 1, 0);
                """;
            await using (var insert = new MySqlCommand(insertSql, connection))
            {
                insert.Parameters.AddWithValue("@name", request.FullName.Trim());
                insert.Parameters.AddWithValue("@username", request.Username.Trim());
                insert.Parameters.AddWithValue("@password", request.Password);
                insert.Parameters.AddWithValue("@phone", (object?)request.Phone?.Trim() ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync();
            }

            await using var idCommand = new MySqlCommand("SELECT LAST_INSERT_ID();", connection);
            int id = Convert.ToInt32(await idCommand.ExecuteScalarAsync());
            return Ok(new { success = true, deliveryWorkerID = id, message = "تمت إضافة عامل التوصيل. يبدأ دوامه عند تسجيل الدخول." });
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            return Conflict(new { success = false, message = "اسم المستخدم موجود مسبقاً. اختر اسماً آخر." });
        }
        catch (MySqlException exception) when (exception.Number is 1205 or 1213 or 1040 or 2002 or 2003 or 2013)
        {
            _logger.LogError(exception, "Database connection/timeout while adding a delivery worker.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { success = false, message = "قاعدة البيانات لم تستجب في الوقت المحدد. تحقق من اتصال API بقاعدة البيانات ثم أعد المحاولة." });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to create delivery worker.");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { success = false, message = "تعذرت إضافة عامل التوصيل بسبب خطأ في الخادم." });
        }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DeliveryWorkerAdminRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (id <= 0 || request is null || string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 100)
            return BadRequest(new { success = false, message = "بيانات العامل غير صحيحة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            string sql = string.IsNullOrWhiteSpace(request.Password)
                ? "UPDATE deliveryworkers SET FullName=@name, Phone=@phone, IsActive=@active, IsOnShift=CASE WHEN @active=0 THEN 0 ELSE IsOnShift END, UpdatedAt=UTC_TIMESTAMP() WHERE DeliveryWorkerID=@id"
                : "UPDATE deliveryworkers SET FullName=@name, Phone=@phone, IsActive=@active, IsOnShift=CASE WHEN @active=0 THEN 0 ELSE IsOnShift END, Password=@password, UpdatedAt=UTC_TIMESTAMP() WHERE DeliveryWorkerID=@id";
            await using (var update = new MySqlCommand(sql, connection, transaction))
            {
                update.Parameters.AddWithValue("@id", id);
                update.Parameters.AddWithValue("@name", request.FullName.Trim());
                update.Parameters.AddWithValue("@phone", (object?)request.Phone?.Trim() ?? DBNull.Value);
                update.Parameters.AddWithValue("@active", request.IsActive);
                if (!string.IsNullOrWhiteSpace(request.Password))
                    update.Parameters.AddWithValue("@password", request.Password);
                if (await update.ExecuteNonQueryAsync() == 0)
                {
                    await transaction.RollbackAsync();
                    return NotFound(new { success = false, message = "عامل التوصيل غير موجود." });
                }
            }

            if (!request.IsActive)
            {
                await using var closeShift = new MySqlCommand(
                    "UPDATE deliveryworkershifts SET EndedAt=UTC_TIMESTAMP() WHERE DeliveryWorkerID=@id AND EndedAt IS NULL",
                    connection, transaction);
                closeShift.Parameters.AddWithValue("@id", id);
                await closeShift.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Ok(new { success = true, message = "تم تحديث حساب عامل التوصيل." });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }


    [HttpPatch("{id:int}/duty-status")]
    public async Task<IActionResult> SetDutyStatus(int id, [FromBody] DeliveryWorkerDutyStatusRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (id <= 0 || request is null)
            return BadRequest(new { success = false, message = "بيانات الدوام غير صحيحة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            bool isActive;
            bool currentDuty;
            const string readSql = "SELECT IsActive, IsOnShift FROM deliveryworkers WHERE DeliveryWorkerID=@id FOR UPDATE;";
            await using (var read = new MySqlCommand(readSql, connection, transaction))
            {
                read.Parameters.AddWithValue("@id", id);
                await using var reader = await read.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    await transaction.RollbackAsync();
                    return NotFound(new { success = false, message = "عامل التوصيل غير موجود." });
                }
                isActive = reader.GetBoolean("IsActive");
                currentDuty = reader.GetBoolean("IsOnShift");
            }

            if (!isActive)
            {
                await transaction.RollbackAsync();
                return Conflict(new { success = false, message = "الحساب معطّل. فعّل الحساب أولاً قبل بدء الدوام." });
            }

            if (currentDuty == request.IsOnShift)
            {
                await transaction.CommitAsync();
                return Ok(new { success = true, deliveryWorkerID = id, isOnShift = currentDuty, message = "حالة الدوام محدثة بالفعل." });
            }

            if (!request.IsOnShift)
            {
                const string activeOrdersSql = """
                    SELECT COUNT(*) FROM orders
                    WHERE DeliveryWorkerID=@id AND IsArchived=0
                      AND Status IN ('Preparing','Ready','OutForDelivery');
                    """;
                await using (var activeOrders = new MySqlCommand(activeOrdersSql, connection, transaction))
                {
                    activeOrders.Parameters.AddWithValue("@id", id);
                    int activeCount = Convert.ToInt32(await activeOrders.ExecuteScalarAsync());
                    if (activeCount > 0)
                    {
                        await transaction.RollbackAsync();
                        return Conflict(new
                        {
                            success = false,
                            message = "لا يمكن إنهاء الدوام قبل إتمام الطلبات النشطة أو تحويلها إلى عامل آخر.",
                            activeOrders = activeCount
                        });
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
                    close.Parameters.AddWithValue("@id", id);
                    await close.ExecuteNonQueryAsync();
                }

                const string turnOffSql = """
                    UPDATE deliveryworkers
                    SET IsOnShift=0, ShiftEndedAt=UTC_TIMESTAMP(),
                        CurrentLatitude=NULL, CurrentLongitude=NULL, LastLocationAt=NULL, UpdatedAt=UTC_TIMESTAMP()
                    WHERE DeliveryWorkerID=@id AND IsActive=1;
                    """;
                await using var turnOff = new MySqlCommand(turnOffSql, connection, transaction);
                turnOff.Parameters.AddWithValue("@id", id);
                await turnOff.ExecuteNonQueryAsync();
            }
            else
            {
                const string turnOnSql = """
                    UPDATE deliveryworkers
                    SET IsOnShift=1, ShiftStartedAt=UTC_TIMESTAMP(), ShiftEndedAt=NULL, UpdatedAt=UTC_TIMESTAMP()
                    WHERE DeliveryWorkerID=@id AND IsActive=1;
                    """;
                await using (var turnOn = new MySqlCommand(turnOnSql, connection, transaction))
                {
                    turnOn.Parameters.AddWithValue("@id", id);
                    await turnOn.ExecuteNonQueryAsync();
                }

                const string openShiftSql = """
                    INSERT INTO deliveryworkershifts(DeliveryWorkerID, StartedAt)
                    SELECT @id, UTC_TIMESTAMP()
                    WHERE NOT EXISTS (
                        SELECT 1 FROM deliveryworkershifts
                        WHERE DeliveryWorkerID=@id AND EndedAt IS NULL
                    );
                    """;
                await using var openShift = new MySqlCommand(openShiftSql, connection, transaction);
                openShift.Parameters.AddWithValue("@id", id);
                await openShift.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Ok(new
            {
                success = true,
                deliveryWorkerID = id,
                isOnShift = request.IsOnShift,
                message = request.IsOnShift ? "تم بدء دوام عامل التوصيل." : "تم إنهاء دوام عامل التوصيل."
            });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (id <= 0)
            return BadRequest(new { success = false, message = "معرّف العامل غير صحيح." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand("DELETE FROM deliveryworkers WHERE DeliveryWorkerID=@id", connection);
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteNonQueryAsync() == 0
            ? NotFound(new { success = false, message = "حساب عامل التوصيل غير موجود." })
            : Ok(new { success = true, deleted = true });
    }
}

public sealed class DeliveryWorkerAdminRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class DeliveryWorkerDutyStatusRequest
{
    public bool IsOnShift { get; set; }
}
