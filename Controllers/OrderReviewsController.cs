using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/order-reviews")]
public sealed class OrderReviewsController : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SubmitReview([FromBody] OrderReviewRequest req)
    {
        if (req.OrderID <= 0 || req.FoodRating < 1 || req.FoodRating > 5 || req.DeliveryRating < 1 || req.DeliveryRating > 5)
            return BadRequest(new { success = false, message = "يرجى اختيار تقييم من 1 إلى 5 للطلب وسرعة التوصيل." });

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        const string sql = @"
            INSERT INTO orderreviews (OrderID, CustomerID, CustomerName, FoodRating, DeliveryRating, Comment, CreatedAt)
            VALUES (@oid, @cid, @cname, @fr, @dr, @comment, UTC_TIMESTAMP())
            ON DUPLICATE KEY UPDATE
                FoodRating = VALUES(FoodRating),
                DeliveryRating = VALUES(DeliveryRating),
                Comment = VALUES(Comment),
                CreatedAt = UTC_TIMESTAMP();";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@oid", req.OrderID);
        cmd.Parameters.AddWithValue("@cid", (object?)req.CustomerID ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cname", (object?)req.CustomerName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fr", req.FoodRating);
        cmd.Parameters.AddWithValue("@dr", req.DeliveryRating);
        cmd.Parameters.AddWithValue("@comment", string.IsNullOrWhiteSpace(req.Comment) ? DBNull.Value : req.Comment.Trim());
        await cmd.ExecuteNonQueryAsync();

        return Ok(new { success = true, message = "شكراً لتقييمك! تم حفظ تقييم الطلب وسرعة التوصيل بنجاح." });
    }

    [HttpGet("order/{orderId:int}")]
    public async Task<IActionResult> GetOrderReview(int orderId)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("SELECT ReviewID, OrderID, FoodRating, DeliveryRating, Comment, CreatedAt FROM orderreviews WHERE OrderID = @oid LIMIT 1;", conn);
        cmd.Parameters.AddWithValue("@oid", orderId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return Ok(new { success = true, rated = false });

        return Ok(new
        {
            success = true,
            rated = true,
            reviewID = r.GetInt64("ReviewID"),
            orderID = r.GetInt32("OrderID"),
            foodRating = r.GetInt32("FoodRating"),
            deliveryRating = r.GetInt32("DeliveryRating"),
            comment = r.IsDBNull(r.GetOrdinal("Comment")) ? "" : r.GetString("Comment"),
            createdAt = r.GetDateTime("CreatedAt")
        });
    }

    [HttpGet]
    public async Task<IActionResult> ListReviews()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var reviews = new List<object>();
        double sumFood = 0, sumDel = 0;

        await using var cmd = new MySqlCommand(@"
            SELECT ReviewID, OrderID, CustomerID, CustomerName, FoodRating, DeliveryRating, Comment, CreatedAt
            FROM orderreviews
            ORDER BY ReviewID DESC
            LIMIT 100;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            int fr = r.GetInt32("FoodRating");
            int dr = r.GetInt32("DeliveryRating");
            sumFood += fr;
            sumDel += dr;
            reviews.Add(new
            {
                reviewID = r.GetInt64("ReviewID"),
                orderID = r.GetInt32("OrderID"),
                customerName = r.IsDBNull(r.GetOrdinal("CustomerName")) ? "زبون" : r.GetString("CustomerName"),
                foodRating = fr,
                deliveryRating = dr,
                comment = r.IsDBNull(r.GetOrdinal("Comment")) ? "" : r.GetString("Comment"),
                createdAt = r.GetDateTime("CreatedAt")
            });
        }

        int cnt = reviews.Count;
        return Ok(new
        {
            success = true,
            totalReviews = cnt,
            avgFoodRating = cnt > 0 ? Math.Round(sumFood / cnt, 2) : 0,
            avgDeliveryRating = cnt > 0 ? Math.Round(sumDel / cnt, 2) : 0,
            reviews
        });
    }
}

public sealed class OrderReviewRequest
{
    public int OrderID { get; set; }
    public int? CustomerID { get; set; }
    public string? CustomerName { get; set; }
    public int FoodRating { get; set; }
    public int DeliveryRating { get; set; }
    public string? Comment { get; set; }
}
