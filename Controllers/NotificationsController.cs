using Microsoft.AspNetCore.Mvc;
using QamishloRestaurantAPI.Services;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly FirebaseNotificationService _firebase;
    private readonly IWebHostEnvironment _environment;

    public NotificationsController(
        FirebaseNotificationService firebase,
        IWebHostEnvironment environment)
    {
        _firebase = firebase;
        _environment = environment;
    }

    [HttpPost("test")]
    public async Task<IActionResult> SendTestNotification(
        [FromBody] TestNotificationRequest request)
    {
        // Keep this temporary test endpoint local-only.
        if (!_environment.IsDevelopment())
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.DeviceToken))
            return BadRequest("Device token is required.");

        try
        {
            var messageId = await _firebase.SendNotificationAsync(
                request.DeviceToken,
                "Qamishlo Restaurant",
                "Firebase connection successful! New notification test."
            );

            return Ok(new
            {
                success = true,
                message = "Notification sent successfully.",
                messageId
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                success = false,
                error = ex.Message
            });
        }
    }
}

public class TestNotificationRequest
{
    public string DeviceToken { get; set; } = string.Empty;
}