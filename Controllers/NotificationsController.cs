using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    [HttpGet("customer/{customerId:int}")]
    public async Task<IActionResult> GetCustomer(int customerId)
    {
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        var list = new List<object>();
        var seenTitlesAndBodies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (customerId > 0)
        {
            const string sql = @"
                SELECT NotificationID, OrderID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, IsRead, CreatedAt
                FROM customernotifications
                WHERE CustomerID = @id
                ORDER BY CreatedAt DESC
                LIMIT 100;";
            await using var cmd = new MySqlCommand(sql, c);
            cmd.Parameters.AddWithValue("@id", customerId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                string tAr = r.IsDBNull(r.GetOrdinal("TitleAR")) ? r.GetString("TitleEN") : r.GetString("TitleAR");
                string bAr = r.IsDBNull(r.GetOrdinal("BodyAR")) ? r.GetString("BodyEN") : r.GetString("BodyAR");
                seenTitlesAndBodies.Add($"{tAr}|{bAr}");

                list.Add(new
                {
                    notificationID = r.GetInt64("NotificationID"),
                    orderID = r.IsDBNull(r.GetOrdinal("OrderID")) ? (int?)null : r.GetInt32("OrderID"),
                    titleEN = r.GetString("TitleEN"),
                    titleAR = tAr,
                    titleKU = r.IsDBNull(r.GetOrdinal("TitleKU")) ? tAr : r.GetString("TitleKU"),
                    bodyEN = r.GetString("BodyEN"),
                    bodyAR = bAr,
                    bodyKU = r.IsDBNull(r.GetOrdinal("BodyKU")) ? bAr : r.GetString("BodyKU"),
                    isRead = r.GetBoolean("IsRead"),
                    createdAt = r.GetDateTime("CreatedAt")
                });
            }
        }

        // Also include general broadcast notifications so all customers (even guests or newly registered) see restaurant broadcasts
        try
        {
            const string broadcastSql = @"
                SELECT BroadcastID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, CouponCode, DiscountText, CreatedAt
                FROM broadcastnotifications
                WHERE IsActive = 1
                ORDER BY CreatedAt DESC
                LIMIT 50;";
            await using var bCmd = new MySqlCommand(broadcastSql, c);
            await using var br = await bCmd.ExecuteReaderAsync();
            while (await br.ReadAsync())
            {
                string tAr = br.GetString("TitleAR");
                string bAr = br.GetString("BodyAR");
                if (seenTitlesAndBodies.Contains($"{tAr}|{bAr}"))
                    continue;

                list.Add(new
                {
                    notificationID = 1000000L + br.GetInt64("BroadcastID"),
                    orderID = (int?)null,
                    titleEN = br.GetString("TitleEN"),
                    titleAR = tAr,
                    titleKU = br.GetString("TitleKU"),
                    bodyEN = br.GetString("BodyEN"),
                    bodyAR = bAr,
                    bodyKU = br.GetString("BodyKU"),
                    couponCode = br.IsDBNull(br.GetOrdinal("CouponCode")) ? null : br.GetString("CouponCode"),
                    discountText = br.IsDBNull(br.GetOrdinal("DiscountText")) ? null : br.GetString("DiscountText"),
                    isRead = false,
                    createdAt = br.GetDateTime("CreatedAt")
                });
            }
        }
        catch
        {
        }

        return Ok(new { success = true, notifications = list });
    }

    [HttpGet("broadcasts")]
    public async Task<IActionResult> GetBroadcasts()
    {
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        var list = new List<object>();

        try
        {
            const string sql = @"
                SELECT BroadcastID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, CouponCode, DiscountText, CreatedAt
                FROM broadcastnotifications
                WHERE IsActive = 1
                ORDER BY CreatedAt DESC
                LIMIT 50;";
            await using var cmd = new MySqlCommand(sql, c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new
                {
                    notificationID = r.GetInt64("BroadcastID"),
                    titleEN = r.GetString("TitleEN"),
                    titleAR = r.GetString("TitleAR"),
                    titleKU = r.GetString("TitleKU"),
                    bodyEN = r.GetString("BodyEN"),
                    bodyAR = r.GetString("BodyAR"),
                    bodyKU = r.GetString("BodyKU"),
                    couponCode = r.IsDBNull(r.GetOrdinal("CouponCode")) ? null : r.GetString("CouponCode"),
                    discountText = r.IsDBNull(r.GetOrdinal("DiscountText")) ? null : r.GetString("DiscountText"),
                    createdAt = r.GetDateTime("CreatedAt")
                });
            }
        }
        catch
        {
        }

        if (list.Count == 0)
        {
            try
            {
                const string fallbackSql = @"
                    SELECT MAX(NotificationID) AS NotificationID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, MAX(CreatedAt) AS CreatedAt
                    FROM customernotifications
                    WHERE IsBroadcast = 1 OR OrderID IS NULL
                    GROUP BY TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU
                    ORDER BY CreatedAt DESC
                    LIMIT 30;";
                await using var fCmd = new MySqlCommand(fallbackSql, c);
                await using var fr = await fCmd.ExecuteReaderAsync();
                while (await fr.ReadAsync())
                {
                    string tEn = fr.GetString("TitleEN");
                    string tAr = fr.IsDBNull(fr.GetOrdinal("TitleAR")) ? tEn : fr.GetString("TitleAR");
                    string bEn = fr.GetString("BodyEN");
                    string bAr = fr.IsDBNull(fr.GetOrdinal("BodyAR")) ? bEn : fr.GetString("BodyAR");
                    list.Add(new
                    {
                        notificationID = fr.GetInt64("NotificationID"),
                        titleEN = tEn,
                        titleAR = tAr,
                        titleKU = fr.IsDBNull(fr.GetOrdinal("TitleKU")) ? tAr : fr.GetString("TitleKU"),
                        bodyEN = bEn,
                        bodyAR = bAr,
                        bodyKU = fr.IsDBNull(fr.GetOrdinal("BodyKU")) ? bAr : fr.GetString("BodyKU"),
                        createdAt = fr.GetDateTime("CreatedAt")
                    });
                }
            }
            catch
            {
            }
        }

        return Ok(new { success = true, notifications = list });
    }

    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id, [FromQuery] int customerId)
    {
        if (id >= 1000000L || customerId <= 0)
            return Ok(new { success = true });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await using var cmd = new MySqlCommand(
            "UPDATE customernotifications SET IsRead=1 WHERE NotificationID=@id AND CustomerID=@customer", c);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@customer", customerId);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }

    
    [HttpDelete("{id:long}")]
    public async Task<IActionResult> DeleteNotification(long id)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("DELETE FROM customernotifications WHERE NotificationID = @id;", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }

    [HttpDelete("customer/{customerId:int}/clear")]
    public async Task<IActionResult> ClearCustomerNotifications(int customerId)
    {
        if (customerId <= 0) return BadRequest(new { success = false });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("DELETE FROM customernotifications WHERE CustomerID = @cid;", conn);
        cmd.Parameters.AddWithValue("@cid", customerId);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true, message = "تم حذف جميع الإشعارات." });
    }

    [HttpPost("broadcast")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastNotificationRequest? x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "يرجى التأكد من تسجيل دخول المدير." });

        if (x == null)
            return BadRequest(new { success = false, message = "بيانات الإشعار فارغة." });

        string primaryTitle = FirstNonEmpty(x.TitleAR, x.TitleEN, x.TitleKU, "إشعار وعرض من مطعم قامشلو 🔥");
        string primaryBody = FirstNonEmpty(x.BodyAR, x.BodyEN, x.BodyKU, primaryTitle);

        if (string.IsNullOrWhiteSpace(primaryBody) && string.IsNullOrWhiteSpace(x.TitleAR) && string.IsNullOrWhiteSpace(x.TitleEN) && string.IsNullOrWhiteSpace(x.TitleKU))
        {
            return BadRequest(new { success = false, message = "أدخل عنوان أو نص الإشعار أولاً." });
        }

        string titleEn = FirstNonEmpty(x.TitleEN, primaryTitle);
        string titleAr = FirstNonEmpty(x.TitleAR, primaryTitle);
        string titleKu = FirstNonEmpty(x.TitleKU, primaryTitle);

        string bodyEn = FirstNonEmpty(x.BodyEN, primaryBody);
        string bodyAr = FirstNonEmpty(x.BodyAR, primaryBody);
        string bodyKu = FirstNonEmpty(x.BodyKU, primaryBody);

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        // 1. Ensure broadcastnotifications table exists and store broadcast for all customers & Offers page
        try
        {
            const string ensureTableSql = @"
                CREATE TABLE IF NOT EXISTS broadcastnotifications (
                    BroadcastID BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                    TitleEN VARCHAR(200) NOT NULL,
                    TitleAR VARCHAR(200) NOT NULL,
                    TitleKU VARCHAR(200) NOT NULL,
                    BodyEN TEXT NOT NULL,
                    BodyAR TEXT NOT NULL,
                    BodyKU TEXT NOT NULL,
                    CouponCode VARCHAR(80) NULL,
                    DiscountText VARCHAR(100) NULL,
                    IsActive TINYINT(1) NOT NULL DEFAULT 1,
                    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
            await using (var ensureCmd = new MySqlCommand(ensureTableSql, c))
            {
                await ensureCmd.ExecuteNonQueryAsync();
            }

            const string insertBroadcastSql = @"
                INSERT INTO broadcastnotifications (TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, CouponCode, DiscountText, IsActive)
                VALUES (@te, @ta, @tk, @be, @ba, @bk, @code, @disc, 1);";
            await using var bCmd = new MySqlCommand(insertBroadcastSql, c);
            bCmd.Parameters.AddWithValue("@te", titleEn);
            bCmd.Parameters.AddWithValue("@ta", titleAr);
            bCmd.Parameters.AddWithValue("@tk", titleKu);
            bCmd.Parameters.AddWithValue("@be", bodyEn);
            bCmd.Parameters.AddWithValue("@ba", bodyAr);
            bCmd.Parameters.AddWithValue("@bk", bodyKu);
            bCmd.Parameters.AddWithValue("@code", string.IsNullOrWhiteSpace(x.CouponCode) ? DBNull.Value : x.CouponCode.Trim());
            bCmd.Parameters.AddWithValue("@disc", string.IsNullOrWhiteSpace(x.DiscountText) ? DBNull.Value : x.DiscountText.Trim());
            await bCmd.ExecuteNonQueryAsync();
        }
        catch
        {
        }

        // 2. Also insert into customernotifications for all existing active customers
        int notifiedCustomers = 0;
        try
        {
            const string sql = @"
                INSERT INTO customernotifications(CustomerID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, IsBroadcast)
                SELECT CustomerID, @te, @ta, @tk, @be, @ba, @bk, 1
                FROM customers
                WHERE IsActive = 1;";
            await using var cmd = new MySqlCommand(sql, c);
            cmd.Parameters.AddWithValue("@te", titleEn);
            cmd.Parameters.AddWithValue("@ta", titleAr);
            cmd.Parameters.AddWithValue("@tk", titleKu);
            cmd.Parameters.AddWithValue("@be", bodyEn);
            cmd.Parameters.AddWithValue("@ba", bodyAr);
            cmd.Parameters.AddWithValue("@bk", bodyKu);
            notifiedCustomers = await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Fallback without IsBroadcast column if older schema
            try
            {
                const string fallbackSql = @"
                    INSERT INTO customernotifications(CustomerID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU)
                    SELECT CustomerID, @te, @ta, @tk, @be, @ba, @bk
                    FROM customers
                    WHERE IsActive = 1;";
                await using var cmd2 = new MySqlCommand(fallbackSql, c);
                cmd2.Parameters.AddWithValue("@te", titleEn);
                cmd2.Parameters.AddWithValue("@ta", titleAr);
                cmd2.Parameters.AddWithValue("@tk", titleKu);
                cmd2.Parameters.AddWithValue("@be", bodyEn);
                cmd2.Parameters.AddWithValue("@ba", bodyAr);
                cmd2.Parameters.AddWithValue("@bk", bodyKu);
                notifiedCustomers = await cmd2.ExecuteNonQueryAsync();
            }
            catch
            {
            }
        }

        return Ok(new
        {
            success = true,
            notifiedCustomers,
            message = "تم إرسال الإشعار وتعميمه في صفحة العروض وإشعارات الزبائن بنجاح."
        });
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v.Trim();
        }
        return "";
    }

    public static async Task AddOrderStatusNotificationAsync(MySqlConnection c, MySqlTransaction tx, int customerId, int orderId, string status)
    {
        var (en, ar, ku) = status switch
        {
            "Preparing" => ("Your order was accepted and is now being prepared.", "تم قبول طلبك وهو قيد التحضير الآن.", "داواکارییەکەت پەسەند کرا و لە ئامادەکردندایە."),
            "Ready" => ("Your order is ready at the restaurant.", "طلبك أصبح جاهزاً في المطعم.", "داواکارییەکەت لە چێشتخانە ئامادەیە."),
            "OutForDelivery" => ("Your order has left the restaurant and is on the way.", "خرج طلبك من المطعم وهو في الطريق إليك.", "داواکارییەکەت لە چێشتخانە دەرچووە و لە ڕێگادایە."),
            "Delivered" => ("Your order has been delivered. Enjoy your meal!", "تم توصيل طلبك بنجاح. صحتين وعافية!", "داواکارییەکەت گەیەنرا."),
            "Rejected" => ("We apologize, your order could not be accepted at this time.", "نعتذر منك، تم رفض الطلب من قبل المطعم.", "داوای لێبوردن دەکەین، داواکارییەکەت ڕەتکرایەوە."),
            "Cancelled" => ("Your order has been cancelled.", "تم إلغاء الطلب.", "داواکارییەکەت هەڵوەشێنرایەوە."),
            _ => ("Your order status was updated.", "تم تحديث حالة طلبك.", "دۆخی داواکارییەکەت نوێکرایەوە.")
        };
        const string sql = "INSERT INTO customernotifications(CustomerID,OrderID,TitleEN,TitleAR,TitleKU,BodyEN,BodyAR,BodyKU) VALUES(@customer,@order,@te,@ta,@tk,@be,@ba,@bk)";
        await using var cmd = new MySqlCommand(sql, c, tx);
        cmd.Parameters.AddWithValue("@customer", customerId);
        cmd.Parameters.AddWithValue("@order", orderId);
        cmd.Parameters.AddWithValue("@te", "Order status");
        cmd.Parameters.AddWithValue("@ta", "حالة الطلب");
        cmd.Parameters.AddWithValue("@tk", "دۆخی داواکاری");
        cmd.Parameters.AddWithValue("@be", en);
        cmd.Parameters.AddWithValue("@ba", ar);
        cmd.Parameters.AddWithValue("@bk", ku);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed class BroadcastNotificationRequest
{
    public string? TitleEN { get; set; }
    public string? TitleAR { get; set; }
    public string? TitleKU { get; set; }
    public string? BodyEN { get; set; }
    public string? BodyAR { get; set; }
    public string? BodyKU { get; set; }
    public string? CouponCode { get; set; }
    public string? DiscountText { get; set; }
}
