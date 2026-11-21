using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Services;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/marketing")]
public sealed class MarketingController : ControllerBase
{
    private readonly FirebaseNotificationService _fcm;

    public MarketingController(FirebaseNotificationService fcm)
    {
        _fcm = fcm;
    }

    [HttpGet("segments")]
    public async Task<IActionResult> GetMarketingSegments([FromQuery] int inactiveDays = 14)
    {
        inactiveDays = Math.Clamp(inactiveDays, 1, 365);
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();

        var inactive = new List<object>();
        const string inactiveSql = @"
            SELECT c.CustomerID, c.FullName, c.Phone,
                   MAX(o.OrderDate) AS LastOrderDate,
                   COUNT(o.OrderID) AS OrdersCount,
                   COALESCE(SUM(o.TotalAmount), 0) AS TotalSpent
            FROM customers c
            LEFT JOIN orders o ON o.CustomerID = c.CustomerID AND o.Status NOT IN ('Cancelled','Rejected')
            GROUP BY c.CustomerID, c.FullName, c.Phone
            HAVING LastOrderDate IS NULL OR LastOrderDate < DATE_SUB(NOW(), INTERVAL @days DAY)
            ORDER BY TotalSpent DESC, LastOrderDate ASC
            LIMIT 60;";
        await using (var cmd = new MySqlCommand(inactiveSql, conn))
        {
            cmd.Parameters.AddWithValue("@days", inactiveDays);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                DateTime? lastOrd = r.IsDBNull(r.GetOrdinal("LastOrderDate")) ? null : r.GetDateTime("LastOrderDate");
                int daysSince = lastOrd.HasValue ? (int)Math.Max(0, (DateTime.Now - lastOrd.Value).TotalDays) : 999;
                inactive.Add(new
                {
                    customerID = r.GetInt32("CustomerID"),
                    fullName = r.IsDBNull(r.GetOrdinal("FullName")) ? "زبون" : r.GetString("FullName"),
                    phone = r.IsDBNull(r.GetOrdinal("Phone")) ? "" : r.GetString("Phone"),
                    lastOrderDate = lastOrd?.ToString("yyyy-MM-dd") ?? "لم يطلب بعد",
                    daysSinceLastOrder = daysSince,
                    ordersCount = Convert.ToInt32(r.GetValue(4)),
                    totalSpent = Convert.ToDecimal(r.GetValue(5))
                });
            }
        }

        var birthdays = new List<object>();
        try
        {
            const string bdaySql = @"
                SELECT CustomerID, FullName, Phone, BirthDate
                FROM customers
                WHERE BirthDate IS NOT NULL
                  AND MONTH(BirthDate) = MONTH(CURDATE())
                ORDER BY DAY(BirthDate) ASC
                LIMIT 50;";
            await using var bCmd = new MySqlCommand(bdaySql, conn);
            await using var br = await bCmd.ExecuteReaderAsync();
            while (await br.ReadAsync())
            {
                birthdays.Add(new
                {
                    customerID = br.GetInt32("CustomerID"),
                    fullName = br.IsDBNull(br.GetOrdinal("FullName")) ? "زبون" : br.GetString("FullName"),
                    phone = br.IsDBNull(br.GetOrdinal("Phone")) ? "" : br.GetString("Phone"),
                    birthDate = br.GetDateTime("BirthDate").ToString("yyyy-MM-dd")
                });
            }
        }
        catch { }

        return Ok(new
        {
            success = true,
            inactiveDays,
            inactiveCount = inactive.Count,
            inactiveCustomers = inactive,
            birthdayCount = birthdays.Count,
            birthdayCustomers = birthdays
        });
    }

    [HttpPost("send-offer")]
    public async Task<IActionResult> SendTargetedOffer([FromBody] TargetedOfferRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Message))
            return BadRequest(new { success = false, message = "عنوان العرض ونصه مطلوبان." });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var targets = req.CustomerIDs ?? new List<int>();
        int sent = 0;

        foreach (int cid in targets.Distinct().Where(x => x > 0))
        {
            try
            {
                await using var ins = new MySqlCommand(@"
                    INSERT INTO customernotifications (CustomerID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, IsRead, CreatedAt)
                    VALUES (@cid, @t, @t, @t, @b, @b, @b, 0, UTC_TIMESTAMP());", conn);
                ins.Parameters.AddWithValue("@cid", cid);
                ins.Parameters.AddWithValue("@t", req.Title.Trim());
                ins.Parameters.AddWithValue("@b", req.Message.Trim());
                await ins.ExecuteNonQueryAsync();
                await _fcm.SendToAudienceAsync("customer", cid, req.Title.Trim(), req.Message.Trim());
                sent++;
            }
            catch { }
        }

        try
        {
            await using var log = new MySqlCommand(@"
                INSERT INTO marketingcampaigns (CampaignType, Title, Message, CouponCode, TargetCount)
                VALUES (@type, @t, @m, @code, @cnt);", conn);
            log.Parameters.AddWithValue("@type", req.CampaignType ?? "InactiveComeback");
            log.Parameters.AddWithValue("@t", req.Title.Trim());
            log.Parameters.AddWithValue("@m", req.Message.Trim());
            log.Parameters.AddWithValue("@code", (object?)req.CouponCode ?? DBNull.Value);
            log.Parameters.AddWithValue("@cnt", sent);
            await log.ExecuteNonQueryAsync();
        }
        catch { }

        return Ok(new { success = true, sentCount = sent, message = $"تم إرسال العرض الذكي إلى {sent} زبون بنجاح!" });
    }
}

public sealed class TargetedOfferRequest
{
    public string? CampaignType { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string? CouponCode { get; set; }
    public List<int>? CustomerIDs { get; set; }
}
