using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Services;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/push")]
public sealed class PushNotificationsController : ControllerBase
{
    private readonly FirebaseNotificationService _fcm;

    public PushNotificationsController(FirebaseNotificationService fcm)
    {
        _fcm = fcm;
    }

    [HttpGet("public-config")]
    public IActionResult GetPublicConfig()
    {
        string apiKey = Environment.GetEnvironmentVariable("FIREBASE_WEB_API_KEY") ?? "";
        string projectId = Environment.GetEnvironmentVariable("FIREBASE_WEB_PROJECT_ID") ?? "";
        string senderId = Environment.GetEnvironmentVariable("FIREBASE_WEB_MESSAGING_SENDER_ID") ?? "";
        string appId = Environment.GetEnvironmentVariable("FIREBASE_WEB_APP_ID") ?? "";
        string vapidKey = Environment.GetEnvironmentVariable("FIREBASE_WEB_VAPID_KEY") ?? "";
        bool configured = !string.IsNullOrWhiteSpace(apiKey) && !string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(senderId);

        return Ok(new
        {
            success = true,
            configured,
            firebaseConfig = new { apiKey, projectId, messagingSenderId = senderId, appId },
            vapidKey
        });
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] PushRegisterRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AudienceType) || string.IsNullOrWhiteSpace(req.TokenOrEndpoint))
            return BadRequest(new { success = false, message = "Missing audience or token" });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        const string sql = @"
            INSERT INTO pushsubscriptions (AudienceType, TargetID, TokenOrEndpoint, P256dh, Auth, Platform, SoundKey, IsActive, UpdatedAt)
            VALUES (@aud, @tid, @tok, @p256, @auth, @plat, @snd, 1, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                AudienceType = VALUES(AudienceType),
                TargetID = VALUES(TargetID),
                P256dh = VALUES(P256dh),
                Auth = VALUES(Auth),
                Platform = VALUES(Platform),
                SoundKey = VALUES(SoundKey),
                IsActive = 1,
                UpdatedAt = UTC_TIMESTAMP();";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@aud", req.AudienceType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@tid", (object?)req.TargetID ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tok", req.TokenOrEndpoint.Trim());
        cmd.Parameters.AddWithValue("@p256", (object?)req.P256dh ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@auth", (object?)req.Auth ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@plat", string.IsNullOrWhiteSpace(req.Platform) ? "web" : req.Platform.Trim());
        cmd.Parameters.AddWithValue("@snd", (object?)req.SoundKey ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();

        return Ok(new { success = true, message = "تم تسجيل الجهاز لاستقبال الإشعارات بالخلفية." });
    }

    [HttpPost("test")]
    public async Task<IActionResult> TestPush([FromBody] PushTestRequest req)
    {
        await _fcm.SendToAudienceAsync(
            req.AudienceType ?? "restaurant",
            req.TargetID,
            req.Title ?? "🔔 تجربة إشعار قامشلو",
            req.Body ?? "الإشعارات بالخلفية تعمل بنجاح!",
            new Dictionary<string, string> { ["type"] = "test" });
        return Ok(new { success = true });
    }
}

public sealed class PushRegisterRequest
{
    public string AudienceType { get; set; } = "customer"; // customer | driver | restaurant
    public int? TargetID { get; set; }
    public string TokenOrEndpoint { get; set; } = "";
    public string? P256dh { get; set; }
    public string? Auth { get; set; }
    public string? Platform { get; set; }
    public string? SoundKey { get; set; }
}

public sealed class PushTestRequest
{
    public string? AudienceType { get; set; }
    public int? TargetID { get; set; }
    public string? Title { get; set; }
    public string? Body { get; set; }
}
