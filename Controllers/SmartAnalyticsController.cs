using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/reports/smart-analytics")]
public sealed class SmartAnalyticsController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetSmartAnalytics([FromQuery] int days = 30)
    {
        days = Math.Clamp(days, 1, 365);
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();

        decimal grossSales = 0, totalDiscounts = 0, totalExpenses = 0;
        int ordersCount = 0;

        await using (var cmd = new MySqlCommand(@"
            SELECT COUNT(*) AS Cnt,
                   COALESCE(SUM(TotalAmount), 0) AS Sales,
                   COALESCE(SUM(DiscountAmount), 0) AS Discounts
            FROM orders
            WHERE Status NOT IN ('Cancelled', 'Rejected')
              AND OrderDate >= DATE_SUB(NOW(), INTERVAL @d DAY);", conn))
        {
            cmd.Parameters.AddWithValue("@d", days);
            await using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                ordersCount = r.GetInt32("Cnt");
                grossSales = r.GetDecimal("Sales");
                totalDiscounts = r.GetDecimal("Discounts");
            }
        }

        try
        {
            await using var expCmd = new MySqlCommand(@"
                SELECT COALESCE(SUM(Amount), 0)
                FROM expenses
                WHERE ExpenseDate >= DATE_SUB(NOW(), INTERVAL @d DAY);", conn);
            expCmd.Parameters.AddWithValue("@d", days);
            var obj = await expCmd.ExecuteScalarAsync();
            if (obj != null && obj != DBNull.Value) totalExpenses = Convert.ToDecimal(obj);
        }
        catch { }

        decimal netProfit = grossSales - totalExpenses;
        decimal avgOrder = ordersCount > 0 ? Math.Round(grossSales / ordersCount, 0) : 0;

        var topProducts = new List<dynamic>();
        try
        {
            await using var prodCmd = new MySqlCommand(@"
                SELECT p.ProductName,
                       SUM(od.Quantity) AS Qty,
                       SUM(od.Quantity * od.UnitPrice) AS Revenue
                FROM orderdetails od
                INNER JOIN orders o ON o.OrderID = od.OrderID
                INNER JOIN products p ON p.ProductID = od.ProductID
                WHERE o.Status NOT IN ('Cancelled', 'Rejected')
                  AND o.OrderDate >= DATE_SUB(NOW(), INTERVAL @d DAY)
                GROUP BY p.ProductID, p.ProductName
                ORDER BY Qty DESC
                LIMIT 5;", conn);
            prodCmd.Parameters.AddWithValue("@d", days);
            await using var pr = await prodCmd.ExecuteReaderAsync();
            while (await pr.ReadAsync())
            {
                topProducts.Add(new
                {
                    productName = pr.GetString("ProductName"),
                    quantity = Convert.ToInt32(pr.GetValue(1)),
                    revenue = Convert.ToDecimal(pr.GetValue(2))
                });
            }
        }
        catch { }

        var peakHours = new List<dynamic>();
        try
        {
            await using var hrCmd = new MySqlCommand(@"
                SELECT HOUR(OrderDate) AS Hr, COUNT(*) AS Cnt
                FROM orders
                WHERE Status NOT IN ('Cancelled', 'Rejected')
                  AND OrderDate >= DATE_SUB(NOW(), INTERVAL @d DAY)
                GROUP BY HOUR(OrderDate)
                ORDER BY Cnt DESC
                LIMIT 4;", conn);
            hrCmd.Parameters.AddWithValue("@d", days);
            await using var hr = await hrCmd.ExecuteReaderAsync();
            while (await hr.ReadAsync())
            {
                int h = Convert.ToInt32(hr.GetValue(0));
                peakHours.Add(new { hour = h, label = $"{h:00}:00", ordersCount = Convert.ToInt32(hr.GetValue(1)) });
            }
        }
        catch { }

        var drivers = new List<dynamic>();
        try
        {
            await using var drCmd = new MySqlCommand(@"
                SELECT dw.FullName, COUNT(o.OrderID) AS DeliveredCount, COALESCE(SUM(o.TotalAmount), 0) AS DeliveredTotal
                FROM orders o
                INNER JOIN deliveryworkers dw ON dw.DeliveryWorkerID = o.DeliveryWorkerID
                WHERE o.Status = 'Delivered'
                  AND o.OrderDate >= DATE_SUB(NOW(), INTERVAL @d DAY)
                GROUP BY dw.DeliveryWorkerID, dw.FullName
                ORDER BY DeliveredCount DESC
                LIMIT 5;", conn);
            drCmd.Parameters.AddWithValue("@d", days);
            await using var dr = await drCmd.ExecuteReaderAsync();
            while (await dr.ReadAsync())
            {
                drivers.Add(new
                {
                    workerName = dr.GetString("FullName"),
                    deliveredCount = Convert.ToInt32(dr.GetValue(1)),
                    deliveredTotal = Convert.ToDecimal(dr.GetValue(2))
                });
            }
        }
        catch { }

        double avgFood = 0, avgDelivery = 0;
        int totalReviews = 0;
        try
        {
            await using var revCmd = new MySqlCommand(@"
                SELECT COUNT(*), COALESCE(AVG(FoodRating), 0), COALESCE(AVG(DeliveryRating), 0)
                FROM orderreviews
                WHERE CreatedAt >= DATE_SUB(UTC_TIMESTAMP(), INTERVAL @d DAY);", conn);
            revCmd.Parameters.AddWithValue("@d", days);
            await using var rr = await revCmd.ExecuteReaderAsync();
            if (await rr.ReadAsync())
            {
                totalReviews = Convert.ToInt32(rr.GetValue(0));
                avgFood = Math.Round(Convert.ToDouble(rr.GetValue(1)), 2);
                avgDelivery = Math.Round(Convert.ToDouble(rr.GetValue(2)), 2);
            }
        }
        catch { }

        var sb = new StringBuilder();
        sb.AppendLine($"📊 *لوحة الأرباح والتحليلات الذكية (آخر {days} يوم)*");
        sb.AppendLine($"• إجمالي المبيعات: {grossSales:N0} د.ع ({ordersCount} طلب)");
        sb.AppendLine($"• إجمالي المصاريف: {totalExpenses:N0} د.ع");
        sb.AppendLine($"• إجمالي الخصومات: {totalDiscounts:N0} د.ع");
        sb.AppendLine($"• ✅ صافي الربح: {netProfit:N0} د.ع");
        sb.AppendLine($"• متوسط قيمة الطلب: {avgOrder:N0} د.ع");
        if (totalReviews > 0)
            sb.AppendLine($"• ⭐ تقييم الطلبات ({totalReviews}): الأكل {avgFood}/5 | سرعة التوصيل {avgDelivery}/5");
        if (topProducts.Count > 0)
        {
            sb.AppendLine("🔥 *الأكثر مبيعاً:*");
            foreach (var p in topProducts)
                sb.AppendLine($"  - {p.productName}: {p.quantity} قطعة ({p.revenue:N0} د.ع)");
        }
        if (peakHours.Count > 0)
        {
            sb.AppendLine("⏰ *أوقات الذروة:* " + string.Join(" ، ", peakHours.Select(x => $"{x.label} ({x.ordersCount})")));
        }
        if (drivers.Count > 0)
        {
            sb.AppendLine("🛵 *أداء عمال التوصيل:*");
            foreach (var d in drivers)
                sb.AppendLine($"  - {d.workerName}: {d.deliveredCount} طلب ({d.deliveredTotal:N0} د.ع)");
        }

        return Ok(new
        {
            success = true,
            days,
            grossSales,
            totalExpenses,
            totalDiscounts,
            netProfit,
            ordersCount,
            avgOrderValue = avgOrder,
            avgFoodRating = avgFood,
            avgDeliveryRating = avgDelivery,
            totalReviews,
            topProducts,
            peakHours,
            driverPerformance = drivers,
            whatsAppSummary = sb.ToString()
        });
    }
}
