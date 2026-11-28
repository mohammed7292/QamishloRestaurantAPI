using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;
using QamishloRestaurantAPI.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/special-offers")]
public sealed class SpecialOffersController : ControllerBase
{
    private readonly FirebaseNotificationService _fcm;

    public SpecialOffersController(FirebaseNotificationService fcm)
    {
        _fcm = fcm;
    }

    private static volatile bool _tableEnsured = false;
    private static async Task EnsureTableAsync(MySqlConnection conn)
    {
        if (_tableEnsured) return;
        const string sql = @"
            CREATE TABLE IF NOT EXISTS specialoffers (
                OfferID INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                TitleAR VARCHAR(200) NOT NULL,
                DescriptionAR TEXT NULL,
                OfferPrice DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                OriginalPrice DECIMAL(10,2) NOT NULL DEFAULT 0.00,
                ItemsJson LONGTEXT NOT NULL,
                ImageURL VARCHAR(500) NULL,
                IsActive TINYINT(1) NOT NULL DEFAULT 1,
                CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
        _tableEnsured = true;
    }

    private static object MapOffer(MySqlDataReader r) => new
    {
        offerID = r.GetInt32("OfferID"),
        titleAR = r.GetString("TitleAR"),
        descriptionAR = r.IsDBNull(r.GetOrdinal("DescriptionAR")) ? "" : r.GetString("DescriptionAR"),
        offerPrice = r.GetDecimal("OfferPrice"),
        originalPrice = r.GetDecimal("OriginalPrice"),
        itemsJson = r.IsDBNull(r.GetOrdinal("ItemsJson")) ? "[]" : r.GetString("ItemsJson"),
        imageURL = r.IsDBNull(r.GetOrdinal("ImageURL")) ? "" : r.GetString("ImageURL"),
        isActive = r.GetBoolean("IsActive"),
        createdAt = r.GetDateTime("CreatedAt")
    };

    [HttpGet]
    public async Task<IActionResult> GetActive()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await EnsureTableAsync(conn);
        var offers = new List<object>();
        const string sql = "SELECT OfferID, TitleAR, DescriptionAR, OfferPrice, OriginalPrice, ItemsJson, ImageURL, IsActive, CreatedAt FROM specialoffers WHERE IsActive = 1 ORDER BY OfferID DESC;";
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) offers.Add(MapOffer(r));
        return Ok(new { success = true, offers });
    }

    [HttpGet("admin")]
    public async Task<IActionResult> GetAll()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await EnsureTableAsync(conn);
        var offers = new List<object>();
        const string sql = "SELECT OfferID, TitleAR, DescriptionAR, OfferPrice, OriginalPrice, ItemsJson, ImageURL, IsActive, CreatedAt FROM specialoffers ORDER BY OfferID DESC;";
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) offers.Add(MapOffer(r));
        return Ok(new { success = true, offers });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSpecialOfferRequest? req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });
        if (req == null || string.IsNullOrWhiteSpace(req.TitleAR) || req.OfferPrice <= 0)
            return BadRequest(new { success = false, message = "أدخل اسم العرض وسعره بشكل صحيح." });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await EnsureTableAsync(conn);
        await DiscountsController.EnsureTablesAsync(conn);

        const string insSql = @"
            INSERT INTO specialoffers (TitleAR, DescriptionAR, OfferPrice, OriginalPrice, ItemsJson, ImageURL, IsActive)
            VALUES (@t, @d, @op, @orig, @items, @img, 1);
            SELECT LAST_INSERT_ID();";
        int newId;
        await using (var cmd = new MySqlCommand(insSql, conn))
        {
            cmd.Parameters.AddWithValue("@t", req.TitleAR.Trim());
            cmd.Parameters.AddWithValue("@d", string.IsNullOrWhiteSpace(req.DescriptionAR) ? DBNull.Value : req.DescriptionAR.Trim());
            cmd.Parameters.AddWithValue("@op", req.OfferPrice);
            cmd.Parameters.AddWithValue("@orig", req.OriginalPrice > 0 ? req.OriginalPrice : req.OfferPrice);
            cmd.Parameters.AddWithValue("@items", string.IsNullOrWhiteSpace(req.ItemsJson) ? "[]" : req.ItemsJson);
            cmd.Parameters.AddWithValue("@img", string.IsNullOrWhiteSpace(req.ImageURL) ? DBNull.Value : req.ImageURL.Trim());
            newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }

        // Immediately notify all customers (broadcast + customernotifications + FCM push)
        string notifTitle = $"🔥 عرض جديد: {req.TitleAR.Trim()}";
        string notifBody = $"{(string.IsNullOrWhiteSpace(req.DescriptionAR) ? "اطلب العرض الجديد الآن من صفحة العروض!" : req.DescriptionAR.Trim())} — السعر: {req.OfferPrice:N0} د.ع";
        try
        {
            const string bSql = @"
                INSERT INTO broadcastnotifications (TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, DiscountText, IsActive)
                VALUES (@t, @t, @t, @b, @b, @b, @p, 1);";
            await using var bCmd = new MySqlCommand(bSql, conn);
            bCmd.Parameters.AddWithValue("@t", notifTitle);
            bCmd.Parameters.AddWithValue("@b", notifBody);
            bCmd.Parameters.AddWithValue("@p", $"{req.OfferPrice:N0} د.ع");
            await bCmd.ExecuteNonQueryAsync();

            const string cSql = @"
                INSERT INTO customernotifications (CustomerID, TitleEN, TitleAR, TitleKU, BodyEN, BodyAR, BodyKU, IsRead)
                SELECT CustomerID, @t, @t, @t, @b, @b, @b, 0
                FROM customers WHERE IsActive = 1 AND PasswordHash IS NOT NULL;";
            await using var cCmd = new MySqlCommand(cSql, conn);
            cCmd.Parameters.AddWithValue("@t", notifTitle);
            cCmd.Parameters.AddWithValue("@b", notifBody);
            await cCmd.ExecuteNonQueryAsync();
        }
        catch { }

        try { await _fcm.SendToAudienceAsync("customer", null, notifTitle, notifBody); } catch { }

        return Ok(new { success = true, offerID = newId, message = "تم نشر العرض وإرسال إشعار لجميع الزبائن!" });
    }

    [HttpPut("{id:int}/toggle")]
    public async Task<IActionResult> Toggle(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await EnsureTableAsync(conn);
        await using var cmd = new MySqlCommand("UPDATE specialoffers SET IsActive = CASE WHEN IsActive=1 THEN 0 ELSE 1 END WHERE OfferID=@id;", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await EnsureTableAsync(conn);
        await using var cmd = new MySqlCommand("DELETE FROM specialoffers WHERE OfferID=@id;", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }
}

public sealed class CreateSpecialOfferRequest
{
    public string TitleAR { get; set; } = "";
    public string? DescriptionAR { get; set; }
    public decimal OfferPrice { get; set; }
    public decimal OriginalPrice { get; set; }
    public string ItemsJson { get; set; } = "[]";
    public string? ImageURL { get; set; }
}
