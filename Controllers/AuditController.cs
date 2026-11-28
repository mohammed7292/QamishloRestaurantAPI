using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/audit")]
public sealed class AuditController : ControllerBase
{
    public static async Task WriteLogAsync(MySqlConnection conn, string actor, string role, string action, string entityType, string? entityId, string? details, string? ip = null)
    {
        try
        {
            await using var cmd = new MySqlCommand(@"
                INSERT INTO auditlogs (ActorUsername, ActorRole, Action, EntityType, EntityID, Details, IpAddress, CreatedAt)
                VALUES (@a, @r, @act, @et, @eid, @det, @ip, UTC_TIMESTAMP());", conn);
            cmd.Parameters.AddWithValue("@a", string.IsNullOrWhiteSpace(actor) ? "Manager" : actor);
            cmd.Parameters.AddWithValue("@r", string.IsNullOrWhiteSpace(role) ? "Manager" : role);
            cmd.Parameters.AddWithValue("@act", action);
            cmd.Parameters.AddWithValue("@et", entityType);
            cmd.Parameters.AddWithValue("@eid", (object?)entityId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@det", (object?)details ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ip", (object?)ip ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch { }
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetAuditLogs([FromQuery] int limit = 100)
    {
        limit = Math.Clamp(limit, 10, 500);
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var logs = new List<object>();
        await using var cmd = new MySqlCommand($"SELECT AuditID, ActorUsername, ActorRole, Action, EntityType, EntityID, Details, CreatedAt FROM auditlogs ORDER BY AuditID DESC LIMIT {limit};", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            logs.Add(new
            {
                auditID = r.GetInt64("AuditID"),
                actorUsername = r.GetString("ActorUsername"),
                actorRole = r.GetString("ActorRole"),
                action = r.GetString("Action"),
                entityType = r.GetString("EntityType"),
                entityID = r.IsDBNull(r.GetOrdinal("EntityID")) ? "" : r.GetString("EntityID"),
                details = r.IsDBNull(r.GetOrdinal("Details")) ? "" : r.GetString("Details"),
                createdAt = r.GetDateTime("CreatedAt")
            });
        }
        return Ok(new { success = true, count = logs.Count, logs });
    }
}
