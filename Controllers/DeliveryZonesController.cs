using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryzones")]
public sealed class DeliveryZonesController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] bool includeInactive = false)
    {
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        decimal minimum = 3500m;
        await using (var setting = new MySqlCommand("SELECT MinimumOrderAmount FROM restaurantsettings ORDER BY SettingID LIMIT 1", connection))
        {
            var scalar = await setting.ExecuteScalarAsync();
            if (scalar is not null && scalar != DBNull.Value) minimum = Convert.ToDecimal(scalar);
        }
        var sql = "SELECT DeliveryZoneID,ZoneName,DeliveryFee,EstimatedMinutes,Description,IsActive,ZoneCoordinates FROM DeliveryZones " +
                  (includeInactive ? "" : "WHERE IsActive=1 ") + "ORDER BY DeliveryZoneID DESC";
        var zones = new List<object>();
        await using (var cmd = new MySqlCommand(sql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                zones.Add(new {
                    deliveryZoneID = reader.GetInt32("DeliveryZoneID"),
                    zoneName = reader.GetString("ZoneName"),
                    deliveryFee = reader.GetDecimal("DeliveryFee"),
                    estimatedMinutes = reader.IsDBNull(reader.GetOrdinal("EstimatedMinutes")) ? (int?)null : reader.GetInt32("EstimatedMinutes"),
                    description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString("Description"),
                    isActive = reader.GetBoolean("IsActive"),
                    zoneCoordinates = reader.IsDBNull(reader.GetOrdinal("ZoneCoordinates")) ? null : reader.GetString("ZoneCoordinates")
                });
        }
        return Ok(new { success = true, minimumOrderAmount = minimum, zones });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] DeliveryZoneRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        if (string.IsNullOrWhiteSpace(request.ZoneName) || request.DeliveryFee < 0 || string.IsNullOrWhiteSpace(request.ZoneCoordinates))
            return BadRequest(new { success = false, message = "اسم المنطقة والسعر والإحداثيات مطلوبة." });
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = "INSERT INTO DeliveryZones(ZoneName,DeliveryFee,EstimatedMinutes,Description,IsActive,ZoneCoordinates) VALUES(@name,@fee,@minutes,@description,@active,@coordinates); SELECT LAST_INSERT_ID();";
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@name", request.ZoneName.Trim());
        cmd.Parameters.AddWithValue("@fee", request.DeliveryFee);
        cmd.Parameters.AddWithValue("@minutes", request.EstimatedMinutes is null ? DBNull.Value : request.EstimatedMinutes);
        cmd.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@active", request.IsActive);
        cmd.Parameters.AddWithValue("@coordinates", request.ZoneCoordinates);
        var id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return Ok(new { success = true, deliveryZoneID = id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] DeliveryZoneRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        if (id <= 0 || string.IsNullOrWhiteSpace(request.ZoneName) || request.DeliveryFee < 0 || string.IsNullOrWhiteSpace(request.ZoneCoordinates))
            return BadRequest(new { success = false, message = "بيانات المنطقة غير صحيحة." });
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = "UPDATE DeliveryZones SET ZoneName=@name,DeliveryFee=@fee,EstimatedMinutes=@minutes,Description=@description,IsActive=@active,ZoneCoordinates=@coordinates WHERE DeliveryZoneID=@id";
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@name", request.ZoneName.Trim());
        cmd.Parameters.AddWithValue("@fee", request.DeliveryFee); cmd.Parameters.AddWithValue("@minutes", request.EstimatedMinutes is null ? DBNull.Value : request.EstimatedMinutes);
        cmd.Parameters.AddWithValue("@description", (object?)request.Description ?? DBNull.Value); cmd.Parameters.AddWithValue("@active", request.IsActive); cmd.Parameters.AddWithValue("@coordinates", request.ZoneCoordinates);
        return await cmd.ExecuteNonQueryAsync() == 0 ? NotFound(new { success = false }) : Ok(new { success = true });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Deactivate(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        await using var connection = DatabaseManager.GetConnection(); await connection.OpenAsync();
        await using var cmd = new MySqlCommand("UPDATE DeliveryZones SET IsActive=0 WHERE DeliveryZoneID=@id", connection);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteNonQueryAsync() == 0 ? NotFound(new { success = false }) : Ok(new { success = true });
    }

    [HttpPut("minimum-order")]
    public async Task<IActionResult> UpdateMinimum([FromBody] MinimumOrderRequest request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request)) return Unauthorized(new { success = false, message = "Manager login required." });
        if (request.MinimumOrderAmount < 0) return BadRequest(new { success = false, message = "القيمة غير صحيحة." });
        await using var connection = DatabaseManager.GetConnection(); await connection.OpenAsync();
        const string sql = "UPDATE restaurantsettings SET MinimumOrderAmount=@amount ORDER BY SettingID LIMIT 1";
        await using var cmd = new MySqlCommand(sql, connection); cmd.Parameters.AddWithValue("@amount", request.MinimumOrderAmount);
        if (await cmd.ExecuteNonQueryAsync() == 0)
            return NotFound(new { success = false, message = "لم يتم العثور على إعدادات المطعم." });
        return Ok(new { success = true, minimumOrderAmount = request.MinimumOrderAmount });
    }
}

public sealed class DeliveryZoneRequest
{
    public string ZoneName { get; set; } = "";
    public decimal DeliveryFee { get; set; }
    public int? EstimatedMinutes { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string ZoneCoordinates { get; set; } = "";
}
public sealed class MinimumOrderRequest { public decimal MinimumOrderAmount { get; set; } }
