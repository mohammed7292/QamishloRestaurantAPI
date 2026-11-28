using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/loyalty")]
public sealed class LoyaltyController : ControllerBase
{
    private static async Task<(bool IsEnabled, decimal EarnPerAmountIQD, int CoinsPerStep, decimal CoinValueIQD, int MinCoinsToRedeem, int CoinsExpiryDays, DateTime? ValidUntilDate)> LoadSettingsAsync(MySqlConnection conn)
    {
        try
        {
            await using var cmd = new MySqlCommand("SELECT IsEnabled, EarnPerAmountIQD, CoinsPerStep, CoinValueIQD, MinCoinsToRedeem, CoinsExpiryDays, ValidUntilDate FROM loyaltysettings WHERE SettingID = 1 LIMIT 1;", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                return (
                    r.GetBoolean("IsEnabled"),
                    r.GetDecimal("EarnPerAmountIQD"),
                    r.GetInt32("CoinsPerStep"),
                    r.GetDecimal("CoinValueIQD"),
                    r.GetInt32("MinCoinsToRedeem"),
                    r.IsDBNull(r.GetOrdinal("CoinsExpiryDays")) ? 90 : r.GetInt32("CoinsExpiryDays"),
                    r.IsDBNull(r.GetOrdinal("ValidUntilDate")) ? (DateTime?)null : r.GetDateTime("ValidUntilDate")
                );
            }
        }
        catch
        {
            await using var cmd = new MySqlCommand("SELECT IsEnabled, EarnPerAmountIQD, CoinsPerStep, CoinValueIQD, MinCoinsToRedeem FROM loyaltysettings WHERE SettingID = 1 LIMIT 1;", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                return (r.GetBoolean(0), r.GetDecimal(1), r.GetInt32(2), r.GetDecimal(3), r.GetInt32(4), 90, null);
            }
        }
        return (true, 1000m, 1, 50m, 20, 90, null);
    }

    public static async Task<int> TryAwardCoinsForOrderAsync(MySqlConnection conn, int orderId)
    {
        try
        {
            var s = await LoadSettingsAsync(conn);
            if (!s.IsEnabled) return 0;
            if (s.ValidUntilDate.HasValue && DateTime.UtcNow > s.ValidUntilDate.Value) return 0;

            await using var chk = new MySqlCommand("SELECT COUNT(*) FROM loyaltytransactions WHERE OrderID = @oid AND DeltaCoins > 0;", conn);
            chk.Parameters.AddWithValue("@oid", orderId);
            if (Convert.ToInt64(await chk.ExecuteScalarAsync()) > 0) return 0;

            int? customerId = null;
            decimal total = 0;
            await using (var oCmd = new MySqlCommand("SELECT CustomerID, TotalAmount FROM orders WHERE OrderID = @oid LIMIT 1;", conn))
            {
                oCmd.Parameters.AddWithValue("@oid", orderId);
                await using var r = await oCmd.ExecuteReaderAsync();
                if (await r.ReadAsync() && !r.IsDBNull(0))
                {
                    customerId = r.GetInt32(0);
                    total = r.GetDecimal(1);
                }
            }
            if (!customerId.HasValue || customerId.Value <= 0 || total <= 0) return 0;

            int steps = (int)Math.Floor(total / Math.Max(1m, s.EarnPerAmountIQD));
            int awarded = Math.Max(1, steps * Math.Max(1, s.CoinsPerStep));

            await using var tx = await conn.BeginTransactionAsync();
            await using (var up = new MySqlCommand(@"
                INSERT INTO customerloyalty (CustomerID, CoinsBalance, TotalEarned, TotalRedeemed, UpdatedAt)
                VALUES (@cid, @c, @c, 0, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE
                    CoinsBalance = CoinsBalance + @c,
                    TotalEarned = TotalEarned + @c,
                    UpdatedAt = UTC_TIMESTAMP();", conn, tx))
            {
                up.Parameters.AddWithValue("@cid", customerId.Value);
                up.Parameters.AddWithValue("@c", awarded);
                await up.ExecuteNonQueryAsync();
            }
            await using (var log = new MySqlCommand(@"
                INSERT INTO loyaltytransactions (CustomerID, OrderID, DeltaCoins, Reason)
                VALUES (@cid, @oid, @c, @reason);", conn, tx))
            {
                log.Parameters.AddWithValue("@cid", customerId.Value);
                log.Parameters.AddWithValue("@oid", orderId);
                log.Parameters.AddWithValue("@c", awarded);
                log.Parameters.AddWithValue("@reason", $"مكافأة طلب #{orderId} (قامشلو كوينز)");
                await log.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return awarded;
        }
        catch { return 0; }
    }

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var s = await LoadSettingsAsync(conn);
        var rewards = await LoadRewardsInternalAsync(conn);
        return Ok(new
        {
            success = true,
            coinNameAR = "قامشلو كوينز",
            coinNameEN = "Qamishlo Coins",
            coinNameKU = "کۆینەکانی قامشلو",
            isEnabled = s.IsEnabled,
            earnPerAmountIQD = s.EarnPerAmountIQD,
            coinsPerStep = s.CoinsPerStep,
            coinValueIQD = s.CoinValueIQD,
            minCoinsToRedeem = s.MinCoinsToRedeem,
            coinsExpiryDays = s.CoinsExpiryDays,
            validUntilDate = s.ValidUntilDate?.ToString("yyyy-MM-dd"),
            rewards
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] LoyaltySettingsDto req)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        DateTime? validUntil = null;
        if (!string.IsNullOrWhiteSpace(req.ValidUntilDate) && DateTime.TryParse(req.ValidUntilDate, out var parsed))
            validUntil = parsed;

        const string sql = @"
            INSERT INTO loyaltysettings (SettingID, IsEnabled, EarnPerAmountIQD, CoinsPerStep, CoinValueIQD, MinCoinsToRedeem, CoinsExpiryDays, ValidUntilDate, UpdatedAt)
            VALUES (1, @en, @earn, @step, @val, @min, @exp, @until, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                IsEnabled = VALUES(IsEnabled),
                EarnPerAmountIQD = VALUES(EarnPerAmountIQD),
                CoinsPerStep = VALUES(CoinsPerStep),
                CoinValueIQD = VALUES(CoinValueIQD),
                MinCoinsToRedeem = VALUES(MinCoinsToRedeem),
                CoinsExpiryDays = VALUES(CoinsExpiryDays),
                ValidUntilDate = VALUES(ValidUntilDate),
                UpdatedAt = UTC_TIMESTAMP();";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@en", req.IsEnabled);
        cmd.Parameters.AddWithValue("@earn", Math.Max(100m, req.EarnPerAmountIQD));
        cmd.Parameters.AddWithValue("@step", Math.Max(1, req.CoinsPerStep));
        cmd.Parameters.AddWithValue("@val", Math.Max(1m, req.CoinValueIQD));
        cmd.Parameters.AddWithValue("@min", Math.Max(1, req.MinCoinsToRedeem));
        cmd.Parameters.AddWithValue("@exp", Math.Max(1, req.CoinsExpiryDays));
        cmd.Parameters.AddWithValue("@until", (object?)validUntil ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true, message = "تم تحديث إعدادات وموعد صلاحية قامشلو كوينز بنجاح." });
    }

    private static async Task<List<object>> LoadRewardsInternalAsync(MySqlConnection conn)
    {
        var list = new List<object>();
        try
        {
            await using var cmd = new MySqlCommand("SELECT RewardID, Title, Description, CoinsCost, DiscountIQD, RewardType FROM loyaltyrewards WHERE IsActive = 1 ORDER BY CoinsCost ASC;", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new
                {
                    rewardID = r.GetInt32("RewardID"),
                    title = r.GetString("Title"),
                    description = r.IsDBNull(r.GetOrdinal("Description")) ? "" : r.GetString("Description"),
                    coinsCost = r.GetInt32("CoinsCost"),
                    discountIQD = r.GetDecimal("DiscountIQD"),
                    rewardType = r.GetString("RewardType")
                });
            }
        }
        catch { }
        return list;
    }

    [HttpGet("rewards")]
    public async Task<IActionResult> GetRewards()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        return Ok(new { success = true, rewards = await LoadRewardsInternalAsync(conn) });
    }

    [HttpPost("rewards")]
    public async Task<IActionResult> AddReward([FromBody] LoyaltyRewardDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || req.CoinsCost <= 0)
            return BadRequest(new { success = false, message = "اسم المكافأة وعدد الكوينز مطلوبان." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("INSERT INTO loyaltyrewards (Title, Description, CoinsCost, DiscountIQD, RewardType, IsActive) VALUES (@t, @d, @c, @iqd, @rt, 1); SELECT LAST_INSERT_ID();", conn);
        cmd.Parameters.AddWithValue("@t", req.Title.Trim());
        cmd.Parameters.AddWithValue("@d", (object?)req.Description?.Trim() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@c", req.CoinsCost);
        cmd.Parameters.AddWithValue("@iqd", Math.Max(0m, req.DiscountIQD));
        cmd.Parameters.AddWithValue("@rt", string.IsNullOrWhiteSpace(req.RewardType) ? "Discount" : req.RewardType.Trim());
        int id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return Ok(new { success = true, rewardID = id });
    }

    [HttpDelete("rewards/{rewardId:int}")]
    public async Task<IActionResult> DeleteReward(int rewardId)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("DELETE FROM loyaltyrewards WHERE RewardID = @id;", conn);
        cmd.Parameters.AddWithValue("@id", rewardId);
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true });
    }

    [HttpGet("customer/{customerId:int}")]
    public async Task<IActionResult> GetCustomerBalance(int customerId)
    {
        if (customerId <= 0) return BadRequest(new { success = false, message = "Invalid customer ID" });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var s = await LoadSettingsAsync(conn);
        var rewards = await LoadRewardsInternalAsync(conn);

        int balance = 0, earned = 0, redeemed = 0;
        await using (var cmd = new MySqlCommand("SELECT CoinsBalance, TotalEarned, TotalRedeemed FROM customerloyalty WHERE CustomerID = @cid LIMIT 1;", conn))
        {
            cmd.Parameters.AddWithValue("@cid", customerId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                balance = r.GetInt32("CoinsBalance");
                earned = r.GetInt32("TotalEarned");
                redeemed = r.GetInt32("TotalRedeemed");
            }
        }

        var txs = new List<object>();
        await using (var txCmd = new MySqlCommand("SELECT TransactionID, OrderID, DeltaCoins, Reason, CreatedAt FROM loyaltytransactions WHERE CustomerID = @cid ORDER BY TransactionID DESC LIMIT 20;", conn))
        {
            txCmd.Parameters.AddWithValue("@cid", customerId);
            await using var tr = await txCmd.ExecuteReaderAsync();
            while (await tr.ReadAsync())
            {
                txs.Add(new
                {
                    transactionID = tr.GetInt64("TransactionID"),
                    orderID = tr.IsDBNull(tr.GetOrdinal("OrderID")) ? (int?)null : tr.GetInt32("OrderID"),
                    deltaCoins = tr.GetInt32("DeltaCoins"),
                    reason = tr.GetString("Reason"),
                    createdAt = tr.GetDateTime("CreatedAt")
                });
            }
        }

        bool notExpired = !s.ValidUntilDate.HasValue || DateTime.UtcNow <= s.ValidUntilDate.Value;
        string tier = earned >= 300 ? "VIP" : earned >= 100 ? "Gold" : "Silver";
        return Ok(new
        {
            success = true,
            customerID = customerId,
            coinsBalance = balance,
            totalEarned = earned,
            totalRedeemed = redeemed,
            membershipTier = tier,
            isEnabled = s.IsEnabled && notExpired,
            earnPerAmountIQD = s.EarnPerAmountIQD,
            coinsPerStep = s.CoinsPerStep,
            coinValueIQD = s.CoinValueIQD,
            minCoinsToRedeem = s.MinCoinsToRedeem,
            coinsExpiryDays = s.CoinsExpiryDays,
            validUntilDate = s.ValidUntilDate?.ToString("yyyy-MM-dd"),
            balanceValueIQD = balance * s.CoinValueIQD,
            canRedeem = s.IsEnabled && notExpired && balance >= s.MinCoinsToRedeem,
            rewards,
            transactions = txs
        });
    }

    [HttpGet("customers")]
    public async Task<IActionResult> ListCustomerBalances()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var s = await LoadSettingsAsync(conn);
        var list = new List<object>();
        const string sql = @"
            SELECT c.CustomerID, c.FullName, c.Phone,
                   COALESCE(cl.CoinsBalance, 0) AS CoinsBalance,
                   COALESCE(cl.TotalEarned, 0) AS TotalEarned,
                   COALESCE(cl.TotalRedeemed, 0) AS TotalRedeemed
            FROM customers c
            LEFT JOIN customerloyalty cl ON cl.CustomerID = c.CustomerID
            ORDER BY COALESCE(cl.CoinsBalance, 0) DESC, c.CustomerID DESC
            LIMIT 100;";
        await using var cmd = new MySqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            int bal = r.GetInt32("CoinsBalance");
            list.Add(new
            {
                customerID = r.GetInt32("CustomerID"),
                fullName = r.IsDBNull(r.GetOrdinal("FullName")) ? "" : r.GetString("FullName"),
                phone = r.IsDBNull(r.GetOrdinal("Phone")) ? "" : r.GetString("Phone"),
                coinsBalance = bal,
                balanceValueIQD = bal * s.CoinValueIQD,
                totalEarned = r.GetInt32("TotalEarned"),
                totalRedeemed = r.GetInt32("TotalRedeemed")
            });
        }
        return Ok(new { success = true, coinValueIQD = s.CoinValueIQD, customers = list });
    }

    [HttpPost("award")]
    public async Task<IActionResult> AwardCoins([FromBody] LoyaltyAwardDto req)
    {
        if (req.CustomerID <= 0 || req.OrderAmountIQD <= 0)
            return BadRequest(new { success = false, message = "Invalid payload" });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var s = await LoadSettingsAsync(conn);
        if (!s.IsEnabled)
            return Ok(new { success = true, awardedCoins = 0 });

        if (req.OrderID.HasValue && req.OrderID.Value > 0)
        {
            await using var chk = new MySqlCommand("SELECT COUNT(*) FROM loyaltytransactions WHERE OrderID = @oid AND DeltaCoins > 0;", conn);
            chk.Parameters.AddWithValue("@oid", req.OrderID.Value);
            if (Convert.ToInt64(await chk.ExecuteScalarAsync()) > 0)
                return Ok(new { success = true, awardedCoins = 0 });
        }

        int steps = (int)Math.Floor(req.OrderAmountIQD / Math.Max(1m, s.EarnPerAmountIQD));
        int awarded = Math.Max(1, steps * Math.Max(1, s.CoinsPerStep));

        await using var tx = await conn.BeginTransactionAsync();
        await using (var up = new MySqlCommand(@"
            INSERT INTO customerloyalty (CustomerID, CoinsBalance, TotalEarned, TotalRedeemed, UpdatedAt)
            VALUES (@cid, @c, @c, 0, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                CoinsBalance = CoinsBalance + @c,
                TotalEarned = TotalEarned + @c,
                UpdatedAt = UTC_TIMESTAMP();", conn, tx))
        {
            up.Parameters.AddWithValue("@cid", req.CustomerID);
            up.Parameters.AddWithValue("@c", awarded);
            await up.ExecuteNonQueryAsync();
        }
        await using (var log = new MySqlCommand(@"
            INSERT INTO loyaltytransactions (CustomerID, OrderID, DeltaCoins, Reason)
            VALUES (@cid, @oid, @c, @reason);", conn, tx))
        {
            log.Parameters.AddWithValue("@cid", req.CustomerID);
            log.Parameters.AddWithValue("@oid", (object?)req.OrderID ?? DBNull.Value);
            log.Parameters.AddWithValue("@c", awarded);
            log.Parameters.AddWithValue("@reason", req.OrderID.HasValue ? $"مكافأة طلب #{req.OrderID.Value}" : "مكافأة طلب قامشلو");
            await log.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return Ok(new { success = true, awardedCoins = awarded });
    }

    [HttpPost("redeem")]
    public async Task<IActionResult> RedeemCoins([FromBody] LoyaltyRedeemDto req)
    {
        if (req.CustomerID <= 0)
            return BadRequest(new { success = false, message = "بيانات الزبون غير صالحة." });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var s = await LoadSettingsAsync(conn);
        if (!s.IsEnabled)
            return BadRequest(new { success = false, message = "نظام قامشلو كوينز متوقف حالياً." });
        if (s.ValidUntilDate.HasValue && DateTime.UtcNow > s.ValidUntilDate.Value)
            return BadRequest(new { success = false, message = "انتهت فترة صلاحية استبدال قامشلو كوينز." });

        int coinsToSpend = req.Coins;
        decimal discountIQD = 0m;
        string rewardLabel = "";

        if (req.RewardID.HasValue && req.RewardID.Value > 0)
        {
            await using var rCmd = new MySqlCommand("SELECT Title, CoinsCost, DiscountIQD FROM loyaltyrewards WHERE RewardID = @rid AND IsActive = 1 LIMIT 1;", conn);
            rCmd.Parameters.AddWithValue("@rid", req.RewardID.Value);
            await using var rr = await rCmd.ExecuteReaderAsync();
            if (!await rr.ReadAsync())
                return NotFound(new { success = false, message = "المكافأة غير موجودة." });
            rewardLabel = rr.GetString("Title");
            coinsToSpend = rr.GetInt32("CoinsCost");
            discountIQD = rr.GetDecimal("DiscountIQD");
        }
        else
        {
            if (coinsToSpend < s.MinCoinsToRedeem)
                return BadRequest(new { success = false, message = $"الحد الأدنى للاستبدال هو {s.MinCoinsToRedeem} قامشلو كوين." });
            discountIQD = coinsToSpend * s.CoinValueIQD;
            rewardLabel = $"خصم {discountIQD:N0} د.ع";
        }

        await using var tx = await conn.BeginTransactionAsync();
        int bal = 0;
        await using (var getCmd = new MySqlCommand("SELECT CoinsBalance FROM customerloyalty WHERE CustomerID = @cid FOR UPDATE;", conn, tx))
        {
            getCmd.Parameters.AddWithValue("@cid", req.CustomerID);
            var obj = await getCmd.ExecuteScalarAsync();
            if (obj != null && obj != DBNull.Value) bal = Convert.ToInt32(obj);
        }
        if (bal < coinsToSpend)
        {
            await tx.RollbackAsync();
            return BadRequest(new { success = false, message = $"رصيدك ({bal} كوين) غير كافٍ، المطلوب {coinsToSpend} كوين." });
        }

        await using (var upd = new MySqlCommand(@"
            UPDATE customerloyalty
            SET CoinsBalance = CoinsBalance - @c,
                TotalRedeemed = TotalRedeemed + @c,
                UpdatedAt = UTC_TIMESTAMP()
            WHERE CustomerID = @cid;", conn, tx))
        {
            upd.Parameters.AddWithValue("@c", coinsToSpend);
            upd.Parameters.AddWithValue("@cid", req.CustomerID);
            await upd.ExecuteNonQueryAsync();
        }
        await using (var log = new MySqlCommand(@"
            INSERT INTO loyaltytransactions (CustomerID, OrderID, DeltaCoins, Reason)
            VALUES (@cid, @oid, @delta, @reason);", conn, tx))
        {
            log.Parameters.AddWithValue("@cid", req.CustomerID);
            log.Parameters.AddWithValue("@oid", (object?)req.OrderID ?? DBNull.Value);
            log.Parameters.AddWithValue("@delta", -coinsToSpend);
            log.Parameters.AddWithValue("@reason", $"استبدال {coinsToSpend} كوين: {rewardLabel}");
            await log.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return Ok(new { success = true, redeemedCoins = coinsToSpend, discountIQD, rewardTitle = rewardLabel, remainingCoins = bal - coinsToSpend });
    }

    [HttpPost("adjust")]
    public async Task<IActionResult> AdjustCoins([FromBody] LoyaltyAdjustDto req)
    {
        if (req.CustomerID <= 0 || req.DeltaCoins == 0)
            return BadRequest(new { success = false, message = "بيانات غير صالحة." });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var up = new MySqlCommand(@"
            INSERT INTO customerloyalty (CustomerID, CoinsBalance, TotalEarned, TotalRedeemed, UpdatedAt)
            VALUES (@cid, GREATEST(0, @d), GREATEST(0, @d), GREATEST(0, -@d), UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                CoinsBalance = GREATEST(0, CoinsBalance + @d),
                TotalEarned = TotalEarned + GREATEST(0, @d),
                TotalRedeemed = TotalRedeemed + GREATEST(0, -@d),
                UpdatedAt = UTC_TIMESTAMP();", conn, tx))
        {
            up.Parameters.AddWithValue("@cid", req.CustomerID);
            up.Parameters.AddWithValue("@d", req.DeltaCoins);
            await up.ExecuteNonQueryAsync();
        }
        await using (var log = new MySqlCommand(@"
            INSERT INTO loyaltytransactions (CustomerID, OrderID, DeltaCoins, Reason)
            VALUES (@cid, NULL, @d, @reason);", conn, tx))
        {
            log.Parameters.AddWithValue("@cid", req.CustomerID);
            log.Parameters.AddWithValue("@d", req.DeltaCoins);
            log.Parameters.AddWithValue("@reason", string.IsNullOrWhiteSpace(req.Reason) ? "تعديل من إدارة المطعم" : req.Reason.Trim());
            await log.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return Ok(new { success = true, message = "تم تحديث رصيد قامشلو كوينز." });
    }
}

public sealed class LoyaltySettingsDto
{
    public bool IsEnabled { get; set; } = true;
    public decimal EarnPerAmountIQD { get; set; } = 1000m;
    public int CoinsPerStep { get; set; } = 1;
    public decimal CoinValueIQD { get; set; } = 50m;
    public int MinCoinsToRedeem { get; set; } = 20;
    public int CoinsExpiryDays { get; set; } = 90;
    public string? ValidUntilDate { get; set; }
}

public sealed class LoyaltyRewardDto
{
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public int CoinsCost { get; set; } = 20;
    public decimal DiscountIQD { get; set; }
    public string? RewardType { get; set; } = "Discount";
}

public sealed class LoyaltyAwardDto
{
    public int CustomerID { get; set; }
    public int? OrderID { get; set; }
    public decimal OrderAmountIQD { get; set; }
}

public sealed class LoyaltyRedeemDto
{
    public int CustomerID { get; set; }
    public int? OrderID { get; set; }
    public int Coins { get; set; }
    public int? RewardID { get; set; }
}

public sealed class LoyaltyAdjustDto
{
    public int CustomerID { get; set; }
    public int DeltaCoins { get; set; }
    public string? Reason { get; set; }
}
