using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/printers")]
public sealed class PrinterSettingsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = """
            SELECT p.PrinterID, p.PrinterName, p.ConnectionType, p.BluetoothAddress,
                   p.Host, p.Port, p.PrintFullReceipt, p.IsEnabled,
                   GROUP_CONCAT(r.CategoryID ORDER BY r.CategoryID) AS CategoryIDs
            FROM restaurantprinters p
            LEFT JOIN printercategoryrouting r ON r.PrinterID=p.PrinterID
            GROUP BY p.PrinterID
            ORDER BY p.PrinterID;
            """;
        await using var command = new MySqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var printers = new List<object>();
        while (await reader.ReadAsync())
        {
            string? categoryIds = reader.IsDBNull(reader.GetOrdinal("CategoryIDs")) ? null : reader.GetString("CategoryIDs");
            printers.Add(new
            {
                printerID = reader.GetInt32("PrinterID"),
                printerName = reader.GetString("PrinterName"),
                connectionType = reader.GetString("ConnectionType"),
                bluetoothAddress = reader.IsDBNull(reader.GetOrdinal("BluetoothAddress")) ? null : reader.GetString("BluetoothAddress"),
                host = reader.IsDBNull(reader.GetOrdinal("Host")) ? null : reader.GetString("Host"),
                port = reader.GetInt32("Port"),
                printFullReceipt = reader.GetBoolean("PrintFullReceipt"),
                isEnabled = reader.GetBoolean("IsEnabled"),
                categoryIDs = string.IsNullOrWhiteSpace(categoryIds) ? Array.Empty<int>() : categoryIds.Split(',').Select(int.Parse).ToArray()
            });
        }
        return Ok(new { success = true, printers });
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] PrinterConfigurationRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (request is null || string.IsNullOrWhiteSpace(request.PrinterName))
            return BadRequest(new { success = false, message = "اسم الطابعة مطلوب." });
        if (request.ConnectionType is not ("Bluetooth" or "Network"))
            return BadRequest(new { success = false, message = "نوع اتصال الطابعة غير مدعوم." });
        if (request.ConnectionType == "Bluetooth" && string.IsNullOrWhiteSpace(request.BluetoothAddress))
            return BadRequest(new { success = false, message = "عنوان Bluetooth MAC مطلوب." });
        if (request.ConnectionType == "Network" && (string.IsNullOrWhiteSpace(request.Host) || request.Port is < 1 or > 65535))
            return BadRequest(new { success = false, message = "عنوان IP والمنفذ غير صالحين." });
        if (request.CategoryIDs is { Length: > 0 } && request.CategoryIDs.Any(x => x <= 0))
            return BadRequest(new { success = false, message = "أحد معرّفات التصنيفات غير صحيح." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            const string insertSql = """
                INSERT INTO restaurantprinters(PrinterName,ConnectionType,BluetoothAddress,Host,Port,PrintFullReceipt,IsEnabled)
                VALUES(@name,@type,@mac,@host,@port,@full,1);
                SELECT LAST_INSERT_ID();
                """;
            int printerId;
            await using (var insert = new MySqlCommand(insertSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("@name", request.PrinterName.Trim());
                insert.Parameters.AddWithValue("@type", request.ConnectionType);
                insert.Parameters.AddWithValue("@mac", (object?)request.BluetoothAddress?.Trim() ?? DBNull.Value);
                insert.Parameters.AddWithValue("@host", (object?)request.Host?.Trim() ?? DBNull.Value);
                insert.Parameters.AddWithValue("@port", request.Port is >= 1 and <= 65535 ? request.Port : 9100);
                insert.Parameters.AddWithValue("@full", request.PrintFullReceipt);
                printerId = Convert.ToInt32(await insert.ExecuteScalarAsync());
            }

            foreach (int categoryId in (request.PrintFullReceipt ? Array.Empty<int>() : request.CategoryIDs ?? Array.Empty<int>()).Distinct())
            {
                await using var exists = new MySqlCommand("SELECT COUNT(*) FROM categories WHERE CategoryID=@id", connection, transaction);
                exists.Parameters.AddWithValue("@id", categoryId);
                if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                    throw new InvalidOperationException($"التصنيف رقم {categoryId} غير موجود.");
                await using var route = new MySqlCommand("INSERT INTO printercategoryrouting(PrinterID,CategoryID) VALUES(@printer,@category)", connection, transaction);
                route.Parameters.AddWithValue("@printer", printerId);
                route.Parameters.AddWithValue("@category", categoryId);
                await route.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return Ok(new { success = true, printerID = printerId, message = "تمت إضافة الطابعة وتوجيهها." });
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
            return Unauthorized(new { success = false });
        if (id <= 0) return BadRequest(new { success = false });
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand("DELETE FROM restaurantprinters WHERE PrinterID=@id", connection);
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteNonQueryAsync() == 0
            ? NotFound(new { success = false, message = "الطابعة غير موجودة." })
            : Ok(new { success = true, deleted = true });
    }
}

public sealed class PrinterConfigurationRequest
{
    public string PrinterName { get; set; } = string.Empty;
    public string ConnectionType { get; set; } = "Bluetooth";
    public string? BluetoothAddress { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 9100;
    public bool PrintFullReceipt { get; set; }
    public int[]? CategoryIDs { get; set; }
}
