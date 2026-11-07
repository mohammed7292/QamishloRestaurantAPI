using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class restaurantsettingsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        const string sql="SELECT SettingID,RestaurantName,Phone,Address,CurrencyCode,IsDeliveryEnabled,IsRestaurantOpen,IsCustomerAppEnabled,OpeningTime,ClosingTime,MinimumOrderAmount,RestaurantLatitude,RestaurantLongitude,FirstOrderDiscountEnabled,FirstOrderDiscountPercent,InvoiceHeader AS ReceiptHeader,InvoiceFooter AS ReceiptFooter,ReceiptShowLogo,DeliveryClosedMessage,PrinterConnectionType,PrinterIPAddress,PrinterPort,PrinterBluetoothAddress FROM restaurantsettings ORDER BY SettingID LIMIT 1";
        await using var cmd=new MySqlCommand(sql,c);await using var r=await cmd.ExecuteReaderAsync();if(!await r.ReadAsync())return NotFound(new{success=false});
        object? V(string key)=>r.IsDBNull(r.GetOrdinal(key))?null:r.GetValue(r.GetOrdinal(key));
        return Ok(new{success=true,settingID=r.GetInt32("SettingID"),restaurantName=r.GetString("RestaurantName"),phone=V("Phone"),address=V("Address"),currencyCode=r.GetString("CurrencyCode"),isDeliveryEnabled=r.GetBoolean("IsDeliveryEnabled"),isRestaurantOpen=r.GetBoolean("IsRestaurantOpen"),isCustomerAppEnabled=r.GetBoolean("IsCustomerAppEnabled"),openingTime=V("OpeningTime")?.ToString(),closingTime=V("ClosingTime")?.ToString(),minimumOrderAmount=r.GetDecimal("MinimumOrderAmount"),restaurantLatitude=V("RestaurantLatitude"),restaurantLongitude=V("RestaurantLongitude"),firstOrderDiscountEnabled=r.GetBoolean("FirstOrderDiscountEnabled"),firstOrderDiscountPercent=r.GetDecimal("FirstOrderDiscountPercent"),receiptHeader=V("ReceiptHeader"),receiptFooter=V("ReceiptFooter"),receiptShowLogo=r.GetBoolean("ReceiptShowLogo"),deliveryClosedMessage=V("DeliveryClosedMessage"),printerConnectionType=r.GetString("PrinterConnectionType"),printerIPAddress=V("PrinterIPAddress"),printerPort=r.GetInt32("PrinterPort"),printerBluetoothAddress=V("PrinterBluetoothAddress"),businessDayStartsAt="02:00"});
    }

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] restaurantsettingsRequest x)
    {
        if(!await ManagerCredentialValidator.IsManagerAsync(Request))return Unauthorized(new{success=false,message="Manager login required."});
        if(x.MinimumOrderAmount<0||x.FirstOrderDiscountPercent<0||x.FirstOrderDiscountPercent>100)return BadRequest(new{success=false,message="Invalid amounts or discount percent."});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        const string sql="UPDATE restaurantsettings SET RestaurantName=@name,Phone=@phone,Address=@address,IsDeliveryEnabled=@delivery,IsRestaurantOpen=@open,IsCustomerAppEnabled=@app,OpeningTime=@opening,ClosingTime=@closing,MinimumOrderAmount=@minimum,RestaurantLatitude=@lat,RestaurantLongitude=@lng,FirstOrderDiscountEnabled=@discountEnabled,FirstOrderDiscountPercent=@discount,InvoiceHeader=@header,InvoiceFooter=@footer,ReceiptShowLogo=@logo,DeliveryClosedMessage=@closed,PrinterConnectionType=@printerType,PrinterIPAddress=@printerIP,PrinterPort=@printerPort,PrinterBluetoothAddress=@printerBluetooth WHERE SettingID=(SELECT SettingID FROM (SELECT SettingID FROM restaurantsettings ORDER BY SettingID LIMIT 1) s)";
        await using var cmd=new MySqlCommand(sql,c);
        cmd.Parameters.AddWithValue("@name",x.RestaurantName);cmd.Parameters.AddWithValue("@phone",(object?)x.Phone??DBNull.Value);cmd.Parameters.AddWithValue("@address",(object?)x.Address??DBNull.Value);cmd.Parameters.AddWithValue("@delivery",x.IsDeliveryEnabled);cmd.Parameters.AddWithValue("@open",x.IsRestaurantOpen);cmd.Parameters.AddWithValue("@app",x.IsCustomerAppEnabled);cmd.Parameters.AddWithValue("@opening",(object?)x.OpeningTime??DBNull.Value);cmd.Parameters.AddWithValue("@closing",(object?)x.ClosingTime??DBNull.Value);cmd.Parameters.AddWithValue("@minimum",x.MinimumOrderAmount);cmd.Parameters.AddWithValue("@lat",(object?)x.RestaurantLatitude??DBNull.Value);cmd.Parameters.AddWithValue("@lng",(object?)x.RestaurantLongitude??DBNull.Value);cmd.Parameters.AddWithValue("@discountEnabled",x.FirstOrderDiscountEnabled);cmd.Parameters.AddWithValue("@discount",x.FirstOrderDiscountPercent);cmd.Parameters.AddWithValue("@header",(object?)x.ReceiptHeader??DBNull.Value);cmd.Parameters.AddWithValue("@footer",(object?)x.ReceiptFooter??DBNull.Value);cmd.Parameters.AddWithValue("@logo",x.ReceiptShowLogo);cmd.Parameters.AddWithValue("@closed",(object?)x.DeliveryClosedMessage??DBNull.Value);cmd.Parameters.AddWithValue("@printerType",x.PrinterConnectionType);cmd.Parameters.AddWithValue("@printerIP",(object?)x.PrinterIPAddress??DBNull.Value);cmd.Parameters.AddWithValue("@printerPort",x.PrinterPort);cmd.Parameters.AddWithValue("@printerBluetooth",(object?)x.PrinterBluetoothAddress??DBNull.Value);
        int affected = await cmd.ExecuteNonQueryAsync();
        if (affected == 0)
        {
            await using var check = new MySqlCommand("SELECT COUNT(*) FROM restaurantsettings", c);
            if (Convert.ToInt32(await check.ExecuteScalarAsync()) == 0)
                return NotFound(new { success = false, message = "Restaurant settings row was not found." });
        }
        return Ok(new { success = true, unchanged = affected == 0 });
    }
}
public sealed class restaurantsettingsRequest { public string RestaurantName{get;set;}="Qamishlo Fast Food";public string? Phone{get;set;}public string? Address{get;set;}public bool IsDeliveryEnabled{get;set;}=true;public bool IsRestaurantOpen{get;set;}=true;public bool IsCustomerAppEnabled{get;set;}=true;public string? OpeningTime{get;set;}public string? ClosingTime{get;set;}public decimal MinimumOrderAmount{get;set;}public decimal? RestaurantLatitude{get;set;}public decimal? RestaurantLongitude{get;set;}public bool FirstOrderDiscountEnabled{get;set;}=true;public decimal FirstOrderDiscountPercent{get;set;}=50;public string? ReceiptHeader{get;set;}public string? ReceiptFooter{get;set;}public bool ReceiptShowLogo{get;set;}=true;public string? DeliveryClosedMessage{get;set;}public string PrinterConnectionType{get;set;}="Share";public string? PrinterIPAddress{get;set;}public int PrinterPort{get;set;}=9100;public string? PrinterBluetoothAddress{get;set;} }
