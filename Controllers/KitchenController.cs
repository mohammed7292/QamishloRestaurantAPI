using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/kitchen")]
public sealed class KitchenController : ControllerBase
{
    [HttpGet("stations")]
    public async Task<IActionResult> GetStations()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var list = new List<object>();
        await using var cmd = new MySqlCommand("SELECT StationID, StationName, StationCode, TargetPrepMinutes FROM kitchenstations WHERE IsActive = 1 ORDER BY StationID;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new { stationID = r.GetInt32(0), stationName = r.GetString(1), stationCode = r.GetString(2), targetPrepMinutes = r.GetInt32(3) });
        return Ok(new { success = true, stations = list });
    }

    [HttpGet("orders")]
    public async Task<IActionResult> GetKitchenOrders([FromQuery] string station = "ALL")
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var orders = new List<Dictionary<string, object?>>();

        await using (var cmd = new MySqlCommand(@"
            SELECT o.OrderID, o.OrderDate, o.OrderType, o.Status, o.Notes,
                   COALESCE(k.StationCode, 'GENERAL') AS StationCode,
                   COALESCE(k.KdsStatus, CASE WHEN o.Status='Ready' THEN 'Ready' WHEN o.Status='Preparing' THEN 'Preparing' ELSE 'Received' END) AS KdsStatus,
                   COALESCE(k.TargetMinutes, 15) AS TargetMinutes,
                   TIMESTAMPDIFF(MINUTE, o.OrderDate, NOW()) AS ElapsedMinutes
            FROM orders o
            LEFT JOIN kitchenordertracking k ON k.OrderID = o.OrderID
            WHERE o.IsArchived = 0 AND o.Status IN ('New','Confirmed','Preparing','Ready')
            ORDER BY o.OrderID ASC
            LIMIT 60;", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                string stCode = r.GetString("StationCode");
                if (!string.Equals(station, "ALL", StringComparison.OrdinalIgnoreCase) && !string.Equals(stCode, station, StringComparison.OrdinalIgnoreCase))
                    continue;
                int elapsed = Math.Max(0, Convert.ToInt32(r.GetValue(8)));
                int target = r.GetInt32("TargetMinutes");
                orders.Add(new Dictionary<string, object?>
                {
                    ["orderID"] = r.GetInt32("OrderID"),
                    ["orderDate"] = r.GetDateTime("OrderDate"),
                    ["orderType"] = r.GetString("OrderType"),
                    ["orderStatus"] = r.GetString("Status"),
                    ["notes"] = r.IsDBNull(r.GetOrdinal("Notes")) ? "" : r.GetString("Notes"),
                    ["stationCode"] = stCode,
                    ["kdsStatus"] = r.GetString("KdsStatus"),
                    ["targetMinutes"] = target,
                    ["elapsedMinutes"] = elapsed,
                    ["isDelayed"] = elapsed >= target,
                    ["items"] = new List<object>()
                });
            }
        }

        if (orders.Count > 0)
        {
            var ids = string.Join(",", orders.Select(o => o["orderID"]));
            var map = orders.ToDictionary(o => Convert.ToInt32(o["orderID"]), o => (List<object>)o["items"]!);
            await using var iCmd = new MySqlCommand($"SELECT od.OrderID, p.ProductName, od.Quantity FROM orderdetails od INNER JOIN products p ON p.ProductID = od.ProductID WHERE od.OrderID IN ({ids});", conn);
            await using var ir = await iCmd.ExecuteReaderAsync();
            while (await ir.ReadAsync())
            {
                int oid = ir.GetInt32(0);
                if (map.TryGetValue(oid, out var list))
                    list.Add(new { productName = ir.GetString(1), quantity = ir.GetInt32(2) });
            }
        }

        return Ok(new { success = true, count = orders.Count, orders });
    }

    [HttpPost("orders/{orderId:int}/status")]
    public async Task<IActionResult> UpdateKitchenStatus(int orderId, [FromBody] KitchenStatusDto req)
    {
        if (orderId <= 0 || string.IsNullOrWhiteSpace(req.KdsStatus))
            return BadRequest(new { success = false, message = "بيانات غير صالحة." });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        string st = string.IsNullOrWhiteSpace(req.StationCode) ? "GENERAL" : req.StationCode.Trim().ToUpperInvariant();
        string kds = req.KdsStatus.Trim();
        string actor = string.IsNullOrWhiteSpace(req.UpdatedBy) ? "Kitchen" : req.UpdatedBy.Trim();

        await using var cmd = new MySqlCommand(@"
            INSERT INTO kitchenordertracking (OrderID, StationCode, KdsStatus, StartedAt, ReadyAt, CompletedAt, UpdatedBy, UpdatedAt)
            VALUES (@oid, @st, @ks, CASE WHEN @ks='Preparing' THEN NOW() ELSE NULL END, CASE WHEN @ks='Ready' THEN NOW() ELSE NULL END, CASE WHEN @ks='Completed' THEN NOW() ELSE NULL END, @by, NOW())
            ON DUPLICATE KEY UPDATE
                KdsStatus = VALUES(KdsStatus),
                StartedAt = COALESCE(StartedAt, VALUES(StartedAt)),
                ReadyAt = COALESCE(ReadyAt, VALUES(ReadyAt)),
                CompletedAt = COALESCE(CompletedAt, VALUES(CompletedAt)),
                UpdatedBy = VALUES(UpdatedBy),
                UpdatedAt = NOW();", conn);
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@st", st);
        cmd.Parameters.AddWithValue("@ks", kds);
        cmd.Parameters.AddWithValue("@by", actor);
        await cmd.ExecuteNonQueryAsync();

        if (kds is "Preparing" or "Ready")
        {
            await using var oUpd = new MySqlCommand("UPDATE orders SET Status = @s, UpdatedAt = UTC_TIMESTAMP() WHERE OrderID = @oid AND Status IN ('New','Confirmed','Preparing');", conn);
            oUpd.Parameters.AddWithValue("@s", kds);
            oUpd.Parameters.AddWithValue("@oid", orderId);
            await oUpd.ExecuteNonQueryAsync();
            if (kds == "Preparing") await InventoryController.DeductStockForOrderAsync(conn, orderId);
        }

        return Ok(new { success = true, orderID = orderId, kdsStatus = kds });
    }
}

public sealed class KitchenStatusDto { public string? StationCode { get; set; } public string KdsStatus { get; set; } = "Preparing"; public string? UpdatedBy { get; set; } }
