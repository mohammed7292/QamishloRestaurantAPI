using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryworkers/{workerId:int}/orders/{orderId:int}/transfer")]
public sealed class DeliveryWorkerTransferController : ControllerBase
{
    // Returns active transfer targets to an authenticated delivery worker.
    // The endpoint deliberately exposes only ID, name and phone.
    [HttpGet("~/api/deliveryworkers/{workerId:int}/transfer-targets")]
    public async Task<IActionResult> GetTransferTargets(int workerId)
    {
        if (workerId <= 0)
            return BadRequest(new { success = false, message = "Invalid worker ID." });

        string username = Request.Headers["X-Delivery-Worker-Username"].ToString();
        string password = Request.Headers["X-Delivery-Worker-Password"].ToString();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return Unauthorized(new { success = false, message = "Worker login is required." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();

        const string authSql = """
            SELECT COUNT(*)
            FROM deliveryworkers
            WHERE DeliveryWorkerID = @id
              AND Username = @username
              AND Password = @password
              AND IsActive = 1;
            """;
        await using (var auth = new MySqlCommand(authSql, connection))
        {
            auth.Parameters.AddWithValue("@id", workerId);
            auth.Parameters.AddWithValue("@username", username.Trim());
            auth.Parameters.AddWithValue("@password", password);
            if (Convert.ToInt32(await auth.ExecuteScalarAsync()) == 0)
                return Unauthorized(new { success = false, message = "Worker session is no longer valid. Please sign in again." });
        }

        const string workersSql = """
            SELECT DeliveryWorkerID, FullName, Phone
            FROM deliveryworkers
            WHERE IsActive = 1 AND DeliveryWorkerID <> @currentWorker
            ORDER BY FullName;
            """;
        await using var command = new MySqlCommand(workersSql, connection);
        command.Parameters.AddWithValue("@currentWorker", workerId);
        await using var reader = await command.ExecuteReaderAsync();

        var workers = new List<object>();
        while (await reader.ReadAsync())
        {
            workers.Add(new
            {
                deliveryWorkerID = reader.GetInt32("DeliveryWorkerID"),
                fullName = reader.GetString("FullName"),
                phone = reader.IsDBNull(reader.GetOrdinal("Phone")) ? null : reader.GetString("Phone")
            });
        }

        return Ok(new { success = true, workers });
    }

    [HttpPost]
    public async Task<IActionResult> Transfer(
        int workerId,
        int orderId,
        [FromBody] TransferDeliveryRequest request)
    {
        if (workerId <= 0 || orderId <= 0 || request.NewWorkerID <= 0 ||
            request.NewWorkerID == workerId ||
            string.IsNullOrWhiteSpace(request.Username) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new
            {
                success = false,
                message = "Valid order and receiving worker IDs plus source worker credentials are required."
            });
        }

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            const string sourceAuthSql = """
                SELECT COUNT(*)
                FROM deliveryworkers
                WHERE DeliveryWorkerID = @id
                  AND Username = @username
                  AND Password = @password
                  AND IsActive = 1;
                """;
            await using (var sourceAuth = new MySqlCommand(sourceAuthSql, connection, transaction))
            {
                sourceAuth.Parameters.AddWithValue("@id", workerId);
                sourceAuth.Parameters.AddWithValue("@username", request.Username.Trim());
                sourceAuth.Parameters.AddWithValue("@password", request.Password);
                if (Convert.ToInt32(await sourceAuth.ExecuteScalarAsync()) == 0)
                {
                    await transaction.RollbackAsync();
                    return Unauthorized(new { success = false, message = "Invalid source worker credentials." });
                }
            }

            const string targetSql = """
                SELECT COUNT(*)
                FROM deliveryworkers
                WHERE DeliveryWorkerID = @id AND IsActive = 1;
                """;
            await using (var target = new MySqlCommand(targetSql, connection, transaction))
            {
                target.Parameters.AddWithValue("@id", request.NewWorkerID);
                if (Convert.ToInt32(await target.ExecuteScalarAsync()) == 0)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { success = false, message = "The receiving delivery worker is not active." });
                }
            }

            const string orderSql = """
                SELECT OrderID
                FROM orders
                WHERE OrderID = @order
                  AND DeliveryWorkerID = @from
                  AND OrderType = 'Delivery'
                  AND IsArchived = 0
                  AND Status NOT IN ('Delivered', 'Cancelled')
                FOR UPDATE;
                """;
            await using (var check = new MySqlCommand(orderSql, connection, transaction))
            {
                check.Parameters.AddWithValue("@order", orderId);
                check.Parameters.AddWithValue("@from", workerId);
                object? found = await check.ExecuteScalarAsync();
                if (found is null || found == DBNull.Value)
                {
                    await transaction.RollbackAsync();
                    return NotFound(new { success = false, message = "Order is not currently assigned to this worker or is already closed." });
                }
            }

            const string updateSql = """
                UPDATE orders
                SET DeliveryWorkerID = @to, UpdatedAt = NOW()
                WHERE OrderID = @order AND DeliveryWorkerID = @from;
                """;
            await using (var update = new MySqlCommand(updateSql, connection, transaction))
            {
                update.Parameters.AddWithValue("@to", request.NewWorkerID);
                update.Parameters.AddWithValue("@order", orderId);
                update.Parameters.AddWithValue("@from", workerId);
                if (await update.ExecuteNonQueryAsync() != 1)
                {
                    await transaction.RollbackAsync();
                    return Conflict(new { success = false, message = "The order assignment changed. Refresh and try again." });
                }
            }

            const string historySql = """
                INSERT INTO deliveryworkertransfers
                    (OrderID, FromWorkerID, ToWorkerID, Notes)
                VALUES (@order, @from, @to, @notes);
                """;
            await using (var history = new MySqlCommand(historySql, connection, transaction))
            {
                history.Parameters.AddWithValue("@order", orderId);
                history.Parameters.AddWithValue("@from", workerId);
                history.Parameters.AddWithValue("@to", request.NewWorkerID);
                history.Parameters.AddWithValue("@notes", (object?)request.Notes ?? DBNull.Value);
                await history.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Ok(new { success = true, orderID = orderId, fromWorkerID = workerId, toWorkerID = request.NewWorkerID });
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}

public sealed class TransferDeliveryRequest
{
    public int NewWorkerID { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string? Notes { get; set; }
}
