using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly ILogger<HealthController> _logger;

    public HealthController(ILogger<HealthController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            await using var command = new MySqlCommand("SELECT 1;", connection);
            await command.ExecuteScalarAsync();

            return Ok(new
            {
                status = "healthy",
                database = "connected",
                utc = DateTime.UtcNow
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "API health check failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "unhealthy",
                database = "unavailable"
            });
        }
    }
}
