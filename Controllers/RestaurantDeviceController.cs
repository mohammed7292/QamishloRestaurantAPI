using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/notifications/restaurant-device")]
public sealed class RestaurantDeviceController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Register([FromBody] RestaurantDeviceRegistration request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة لتسجيل جهاز المطعم." });

        if (request is null || string.IsNullOrWhiteSpace(request.DeviceToken) || request.DeviceToken.Length > 512)
            return BadRequest(new { success = false, message = "رمز الإشعارات غير صالح." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        const string sql = """
            INSERT INTO restaurantappdevices(DeviceToken, Platform, IsActive, CreatedAt, LastSeenAt)
            VALUES(@token, @platform, 1, UTC_TIMESTAMP(), UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE Platform=VALUES(Platform), IsActive=1, LastSeenAt=UTC_TIMESTAMP();
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@token", request.DeviceToken.Trim());
        command.Parameters.AddWithValue("@platform", string.IsNullOrWhiteSpace(request.Platform) ? "Android" : request.Platform.Trim());
        await command.ExecuteNonQueryAsync();
        return Ok(new { success = true, message = "تم تسجيل جهاز المطعم لاستقبال الإشعارات." });
    }

    [HttpDelete]
    public async Task<IActionResult> Unregister([FromQuery] string deviceToken)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false });
        if (string.IsNullOrWhiteSpace(deviceToken))
            return BadRequest(new { success = false });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "UPDATE restaurantappdevices SET IsActive=0 WHERE DeviceToken=@token", connection);
        command.Parameters.AddWithValue("@token", deviceToken.Trim());
        await command.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }
}

public sealed class RestaurantDeviceRegistration
{
    public string DeviceToken { get; set; } = string.Empty;
    public string Platform { get; set; } = "Android";
}
