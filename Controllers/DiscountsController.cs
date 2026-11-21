using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/discounts")]
public sealed class DiscountsController : ControllerBase
{
    private static readonly string[] ArabicDays =
    {
        "الأحد", "الإثنين", "الثلاثاء", "الأربعاء", "الخميس", "الجمعة", "السبت"
    };

    public static string GetArabicDayName(DateTime? dt)
        => dt.HasValue ? ArabicDays[(int)dt.Value.DayOfWeek] : "";

    public static async Task EnsureTablesAsync(MySqlConnection connection)
    {
        const string createCouponsSql = @"
            CREATE TABLE IF NOT EXISTS discountcoupons (
                CouponID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                CouponCode VARCHAR(80) NOT NULL,
                Title VARCHAR(200) NOT NULL DEFAULT 'قسيمة خصم من قامشلو',
                Description TEXT NULL,
                DiscountType VARCHAR(20) NOT NULL DEFAULT 'Percent',
                DiscountValue DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                StartDate DATETIME NULL,
                EndDate DATETIME NULL,
                MaxUses INT NULL,
                UsedCount INT NOT NULL DEFAULT 0,
                IsActive TINYINT(1) NOT NULL DEFAULT 1,
                ShowInOffersPage TINYINT(1) NOT NULL DEFAULT 1,
                CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                UNIQUE KEY UQ_DiscountCoupons_Code (CouponCode)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
        await using (var cmd = new MySqlCommand(createCouponsSql, connection))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        // Idempotently add new scheduling & usage-limit columns to existing databases
        var extraCols = new (string Name, string Def)[]
        {
            ("StartDate", "DATETIME NULL"),
            ("EndDate", "DATETIME NULL"),
            ("MaxUses", "INT NULL"),
            ("UsedCount", "INT NOT NULL DEFAULT 0")
        };

        foreach (var (colName, colDef) in extraCols)
        {
            try
            {
                const string checkSql = @"
                    SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
                    WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'discountcoupons' AND COLUMN_NAME = @col;";
                await using var checkCmd = new MySqlCommand(checkSql, connection);
                checkCmd.Parameters.AddWithValue("@col", colName);
                long exists = Convert.ToInt64(await checkCmd.ExecuteScalarAsync());
                if (exists == 0)
                {
                    await using var alterCmd = new MySqlCommand($"ALTER TABLE discountcoupons ADD COLUMN `{colName}` {colDef};", connection);
                    await alterCmd.ExecuteNonQueryAsync();
                }
            }
            catch
            {
            }
        }

        const string createBroadcastSql = @"
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
        await using (var bCmd = new MySqlCommand(createBroadcastSql, connection))
        {
            await bCmd.ExecuteNonQueryAsync();
        }
    }

    private static object BuildCouponDto(MySqlDataReader r, DateTime nowBaghdad)
    {
        string dType = r.GetString("DiscountType");
        decimal dVal = r.GetDecimal("DiscountValue");
        string badge = string.Equals(dType, "Fixed", StringComparison.OrdinalIgnoreCase)
            ? $"خصم {dVal:N0} د.ع"
            : $"خصم {dVal:0.##}%";

        int startOrd = r.GetOrdinal("StartDate");
        int endOrd = r.GetOrdinal("EndDate");
        int maxOrd = r.GetOrdinal("MaxUses");
        int usedOrd = r.GetOrdinal("UsedCount");

        DateTime? startDate = r.IsDBNull(startOrd) ? null : r.GetDateTime(startOrd);
        DateTime? endDate = r.IsDBNull(endOrd) ? null : r.GetDateTime(endOrd);
        int? maxUses = r.IsDBNull(maxOrd) ? null : r.GetInt32(maxOrd);
        int usedCount = r.IsDBNull(usedOrd) ? 0 : r.GetInt32(usedOrd);
        int? remainingUses = (maxUses.HasValue && maxUses.Value > 0) ? Math.Max(0, maxUses.Value - usedCount) : null;

        bool isNotStarted = startDate.HasValue && nowBaghdad < startDate.Value;
        bool isExpired = endDate.HasValue && nowBaghdad > endDate.Value;
        bool isExhausted = maxUses.HasValue && maxUses.Value > 0 && usedCount >= maxUses.Value;

        string scheduleText = "";
        if (startDate.HasValue && endDate.HasValue)
            scheduleText = $"من {GetArabicDayName(startDate)} {startDate:yyyy/MM/dd HH:mm} إلى {GetArabicDayName(endDate)} {endDate:yyyy/MM/dd HH:mm}";
        else if (startDate.HasValue)
            scheduleText = $"يبدأ {GetArabicDayName(startDate)} {startDate:yyyy/MM/dd HH:mm}";
        else if (endDate.HasValue)
            scheduleText = $"ينتهي {GetArabicDayName(endDate)} {endDate:yyyy/MM/dd HH:mm}";

        string usageText = (maxUses.HasValue && maxUses.Value > 0)
            ? $"المتبقي: {remainingUses} من أصل {maxUses.Value} شخص"
            : "استخدام مفتوح للجميع";

        return new
        {
            couponID = r.GetInt32("CouponID"),
            couponCode = r.GetString("CouponCode"),
            title = r.GetString("Title"),
            description = r.IsDBNull(r.GetOrdinal("Description")) ? "" : r.GetString("Description"),
            discountType = dType,
            discountValue = dVal,
            discountBadge = badge,
            startDate,
            endDate,
            startDayName = GetArabicDayName(startDate),
            endDayName = GetArabicDayName(endDate),
            scheduleText,
            maxUses,
            usedCount,
            remainingUses,
            usageText,
            isNotStarted,
            isExpired,
            isExhausted,
            isActive = r.GetBoolean("IsActive"),
            showInOffersPage = r.GetBoolean("ShowInOffersPage"),
            createdAt = r.GetDateTime("CreatedAt")
        };
    }

    // Public endpoint for Customer App & Customer Web Offers Page
    [HttpGet]
    public async Task<IActionResult> GetPublicOffersAndNotifications()
    {
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await EnsureTablesAsync(connection);

        DateTime nowBaghdad = ExpensesController.BaghdadNow();
        var coupons = new List<object>();
        const string couponsSql = @"
            SELECT CouponID, CouponCode, Title, Description, DiscountType, DiscountValue,
                   StartDate, EndDate, MaxUses, UsedCount, IsActive, ShowInOffersPage, CreatedAt
            FROM discountcoupons
            WHERE IsActive = 1
              AND ShowInOffersPage = 1
              AND (EndDate IS NULL OR EndDate >= @now)
              AND (MaxUses IS NULL OR MaxUses <= 0 OR UsedCount < MaxUses)
            ORDER BY CouponID DESC;";
        await using (var cmd = new MySqlCommand(couponsSql, connection))
        {
            cmd.Parameters.AddWithValue("@now", nowBaghdad);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                coupons.Add(BuildCouponDto(r, nowBaghdad));
            }
        }

        var notifications = new List<object>();
        try
        {
            const string notifSql = @"
                SELECT BroadcastID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, CouponCode, DiscountText, CreatedAt
                FROM broadcastnotifications
                WHERE IsActive = 1
                ORDER BY CreatedAt DESC
                LIMIT 40;";
            await using var nCmd = new MySqlCommand(notifSql, connection);
            await using var nr = await nCmd.ExecuteReaderAsync();
            while (await nr.ReadAsync())
            {
                notifications.Add(new
                {
                    notificationID = nr.GetInt64("BroadcastID"),
                    titleEN = nr.GetString("TitleEN"),
                    titleAR = nr.GetString("TitleAR"),
                    titleKU = nr.GetString("TitleKU"),
                    bodyEN = nr.GetString("BodyEN"),
                    bodyAR = nr.GetString("BodyAR"),
                    bodyKU = nr.GetString("BodyKU"),
                    couponCode = nr.IsDBNull(nr.GetOrdinal("CouponCode")) ? null : nr.GetString("CouponCode"),
                    discountText = nr.IsDBNull(nr.GetOrdinal("DiscountText")) ? null : nr.GetString("DiscountText"),
                    isRead = false,
                    isBroadcast = true,
                    createdAt = nr.GetDateTime("CreatedAt")
                });
            }
        }
        catch
        {
        }

        return Ok(new
        {
            success = true,
            serverTimeBaghdad = nowBaghdad,
            coupons,
            notifications
        });
    }

    // Manager endpoint to list all coupons
    [HttpGet("admin")]
    public async Task<IActionResult> GetAdminCoupons()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await EnsureTablesAsync(connection);

        DateTime nowBaghdad = ExpensesController.BaghdadNow();
        var coupons = new List<object>();
        const string sql = @"
            SELECT CouponID, CouponCode, Title, Description, DiscountType, DiscountValue,
                   StartDate, EndDate, MaxUses, UsedCount, IsActive, ShowInOffersPage, CreatedAt
            FROM discountcoupons
            ORDER BY CouponID DESC;";
        await using var cmd = new MySqlCommand(sql, connection);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            coupons.Add(BuildCouponDto(r, nowBaghdad));
        }

        return Ok(new { success = true, coupons });
    }

    // Manager endpoint to create/update a discount coupon with schedule & max-user limit
    [HttpPost]
    public async Task<IActionResult> CreateCoupon([FromBody] DiscountsCreateCouponRequest? request)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });

        if (request == null || string.IsNullOrWhiteSpace(request.CouponCode) || request.DiscountValue <= 0)
            return BadRequest(new { success = false, message = "أدخل كود/رقم القسيمة وقيمة الخصم بشكل صحيح." });

        if (request.StartDate.HasValue && request.EndDate.HasValue && request.EndDate.Value <= request.StartDate.Value)
            return BadRequest(new { success = false, message = "تاريخ ووقت انتهاء الخصم يجب أن يكون بعد تاريخ ووقت البداية." });

        string code = request.CouponCode.Trim();
        string dType = string.Equals(request.DiscountType, "Fixed", StringComparison.OrdinalIgnoreCase) ? "Fixed" : "Percent";
        decimal dVal = dType == "Percent" ? Math.Clamp(request.DiscountValue, 1m, 100m) : request.DiscountValue;
        string badgeText = dType == "Fixed" ? $"خصم {dVal:N0} د.ع" : $"خصم {dVal:0.##}%";
        int? maxUses = request.MaxUses.HasValue && request.MaxUses.Value > 0 ? request.MaxUses.Value : null;

        string title = string.IsNullOrWhiteSpace(request.Title)
            ? $"قسيمة {badgeText} من مطعم قامشلو 🔥"
            : request.Title.Trim();

        string schedulePart = "";
        if (request.StartDate.HasValue && request.EndDate.HasValue)
            schedulePart = $" (صالح من {GetArabicDayName(request.StartDate)} {request.StartDate:yyyy/MM/dd HH:mm} حتى {GetArabicDayName(request.EndDate)} {request.EndDate:yyyy/MM/dd HH:mm})";
        else if (request.EndDate.HasValue)
            schedulePart = $" (ينتهي {GetArabicDayName(request.EndDate)} {request.EndDate:yyyy/MM/dd HH:mm})";

        string limitPart = maxUses.HasValue ? $" — متاح لأول {maxUses.Value} شخص" : "";

        string description = string.IsNullOrWhiteSpace(request.Description)
            ? $"استخدم كود الخصم ({code}) للحصول على {badgeText}{schedulePart}{limitPart}!"
            : request.Description.Trim();

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await EnsureTablesAsync(connection);

        const string upsertSql = @"
            INSERT INTO discountcoupons (CouponCode, Title, Description, DiscountType, DiscountValue, StartDate, EndDate, MaxUses, UsedCount, IsActive, ShowInOffersPage)
            VALUES (@code, @title, @desc, @type, @val, @start, @end, @maxUses, 0, 1, @show)
            ON DUPLICATE KEY UPDATE
                Title = VALUES(Title),
                Description = VALUES(Description),
                DiscountType = VALUES(DiscountType),
                DiscountValue = VALUES(DiscountValue),
                StartDate = VALUES(StartDate),
                EndDate = VALUES(EndDate),
                MaxUses = VALUES(MaxUses),
                IsActive = 1,
                ShowInOffersPage = VALUES(ShowInOffersPage);";
        await using (var cmd = new MySqlCommand(upsertSql, connection))
        {
            cmd.Parameters.AddWithValue("@code", code);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@desc", description);
            cmd.Parameters.AddWithValue("@type", dType);
            cmd.Parameters.AddWithValue("@val", dVal);
            cmd.Parameters.AddWithValue("@start", (object?)request.StartDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@end", (object?)request.EndDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@maxUses", (object?)maxUses ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@show", request.ShowInOffersPage);
            await cmd.ExecuteNonQueryAsync();
        }

        if (request.ShowInOffersPage)
        {
            try
            {
                const string bSql = @"
                    INSERT INTO broadcastnotifications (TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, CouponCode, DiscountText, IsActive)
                    VALUES (@title, @title, @title, @body, @body, @body, @code, @badge, 1);";
                await using var bCmd = new MySqlCommand(bSql, connection);
                bCmd.Parameters.AddWithValue("@title", title);
                bCmd.Parameters.AddWithValue("@body", $"{description} — كود الخصم: {code}");
                bCmd.Parameters.AddWithValue("@code", code);
                bCmd.Parameters.AddWithValue("@badge", badgeText);
                await bCmd.ExecuteNonQueryAsync();

                const string cSql = @"
                    INSERT INTO customernotifications(CustomerID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, IsBroadcast)
                    SELECT CustomerID, @title, @title, @title, @body, @body, @body, 1
                    FROM customers
                    WHERE IsActive = 1;";
                await using var cCmd = new MySqlCommand(cSql, connection);
                cCmd.Parameters.AddWithValue("@title", title);
                cCmd.Parameters.AddWithValue("@body", $"{description} — كود الخصم: {code}");
                await cCmd.ExecuteNonQueryAsync();
            }
            catch
            {
            }
        }

        return Ok(new
        {
            success = true,
            couponCode = code,
            title,
            description,
            discountType = dType,
            discountValue = dVal,
            discountBadge = badgeText,
            startDate = request.StartDate,
            endDate = request.EndDate,
            maxUses,
            message = "تم حفظ قسيمة الخصم ونشرها في صفحة العروض للزبائن بنجاح."
        });
    }

    // Customer endpoint to validate a coupon code (enforcing start/end datetime and max uses)
    [HttpPost("validate")]
    public async Task<IActionResult> ValidateCoupon([FromBody] DiscountsValidateCouponRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Code))
            return BadRequest(new { success = false, message = "يرجى إدخال كود أو نص قسيمة الخصم." });

        string code = request.Code.Trim();
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await EnsureTablesAsync(connection);

        const string sql = @"
            SELECT CouponID, CouponCode, Title, Description, DiscountType, DiscountValue,
                   StartDate, EndDate, MaxUses, UsedCount
            FROM discountcoupons
            WHERE CouponCode = @code AND IsActive = 1
            LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, connection);
        cmd.Parameters.AddWithValue("@code", code);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
        {
            return NotFound(new
            {
                success = false,
                message = "كود الخصم غير صحيح أو غير مفعل."
            });
        }

        int couponId = r.GetInt32("CouponID");
        string matchedCode = r.GetString("CouponCode");
        string title = r.GetString("Title");
        string desc = r.IsDBNull(r.GetOrdinal("Description")) ? "" : r.GetString("Description");
        string dType = r.GetString("DiscountType");
        decimal dVal = r.GetDecimal("DiscountValue");

        DateTime? startDate = r.IsDBNull(r.GetOrdinal("StartDate")) ? null : r.GetDateTime("StartDate");
        DateTime? endDate = r.IsDBNull(r.GetOrdinal("EndDate")) ? null : r.GetDateTime("EndDate");
        int? maxUses = r.IsDBNull(r.GetOrdinal("MaxUses")) ? null : r.GetInt32("MaxUses");
        int usedCount = r.IsDBNull(r.GetOrdinal("UsedCount")) ? 0 : r.GetInt32("UsedCount");

        DateTime nowBaghdad = ExpensesController.BaghdadNow();

        if (startDate.HasValue && nowBaghdad < startDate.Value)
        {
            return BadRequest(new
            {
                success = false,
                message = $"هذا الخصم لم يبدأ بعد — يبدأ يوم {GetArabicDayName(startDate)} {startDate:yyyy/MM/dd} الساعة {startDate:HH:mm}."
            });
        }

        if (endDate.HasValue && nowBaghdad > endDate.Value)
        {
            return BadRequest(new
            {
                success = false,
                message = $"انتهت صلاحية كود الخصم يوم {GetArabicDayName(endDate)} {endDate:yyyy/MM/dd} الساعة {endDate:HH:mm}."
            });
        }

        if (maxUses.HasValue && maxUses.Value > 0 && usedCount >= maxUses.Value)
        {
            return BadRequest(new
            {
                success = false,
                message = $"تم استنفاد العدد المسموح به لهذا الخصم ({maxUses.Value} شخص)."
            });
        }

        decimal subtotal = Math.Max(0m, request.Subtotal);
        decimal calculatedDiscount = string.Equals(dType, "Fixed", StringComparison.OrdinalIgnoreCase)
            ? (subtotal > 0 ? Math.Min(subtotal, dVal) : dVal)
            : Math.Round(subtotal * Math.Clamp(dVal, 0m, 100m) / 100m, 2, MidpointRounding.AwayFromZero);

        string badge = string.Equals(dType, "Fixed", StringComparison.OrdinalIgnoreCase)
            ? $"{dVal:N0} د.ع"
            : $"{dVal:0.##}%";

        int? remainingUses = (maxUses.HasValue && maxUses.Value > 0) ? Math.Max(0, maxUses.Value - usedCount) : null;

        return Ok(new
        {
            success = true,
            couponID = couponId,
            couponCode = matchedCode,
            title,
            description = desc,
            discountType = dType,
            discountValue = dVal,
            startDate,
            endDate,
            maxUses,
            usedCount,
            remainingUses,
            calculatedDiscount,
            message = $"تم تفعيل قسيمة الخصم ({matchedCode}) بنجاح — قيمة الخصم: {badge}"
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteCoupon(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false });

        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await EnsureTablesAsync(connection);

        await using var cmd = new MySqlCommand("DELETE FROM discountcoupons WHERE CouponID = @id;", connection);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }
}

public sealed class DiscountsCreateCouponRequest
{
    public string CouponCode { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string DiscountType { get; set; } = "Percent";
    public decimal DiscountValue { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public int? MaxUses { get; set; }
    public bool ShowInOffersPage { get; set; } = true;
}

public sealed class DiscountsValidateCouponRequest
{
    public string Code { get; set; } = "";
    public decimal Subtotal { get; set; }
}
