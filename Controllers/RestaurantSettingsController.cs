using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class restaurantsettingsController : ControllerBase
{
    private static volatile bool _v16ColsEnsured = false;
    private static readonly object _settingsCacheLock = new();
    private static object? _cachedPublicSettings;
    private static object? _cachedManagerSettings;
    private static DateTime _settingsCacheExpiresUtc = DateTime.MinValue;

    private static async Task EnsureV16ColumnsAsync(MySqlConnection c)
    {
        if (_v16ColsEnsured) return;
        string[] cols =
        {
            "SmtpEmail|VARCHAR(190) NULL",
            "SmtpAppPassword|VARCHAR(190) NULL",
            "CustomerWebUrl|VARCHAR(500) NULL",
            "CustomerWebQrBase64|LONGTEXT NULL",
            "ClosureReasonKey|VARCHAR(40) NULL",
            "FirstOrderMinOrderAmount|DECIMAL(10,2) NOT NULL DEFAULT 0",
            "FirstOrderMaxOrderAmount|DECIMAL(10,2) NOT NULL DEFAULT 0",
            "FirstOrderMaxDiscountAmount|DECIMAL(10,2) NOT NULL DEFAULT 0",
            "FirstOrderAllowedOrderTypes|VARCHAR(20) NOT NULL DEFAULT 'All'"
        };
        foreach (var entry in cols)
        {
            var parts = entry.Split('|');
            try
            {
                await using var chk = new MySqlCommand("SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='restaurantsettings' AND COLUMN_NAME=@col;", c);
                chk.Parameters.AddWithValue("@col", parts[0]);
                if (Convert.ToInt32(await chk.ExecuteScalarAsync()) == 0)
                {
                    await using var alt = new MySqlCommand($"ALTER TABLE restaurantsettings ADD COLUMN {parts[0]} {parts[1]};", c);
                    await alt.ExecuteNonQueryAsync();
                }
            }
            catch { }
        }
        _v16ColsEnsured = true;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        bool isMgr = await ManagerCredentialValidator.IsManagerAsync(Request);
        lock (_settingsCacheLock)
        {
            if (DateTime.UtcNow < _settingsCacheExpiresUtc)
            {
                var cached = isMgr ? _cachedManagerSettings : _cachedPublicSettings;
                if (cached != null) return Ok(cached);
            }
        }
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await EnsureV16ColumnsAsync(c);
        const string sql = "SELECT SettingID,RestaurantName,Phone,Address,CurrencyCode,IsDeliveryEnabled,IsRestaurantOpen,IsCustomerAppEnabled,OpeningTime,ClosingTime,MinimumOrderAmount,RestaurantLatitude,RestaurantLongitude,FirstOrderDiscountEnabled,FirstOrderDiscountPercent,FirstOrderMinOrderAmount,FirstOrderMaxOrderAmount,FirstOrderMaxDiscountAmount,FirstOrderAllowedOrderTypes,InvoiceHeader AS ReceiptHeader,InvoiceFooter AS ReceiptFooter,ReceiptShowLogo,DeliveryClosedMessage,PrinterConnectionType,PrinterIPAddress,PrinterPort,PrinterBluetoothAddress,SmtpEmail,SmtpAppPassword,CustomerWebUrl,CustomerWebQrBase64,ClosureReasonKey FROM restaurantsettings ORDER BY SettingID LIMIT 1";
        await using var cmd = new MySqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return NotFound(new { success = false });
        object? V(string key) => r.IsDBNull(r.GetOrdinal(key)) ? null : r.GetValue(r.GetOrdinal(key));
        string? smtpEmail = V("SmtpEmail")?.ToString();
        string? smtpPass = V("SmtpAppPassword")?.ToString();
        bool hasSmtp = !string.IsNullOrWhiteSpace(smtpEmail) && !string.IsNullOrWhiteSpace(smtpPass);
        var payload = new
        {
            success = true,
            settingID = r.GetInt32("SettingID"),
            restaurantName = r.GetString("RestaurantName"),
            phone = V("Phone"),
            address = V("Address"),
            currencyCode = r.GetString("CurrencyCode"),
            isDeliveryEnabled = r.GetBoolean("IsDeliveryEnabled"),
            isRestaurantOpen = r.GetBoolean("IsRestaurantOpen"),
            isCustomerAppEnabled = r.GetBoolean("IsCustomerAppEnabled"),
            openingTime = V("OpeningTime")?.ToString(),
            closingTime = V("ClosingTime")?.ToString(),
            minimumOrderAmount = r.GetDecimal("MinimumOrderAmount"),
            restaurantLatitude = V("RestaurantLatitude"),
            restaurantLongitude = V("RestaurantLongitude"),
            firstOrderDiscountEnabled = r.GetBoolean("FirstOrderDiscountEnabled"),
            firstOrderDiscountPercent = r.GetDecimal("FirstOrderDiscountPercent"),
            firstOrderMinOrderAmount = V("FirstOrderMinOrderAmount") != null ? Convert.ToDecimal(V("FirstOrderMinOrderAmount")) : 0m,
            firstOrderMaxOrderAmount = V("FirstOrderMaxOrderAmount") != null ? Convert.ToDecimal(V("FirstOrderMaxOrderAmount")) : 0m,
            firstOrderMaxDiscountAmount = V("FirstOrderMaxDiscountAmount") != null ? Convert.ToDecimal(V("FirstOrderMaxDiscountAmount")) : 0m,
            firstOrderAllowedOrderTypes = V("FirstOrderAllowedOrderTypes")?.ToString() ?? "All",
            receiptHeader = V("ReceiptHeader"),
            receiptFooter = V("ReceiptFooter"),
            receiptShowLogo = r.GetBoolean("ReceiptShowLogo"),
            deliveryClosedMessage = V("DeliveryClosedMessage"),
            printerConnectionType = r.GetString("PrinterConnectionType"),
            printerIPAddress = V("PrinterIPAddress"),
            printerPort = r.GetInt32("PrinterPort"),
            printerBluetoothAddress = V("PrinterBluetoothAddress"),
            smtpEmail = isMgr ? smtpEmail : null,
            smtpConfigured = hasSmtp,
            customerWebUrl = V("CustomerWebUrl"),
            customerWebQrBase64 = V("CustomerWebQrBase64"),
            closureReasonKey = V("ClosureReasonKey"),
            businessDayStartsAt = "02:00"
        };
        lock (_settingsCacheLock)
        {
            if (isMgr) _cachedManagerSettings = payload;
            else _cachedPublicSettings = payload;
            _settingsCacheExpiresUtc = DateTime.UtcNow.AddSeconds(30);
        }
        return Ok(payload);
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] restaurantsettingsRequest x)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });
        if (x.MinimumOrderAmount < 0 || x.FirstOrderDiscountPercent < 0 || x.FirstOrderDiscountPercent > 100 || x.FirstOrderMinOrderAmount < 0 || x.FirstOrderMaxOrderAmount < 0 || x.FirstOrderMaxDiscountAmount < 0 || (x.FirstOrderMaxOrderAmount > 0 && x.FirstOrderMaxOrderAmount < x.FirstOrderMinOrderAmount))
            return BadRequest(new { success = false, message = "Invalid amounts or discount percent." });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        await EnsureV16ColumnsAsync(c);
        const string sql = @"UPDATE restaurantsettings
            SET RestaurantName=@name,Phone=@phone,Address=@address,IsDeliveryEnabled=@delivery,IsRestaurantOpen=@open,
                IsCustomerAppEnabled=@app,OpeningTime=@opening,ClosingTime=@closing,MinimumOrderAmount=@minimum,
                RestaurantLatitude=@lat,RestaurantLongitude=@lng,FirstOrderDiscountEnabled=@discountEnabled,
                FirstOrderDiscountPercent=@discount,FirstOrderMinOrderAmount=@discMinOrder,FirstOrderMaxOrderAmount=@discMaxOrder,FirstOrderMaxDiscountAmount=@discMaxAmount,FirstOrderAllowedOrderTypes=@discOrderTypes,InvoiceHeader=@header,InvoiceFooter=@footer,ReceiptShowLogo=@logo,
                DeliveryClosedMessage=@closed,PrinterConnectionType=@printerType,PrinterIPAddress=@printerIP,
                PrinterPort=@printerPort,PrinterBluetoothAddress=@printerBluetooth,
                SmtpEmail=COALESCE(@smtpEmail, SmtpEmail),
                SmtpAppPassword=CASE WHEN @smtpPass IS NOT NULL AND @smtpPass <> '' THEN @smtpPass ELSE SmtpAppPassword END,
                CustomerWebUrl=COALESCE(@cwUrl, CustomerWebUrl),
                CustomerWebQrBase64=COALESCE(@cwQr, CustomerWebQrBase64),
                ClosureReasonKey=COALESCE(@closureReason, ClosureReasonKey)
            WHERE SettingID=(SELECT SettingID FROM (SELECT SettingID FROM restaurantsettings ORDER BY SettingID LIMIT 1) s)";
        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@name", x.RestaurantName);
        cmd.Parameters.AddWithValue("@phone", (object?)x.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@address", (object?)x.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@delivery", x.IsDeliveryEnabled);
        cmd.Parameters.AddWithValue("@open", x.IsRestaurantOpen);
        cmd.Parameters.AddWithValue("@app", x.IsCustomerAppEnabled);
        cmd.Parameters.AddWithValue("@opening", (object?)x.OpeningTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@closing", (object?)x.ClosingTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@minimum", x.MinimumOrderAmount);
        cmd.Parameters.AddWithValue("@lat", (object?)x.RestaurantLatitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lng", (object?)x.RestaurantLongitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@discountEnabled", x.FirstOrderDiscountEnabled);
        cmd.Parameters.AddWithValue("@discount", x.FirstOrderDiscountPercent);
        cmd.Parameters.AddWithValue("@discMinOrder", x.FirstOrderMinOrderAmount);
        cmd.Parameters.AddWithValue("@discMaxOrder", x.FirstOrderMaxOrderAmount);
        cmd.Parameters.AddWithValue("@discMaxAmount", x.FirstOrderMaxDiscountAmount);
        string safeAllowedTypes = (x.FirstOrderAllowedOrderTypes ?? "All").Trim() switch
        {
            "Delivery" => "Delivery",
            "Takeaway" => "Takeaway",
            _ => "All"
        };
        cmd.Parameters.AddWithValue("@discOrderTypes", safeAllowedTypes);
        cmd.Parameters.AddWithValue("@header", (object?)x.ReceiptHeader ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@footer", (object?)x.ReceiptFooter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@logo", x.ReceiptShowLogo);
        cmd.Parameters.AddWithValue("@closed", (object?)x.DeliveryClosedMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@printerType", x.PrinterConnectionType);
        cmd.Parameters.AddWithValue("@printerIP", (object?)x.PrinterIPAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@printerPort", x.PrinterPort);
        cmd.Parameters.AddWithValue("@printerBluetooth", (object?)x.PrinterBluetoothAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@smtpEmail", string.IsNullOrWhiteSpace(x.SmtpEmail) ? DBNull.Value : x.SmtpEmail.Trim());
        cmd.Parameters.AddWithValue("@smtpPass", string.IsNullOrWhiteSpace(x.SmtpAppPassword) ? DBNull.Value : x.SmtpAppPassword.Trim().Replace(" ", ""));
        cmd.Parameters.AddWithValue("@cwUrl", string.IsNullOrWhiteSpace(x.CustomerWebUrl) ? DBNull.Value : x.CustomerWebUrl.Trim());
        cmd.Parameters.AddWithValue("@cwQr", string.IsNullOrWhiteSpace(x.CustomerWebQrBase64) ? DBNull.Value : x.CustomerWebQrBase64.Trim());
        cmd.Parameters.AddWithValue("@closureReason", string.IsNullOrWhiteSpace(x.ClosureReasonKey) ? DBNull.Value : x.ClosureReasonKey.Trim());
        int affected = await cmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            await using var check = new MySqlCommand("SELECT COUNT(*) FROM restaurantsettings", c);
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
                return NotFound(new { success = false, message = "Restaurant settings row was not found." });
        }
        lock (_settingsCacheLock)
        {
            _cachedPublicSettings = null;
            _cachedManagerSettings = null;
            _settingsCacheExpiresUtc = DateTime.MinValue;
        }
        return Ok(new { success = true, unchanged = affected == 0 });
    }

    [HttpPost("monthly-reset-now")]
    public async Task<IActionResult> RunMonthlyResetNow()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "Manager login required." });
        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        string ym = ExpensesController.BusinessDate(ExpensesController.BaghdadNow()).ToString("yyyy-MM");
        int cleared = await QamishloRestaurantAPI.Services.MonthlyResetBackgroundService.ExecuteResetAsync(c, ym);
        return Ok(new { success = true, month = ym, clearedTables = cleared, message = "تم التصفير الشهري الشامل بنجاح مع الحفاظ على الزبائن والعمال والمنتجات والتصنيفات." });
    }
}

public sealed class restaurantsettingsRequest
{
    public string RestaurantName { get; set; } = "Qamishlo Fast Food";
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool IsDeliveryEnabled { get; set; } = true;
    public bool IsRestaurantOpen { get; set; } = true;
    public bool IsCustomerAppEnabled { get; set; } = true;
    public string? OpeningTime { get; set; }
    public string? ClosingTime { get; set; }
    public decimal MinimumOrderAmount { get; set; }
    public decimal? RestaurantLatitude { get; set; }
    public decimal? RestaurantLongitude { get; set; }
    public bool FirstOrderDiscountEnabled { get; set; } = true;
    public decimal FirstOrderDiscountPercent { get; set; } = 50;
    public decimal FirstOrderMinOrderAmount { get; set; } = 0;
    public decimal FirstOrderMaxOrderAmount { get; set; } = 0;
    public decimal FirstOrderMaxDiscountAmount { get; set; } = 0;
    public string FirstOrderAllowedOrderTypes { get; set; } = "All";
    public string? ReceiptHeader { get; set; }
    public string? ReceiptFooter { get; set; }
    public bool ReceiptShowLogo { get; set; } = true;
    public string? DeliveryClosedMessage { get; set; }
    public string PrinterConnectionType { get; set; } = "Share";
    public string? PrinterIPAddress { get; set; }
    public int PrinterPort { get; set; } = 9100;
    public string? PrinterBluetoothAddress { get; set; }
    public string? SmtpEmail { get; set; }
    public string? SmtpAppPassword { get; set; }
    public string? CustomerWebUrl { get; set; }
    public string? CustomerWebQrBase64 { get; set; }
    public string? ClosureReasonKey { get; set; }
}
