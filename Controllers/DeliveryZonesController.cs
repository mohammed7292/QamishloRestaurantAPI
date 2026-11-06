using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/deliveryzones")]
public class DeliveryZonesController : ControllerBase
{
    private static readonly JsonSerializerOptions CoordinateJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // GET api/deliveryzones
    // Customers receive active zones by default. The restaurant app can request includeInactive=true.
    [HttpGet]
    public async Task<IActionResult> GetDeliveryZones(
        [FromQuery] bool includeInactive = false)
    {
        try
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            decimal minimumOrderAmount = 3500m;
            const string settingsSql = @"
                SELECT MinimumOrderAmount
                FROM restaurantsettings
                ORDER BY SettingID
                LIMIT 1;";

            await using (var settingsCommand = new MySqlCommand(settingsSql, connection))
            {
                var value = await settingsCommand.ExecuteScalarAsync();
                if (value != null && value != DBNull.Value)
                    minimumOrderAmount = Convert.ToDecimal(value);
            }

            string zonesSql = @"
                SELECT DeliveryZoneID, ZoneName, DeliveryFee,
                       EstimatedMinutes, Description, IsActive, ZoneCoordinates
                FROM deliveryzones
                WHERE (@IncludeInactive = 1 OR IsActive = 1)
                ORDER BY DeliveryZoneID;";

            var zones = new List<DeliveryZoneDto>();

            await using (var command = new MySqlCommand(zonesSql, connection))
            {
                command.Parameters.AddWithValue("@IncludeInactive", includeInactive ? 1 : 0);

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    zones.Add(new DeliveryZoneDto
                    {
                        DeliveryZoneID = reader.GetInt32(reader.GetOrdinal("DeliveryZoneID")),
                        ZoneName = reader.GetString(reader.GetOrdinal("ZoneName")),
                        DeliveryFee = reader.GetDecimal(reader.GetOrdinal("DeliveryFee")),
                        EstimatedMinutes = reader.IsDBNull(reader.GetOrdinal("EstimatedMinutes"))
                            ? null
                            : reader.GetInt32(reader.GetOrdinal("EstimatedMinutes")),
                        Description = reader.IsDBNull(reader.GetOrdinal("Description"))
                            ? null
                            : reader.GetString(reader.GetOrdinal("Description")),
                        IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                        ZoneCoordinates = reader.IsDBNull(reader.GetOrdinal("ZoneCoordinates"))
                            ? null
                            : reader.GetString("ZoneCoordinates")
                    });
                }
            }

            return Ok(new
            {
                success = true,
                minimumOrderAmount,
                zones
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = "Failed to load delivery configuration.",
                error = ex.Message
            });
        }
    }

    // POST api/deliveryzones
    [HttpPost]
    public async Task<IActionResult> CreateDeliveryZone(
        [FromBody] SaveDeliveryZoneRequest request)
    {
        if (!IsValidRequest(request, out string error))
            return BadRequest(new { success = false, message = error });

        try
        {
            const string sql = @"
                INSERT INTO deliveryzones
                    (ZoneName, DeliveryFee, EstimatedMinutes, Description,
                     IsActive, ZoneCoordinates)
                VALUES
                    (@ZoneName, @DeliveryFee, @EstimatedMinutes, @Description,
                     @IsActive, @ZoneCoordinates);";

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            AddZoneParameters(command, request);
            await command.ExecuteNonQueryAsync();

            return Ok(new
            {
                success = true,
                deliveryZoneID = command.LastInsertedId,
                message = "Delivery zone created successfully."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = "Failed to create delivery zone.",
                error = ex.Message
            });
        }
    }

    // PUT api/deliveryzones/5
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateDeliveryZone(
        int id,
        [FromBody] SaveDeliveryZoneRequest request)
    {
        if (id <= 0)
            return BadRequest(new { success = false, message = "Invalid delivery zone ID." });

        if (!IsValidRequest(request, out string error))
            return BadRequest(new { success = false, message = error });

        try
        {
            const string sql = @"
                UPDATE deliveryzones
                SET ZoneName = @ZoneName,
                    DeliveryFee = @DeliveryFee,
                    EstimatedMinutes = @EstimatedMinutes,
                    Description = @Description,
                    IsActive = @IsActive,
                    ZoneCoordinates = @ZoneCoordinates,
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE DeliveryZoneID = @DeliveryZoneID;";

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var command = new MySqlCommand(sql, connection);
            AddZoneParameters(command, request);
            command.Parameters.AddWithValue("@DeliveryZoneID", id);

            int affected = await command.ExecuteNonQueryAsync();
            if (affected == 0)
            {
                // A valid update can report zero changed rows when the submitted values are unchanged.
                await using var existsCommand = new MySqlCommand(
                    "SELECT COUNT(*) FROM deliveryzones WHERE DeliveryZoneID = @ID;", connection);
                existsCommand.Parameters.AddWithValue("@ID", id);
                var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync());
                if (exists == 0)
                    return NotFound(new { success = false, message = "Delivery zone not found." });
            }

            return Ok(new { success = true, message = "Delivery zone updated successfully." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = "Failed to update delivery zone.",
                error = ex.Message
            });
        }
    }

    // DELETE api/deliveryzones/5 (soft delete / disable)
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeactivateDeliveryZone(int id)
    {
        if (id <= 0)
            return BadRequest(new { success = false, message = "Invalid delivery zone ID." });

        try
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var command = new MySqlCommand(
                "UPDATE deliveryzones SET IsActive = 0, UpdatedAt = CURRENT_TIMESTAMP WHERE DeliveryZoneID = @ID;",
                connection);
            command.Parameters.AddWithValue("@ID", id);
            int affected = await command.ExecuteNonQueryAsync();

            if (affected == 0)
            {
                await using var existsCommand = new MySqlCommand(
                    "SELECT COUNT(*) FROM deliveryzones WHERE DeliveryZoneID = @ID;", connection);
                existsCommand.Parameters.AddWithValue("@ID", id);
                if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync()) == 0)
                    return NotFound(new { success = false, message = "Delivery zone not found." });
            }

            return Ok(new { success = true, message = "Delivery zone deactivated successfully." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = "Failed to deactivate delivery zone.",
                error = ex.Message
            });
        }
    }

    // PUT api/deliveryzones/minimum-order
    [HttpPut("minimum-order")]
    public async Task<IActionResult> UpdateMinimumOrder(
        [FromBody] MinimumOrderRequest request)
    {
        if (request.MinimumOrderAmount < 0)
            return BadRequest(new { success = false, message = "Minimum order cannot be negative." });

        try
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            await using var command = new MySqlCommand(@"
                UPDATE restaurantsettings
                SET MinimumOrderAmount = @Amount
                WHERE SettingID = (
                    SELECT SettingID FROM (
                        SELECT SettingID FROM restaurantsettings
                        ORDER BY SettingID LIMIT 1
                    ) AS first_setting
                );", connection);

            command.Parameters.AddWithValue("@Amount", request.MinimumOrderAmount);
            int affected = await command.ExecuteNonQueryAsync();

            if (affected == 0)
            {
                await using var existsCommand = new MySqlCommand(
                    "SELECT COUNT(*) FROM restaurantsettings;", connection);
                if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync()) == 0)
                {
                    return Conflict(new
                    {
                        success = false,
                        message = "The restaurantsettings table has no settings row to update."
                    });
                }
            }

            return Ok(new
            {
                success = true,
                minimumOrderAmount = request.MinimumOrderAmount,
                message = "Minimum order amount updated successfully."
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                message = "Failed to update minimum order amount.",
                error = ex.Message
            });
        }
    }

    private static void AddZoneParameters(
        MySqlCommand command,
        SaveDeliveryZoneRequest request)
    {
        command.Parameters.AddWithValue("@ZoneName", request.ZoneName.Trim());
        command.Parameters.AddWithValue("@DeliveryFee", request.DeliveryFee);
        command.Parameters.AddWithValue("@EstimatedMinutes",
            (object?)request.EstimatedMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("@Description", string.IsNullOrWhiteSpace(request.Description)
            ? DBNull.Value
            : request.Description.Trim());
        command.Parameters.AddWithValue("@IsActive", request.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("@ZoneCoordinates", request.ZoneCoordinates);
    }

    private static bool IsValidRequest(
        SaveDeliveryZoneRequest request,
        out string error)
    {
        error = "";

        if (request == null || string.IsNullOrWhiteSpace(request.ZoneName))
        {
            error = "Zone name is required.";
            return false;
        }

        if (request.DeliveryFee < 0)
        {
            error = "Delivery fee cannot be negative.";
            return false;
        }

        if (request.EstimatedMinutes.HasValue && request.EstimatedMinutes.Value < 0)
        {
            error = "Estimated minutes cannot be negative.";
            return false;
        }

        if (!TryReadCoordinates(request.ZoneCoordinates, out var points) || points.Count < 3)
        {
            error = "A delivery zone must contain at least three valid map points.";
            return false;
        }

        return true;
    }

    private static bool TryReadCoordinates(
        string? json,
        out List<DeliveryCoordinate> points)
    {
        points = new List<DeliveryCoordinate>();

        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            points = JsonSerializer.Deserialize<List<DeliveryCoordinate>>(
                json, CoordinateJsonOptions) ?? new List<DeliveryCoordinate>();

            return points.Count >= 3 && points.All(p =>
                double.IsFinite(p.Lat) && double.IsFinite(p.Lng) &&
                p.Lat >= -90 && p.Lat <= 90 &&
                p.Lng >= -180 && p.Lng <= 180);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public class DeliveryZoneDto
{
    public int DeliveryZoneID { get; set; }
    public string ZoneName { get; set; } = "";
    public decimal DeliveryFee { get; set; }
    public int? EstimatedMinutes { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public string? ZoneCoordinates { get; set; }
}

public class SaveDeliveryZoneRequest
{
    public string ZoneName { get; set; } = "";
    public decimal DeliveryFee { get; set; }
    public int? EstimatedMinutes { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public string ZoneCoordinates { get; set; } = "";
}

public class MinimumOrderRequest
{
    public decimal MinimumOrderAmount { get; set; }
}

public class DeliveryCoordinate
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lng")]
    public double Lng { get; set; }
}
