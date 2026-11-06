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
        if(customerId<=0)return BadRequest(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        const string sql="SELECT NotificationID,OrderID,TitleEN,TitleAR,TitleKU,BodyEN,BodyAR,BodyKU,IsRead,CreatedAt FROM CustomerNotifications WHERE CustomerID=@id ORDER BY CreatedAt DESC LIMIT 100";
        await using var cmd=new MySqlCommand(sql,c);cmd.Parameters.AddWithValue("@id",customerId);await using var r=await cmd.ExecuteReaderAsync();var list=new List<object>();
        while(await r.ReadAsync()) list.Add(new{notificationID=r.GetInt64("NotificationID"),orderID=r.IsDBNull(r.GetOrdinal("OrderID"))?(int?)null:r.GetInt32("OrderID"),titleEN=r.GetString("TitleEN"),titleAR=r.IsDBNull(r.GetOrdinal("TitleAR"))?null:r.GetString("TitleAR"),titleKU=r.IsDBNull(r.GetOrdinal("TitleKU"))?null:r.GetString("TitleKU"),bodyEN=r.GetString("BodyEN"),bodyAR=r.IsDBNull(r.GetOrdinal("BodyAR"))?null:r.GetString("BodyAR"),bodyKU=r.IsDBNull(r.GetOrdinal("BodyKU"))?null:r.GetString("BodyKU"),isRead=r.GetBoolean("IsRead"),createdAt=r.GetDateTime("CreatedAt")});
        return Ok(new{success=true,notifications=list});
    }
    [HttpPost("{id:long}/read")]
    public async Task<IActionResult> MarkRead(long id,[FromQuery]int customerId)
    {
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();await using var cmd=new MySqlCommand("UPDATE CustomerNotifications SET IsRead=1 WHERE NotificationID=@id AND CustomerID=@customer",c);cmd.Parameters.AddWithValue("@id",id);cmd.Parameters.AddWithValue("@customer",customerId);
        return await cmd.ExecuteNonQueryAsync()==0?NotFound(new{success=false}):Ok(new{success=true});
    }
    [HttpPost("broadcast")]
    public async Task<IActionResult> Broadcast([FromBody] BroadcastNotificationRequest x)
    {
        if(!await ManagerCredentialValidator.IsManagerAsync(Request))return Unauthorized(new{success=false});
        if(string.IsNullOrWhiteSpace(x.TitleEN)||string.IsNullOrWhiteSpace(x.BodyEN))return BadRequest(new{success=false});
        await using var c=DatabaseManager.GetConnection();await c.OpenAsync();
        const string sql="INSERT INTO CustomerNotifications(CustomerID,TitleEN,TitleAR,TitleKU,BodyEN,BodyAR,BodyKU,IsBroadcast) SELECT CustomerID,@te,@ta,@tk,@be,@ba,@bk,0 FROM Customers WHERE IsActive=1";
        await using var cmd=new MySqlCommand(sql,c);cmd.Parameters.AddWithValue("@te",x.TitleEN);cmd.Parameters.AddWithValue("@ta",(object?)x.TitleAR??DBNull.Value);cmd.Parameters.AddWithValue("@tk",(object?)x.TitleKU??DBNull.Value);cmd.Parameters.AddWithValue("@be",x.BodyEN);cmd.Parameters.AddWithValue("@ba",(object?)x.BodyAR??DBNull.Value);cmd.Parameters.AddWithValue("@bk",(object?)x.BodyKU??DBNull.Value);await cmd.ExecuteNonQueryAsync();return Ok(new{success=true});
    }
    public static async Task AddOrderStatusNotificationAsync(MySqlConnection c, MySqlTransaction tx, int customerId, int orderId, string status)
    {
        var (en,ar,ku)=status switch {"Preparing"=>("Your order was accepted.","تم قبول طلبك.","داواکارییەکەت پەسەند کرا."),"Ready"=>("Your order is ready at the restaurant.","طلبك أصبح جاهزاً في المطعم.","داواکارییەکەت لە چێشتخانە ئامادەیە."),"OutForDelivery"=>("Your order has left the restaurant and is on the way.","خرج طلبك من المطعم وهو في الطريق إليك.","داواکارییەکەت لە چێشتخانە دەرچووە و لە ڕێگادایە."),"Delivered"=>("Your order has been delivered.","تم توصيل طلبك.","داواکارییەکەت گەیەنرا."),_=> ("Your order status was updated.","تم تحديث حالة طلبك.","دۆخی داواکارییەکەت نوێکرایەوە.")};
        const string sql="INSERT INTO CustomerNotifications(CustomerID,OrderID,TitleEN,TitleAR,TitleKU,BodyEN,BodyAR,BodyKU) VALUES(@customer,@order,@te,@ta,@tk,@be,@ba,@bk)";
        await using var cmd=new MySqlCommand(sql,c,tx);cmd.Parameters.AddWithValue("@customer",customerId);cmd.Parameters.AddWithValue("@order",orderId);cmd.Parameters.AddWithValue("@te","Order status");cmd.Parameters.AddWithValue("@ta","حالة الطلب");cmd.Parameters.AddWithValue("@tk","دۆخی داواکاری");cmd.Parameters.AddWithValue("@be",en);cmd.Parameters.AddWithValue("@ba",ar);cmd.Parameters.AddWithValue("@bk",ku);await cmd.ExecuteNonQueryAsync();
    }
}
public sealed class BroadcastNotificationRequest { public string TitleEN{get;set;}="";public string? TitleAR{get;set;}public string? TitleKU{get;set;}public string BodyEN{get;set;}="";public string? BodyAR{get;set;}public string? BodyKU{get;set;} }
