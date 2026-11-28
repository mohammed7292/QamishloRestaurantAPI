using System.Text;
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/assistant")]
public sealed class ManagerAssistantController : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetMobileMiniDashboard()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();

        decimal todaySales = 0, todayExpenses = 0, weekSales = 0, prevWeekSales = 0;
        int todayOrders = 0, activeOrders = 0, unavailableProducts = 0, activeDrivers = 0, delayedOrders = 0;

        await using (var cmd = new MySqlCommand(@"
            SELECT COUNT(*), COALESCE(SUM(TotalAmount), 0)
            FROM orders
            WHERE Status NOT IN ('Cancelled','Rejected') AND DATE(OrderDate) = CURDATE();", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            if (await r.ReadAsync()) { todayOrders = Convert.ToInt32(r.GetValue(0)); todaySales = Convert.ToDecimal(r.GetValue(1)); }
        }

        try
        {
            await using var eCmd = new MySqlCommand("SELECT COALESCE(SUM(Amount), 0) FROM expenses WHERE DATE(ExpenseDate) = CURDATE();", conn);
            var eo = await eCmd.ExecuteScalarAsync();
            if (eo != null && eo != DBNull.Value) todayExpenses = Convert.ToDecimal(eo);
        }
        catch { }

        await using (var wCmd = new MySqlCommand(@"
            SELECT
                COALESCE(SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY) THEN TotalAmount ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 14 DAY) AND OrderDate < DATE_SUB(NOW(), INTERVAL 7 DAY) THEN TotalAmount ELSE 0 END), 0),
                SUM(CASE WHEN Status IN ('New','Preparing','Ready','OutForDelivery') AND IsArchived = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN Status IN ('Preparing','Ready','OutForDelivery') AND IsArchived = 0 AND TIMESTAMPDIFF(MINUTE, OrderDate, NOW()) >= 35 THEN 1 ELSE 0 END)
            FROM orders
            WHERE Status NOT IN ('Cancelled','Rejected');", conn))
        await using (var r = await wCmd.ExecuteReaderAsync())
        {
            if (await r.ReadAsync())
            {
                weekSales = Convert.ToDecimal(r.GetValue(0));
                prevWeekSales = Convert.ToDecimal(r.GetValue(1));
                activeOrders = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                delayedOrders = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
            }
        }

        try
        {
            await using var pCmd = new MySqlCommand("SELECT COUNT(*) FROM products WHERE ProductName NOT LIKE '__DELETED__%' AND IsAvailable = 0;", conn);
            unavailableProducts = Convert.ToInt32(await pCmd.ExecuteScalarAsync());
        }
        catch { }

        try
        {
            await using var dCmd = new MySqlCommand("SELECT COUNT(*) FROM deliveryworkers WHERE IsActive = 1 AND IsOnShift = 1;", conn);
            activeDrivers = Convert.ToInt32(await dCmd.ExecuteScalarAsync());
        }
        catch { }

        return Ok(new
        {
            success = true,
            todaySales,
            todayExpenses,
            todayNetProfit = todaySales - todayExpenses,
            todayOrders,
            activeOrders,
            delayedOrders,
            unavailableProducts,
            activeDrivers,
            weekSales,
            prevWeekSales
        });
    }


    [HttpGet("anomalies")]
    public async Task<IActionResult> DetectAnomalies()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var alerts = new List<object>();

        // 1. High cancellation count in last 24h
        await using (var c1 = new MySqlCommand("SELECT COUNT(*) FROM orders WHERE Status IN ('Cancelled','Rejected') AND OrderDate >= DATE_SUB(NOW(), INTERVAL 24 HOUR);", conn))
        {
            int canc = Convert.ToInt32(await c1.ExecuteScalarAsync() ?? 0);
            if (canc >= 3) alerts.Add(new { severity = "high", category = "Cancellations", message = $"⚠️ تم إلغاء/رفض {canc} طلبات خلال آخر 24 ساعة — يرجى مراجعة الأسباب." });
        }
        // 2. Cash session discrepancies
        try
        {
            await using var c2 = new MySqlCommand("SELECT CashSessionID, CashDifference, ClosedBy FROM cashsessions WHERE Status='Closed' AND ABS(COALESCE(CashDifference,0)) >= 5000 ORDER BY CashSessionID DESC LIMIT 5;", conn);
            await using var r2 = await c2.ExecuteReaderAsync();
            while (await r2.ReadAsync())
                alerts.Add(new { severity = "high", category = "CashDifference", message = $"💵 فرق نقدي في إغلاق الصندوق #{r2.GetInt64(0)} بمقدار {r2.GetDecimal(1):N0} د.ع (بواسطة {r2.GetString(2)})." });
        }
        catch { }
        // 3. Low stock raw materials
        try
        {
            await using var c3 = new MySqlCommand("SELECT Name, CurrentQuantity, BaseUnit FROM inventoryitems WHERE IsActive=1 AND CurrentQuantity <= MinimumQuantity LIMIT 10;", conn);
            await using var r3 = await c3.ExecuteReaderAsync();
            while (await r3.ReadAsync())
                alerts.Add(new { severity = "medium", category = "LowStock", message = $"📦 انخفاض مخزون «{r3.GetString(0)}»: المتبقي {r3.GetDecimal(1):0.##} {r3.GetString(2)}." });
        }
        catch { }

        return Ok(new { success = true, readOnly = true, anomalyCount = alerts.Count, anomalies = alerts });
    }

    [HttpPost("ask")]
    public async Task<IActionResult> AskAssistant([FromBody] AssistantQuestionRequest req)
    {
        string q = (req.Question ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { success = false, message = "اكتب سؤالك أولاً." });

        bool isKurdish = req.Lang == "ku" || q.Contains("چەند") || q.Contains("ئەمڕۆ") || q.Contains("فرۆشتن") || q.Contains("هەفتە") || q.Contains("بکڕم") || q.Contains("دواکەوتوو");
        string ql = q.ToLowerInvariant();

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var sb = new StringBuilder();

        // 1. Today's Profit (كم ربحت اليوم / ئەمڕۆ چەندم قازانج کرد)
        if (ql.Contains("ربح") || ql.Contains("اليوم") || ql.Contains("قازانج") || ql.Contains("ئەمڕۆ"))
        {
            decimal sales = 0, exp = 0, disc = 0; int cnt = 0;
            await using (var cmd = new MySqlCommand("SELECT COUNT(*), COALESCE(SUM(TotalAmount),0), COALESCE(SUM(DiscountAmount),0) FROM orders WHERE Status NOT IN ('Cancelled','Rejected') AND DATE(OrderDate)=CURDATE();", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync()) { cnt = Convert.ToInt32(r.GetValue(0)); sales = Convert.ToDecimal(r.GetValue(1)); disc = Convert.ToDecimal(r.GetValue(2)); }

            try
            {
                await using var ec = new MySqlCommand("SELECT COALESCE(SUM(Amount),0) FROM expenses WHERE DATE(ExpenseDate)=CURDATE();", conn);
                exp = Convert.ToDecimal(await ec.ExecuteScalarAsync() ?? 0m);
            }
            catch { }

            decimal net = sales - exp;
            if (isKurdish)
            {
                sb.AppendLine($"📊 پۆختەی قازانجی ئەمڕۆ ({DateTime.Now:yyyy-MM-dd}):");
                sb.AppendLine($"• کۆی فرۆشتن: {sales:N0} د.ع ({cnt} داواکاری)");
                sb.AppendLine($"• کۆی خەرجییەکان: {exp:N0} د.ع");
                sb.AppendLine($"• داشکاندنەکان: {disc:N0} د.ع");
                sb.AppendLine($"• ✅ قازانجی سافی ئەمڕۆ: {net:N0} د.ع");
            }
            else
            {
                sb.AppendLine($"📊 ملخص أرباح اليوم الفعلي ({DateTime.Now:yyyy-MM-dd}):");
                sb.AppendLine($"• إجمالي المبيعات: {sales:N0} د.ع ({cnt} طلب)");
                sb.AppendLine($"• مصاريف اليوم: {exp:N0} د.ع");
                sb.AppendLine($"• الخصومات الممنوحة: {disc:N0} د.ع");
                sb.AppendLine($"• ✅ صافي ربح اليوم: {net:N0} د.ع");
            }
        }
        // 2. Best-selling dish this week (أكثر وجبة مبيعاً هذا الأسبوع / پڕفرۆشترین خواردن)
        else if (ql.Contains("أكثر") || ql.Contains("اكثر") || ql.Contains("مبيع") || ql.Contains("وجبة") || ql.Contains("پڕفرۆش"))
        {
            var items = new List<(string Name, int Qty, decimal Rev)>();
            await using (var cmd = new MySqlCommand(@"
                SELECT p.ProductName, SUM(od.Quantity) AS Qty, SUM(od.Quantity * od.UnitPrice) AS Rev
                FROM orderdetails od
                INNER JOIN orders o ON o.OrderID = od.OrderID
                INNER JOIN products p ON p.ProductID = od.ProductID
                WHERE o.Status NOT IN ('Cancelled','Rejected') AND o.OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY)
                GROUP BY p.ProductID, p.ProductName
                ORDER BY Qty DESC LIMIT 5;", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) items.Add((r.GetString(0), Convert.ToInt32(r.GetValue(1)), Convert.ToDecimal(r.GetValue(2))));

            sb.AppendLine(isKurdish ? "🔥 پڕفرۆشترین ژەمەکان لەم هەفتەیەدا (7 ڕۆژی ڕابردوو):" : "🔥 أكثر الوجبات مبيعاً خلال آخر 7 أيام:");
            if (items.Count == 0) sb.AppendLine(isKurdish ? "هیچ داواکارییەک لەم هەفتەیەدا تۆمار نەکراوە." : "لا توجد مبيعات مسجلة خلال آخر 7 أيام.");
            else foreach (var it in items) sb.AppendLine($"• {it.Name}: {it.Qty} قطعة — {it.Rev:N0} د.ع");
        }
        // 3. Why did sales drop? (لماذا انخفضت المبيعات / بۆچی فرۆشتن دابەزی)
        else if (ql.Contains("انخفض") || ql.Contains("نقص") || ql.Contains("لماذا") || ql.Contains("بۆچی") || ql.Contains("دابەز"))
        {
            decimal thisW = 0, lastW = 0; int thisCnt = 0, lastCnt = 0, cancelled = 0;
            await using (var cmd = new MySqlCommand(@"
                SELECT
                    COALESCE(SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY) AND Status NOT IN ('Cancelled','Rejected') THEN TotalAmount ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 14 DAY) AND OrderDate < DATE_SUB(NOW(), INTERVAL 7 DAY) AND Status NOT IN ('Cancelled','Rejected') THEN TotalAmount ELSE 0 END), 0),
                    SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY) AND Status NOT IN ('Cancelled','Rejected') THEN 1 ELSE 0 END),
                    SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 14 DAY) AND OrderDate < DATE_SUB(NOW(), INTERVAL 7 DAY) AND Status NOT IN ('Cancelled','Rejected') THEN 1 ELSE 0 END),
                    SUM(CASE WHEN OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY) AND Status IN ('Cancelled','Rejected') THEN 1 ELSE 0 END)
                FROM orders;", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync())
                {
                    thisW = Convert.ToDecimal(r.GetValue(0)); lastW = Convert.ToDecimal(r.GetValue(1));
                    thisCnt = r.IsDBNull(2) ? 0 : Convert.ToInt32(r.GetValue(2));
                    lastCnt = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3));
                    cancelled = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4));
                }

            decimal diffPct = lastW > 0 ? Math.Round(((thisW - lastW) / lastW) * 100m, 1) : 0;
            sb.AppendLine(isKurdish ? "📉 شیکاری گۆڕانکاری فرۆشتن (ئەم هەفتەیە بەرامبەر هەفتەی پێشوو):" : "📉 تحليل مقارنة المبيعات (هذا الأسبوع مقابل الأسبوع الماضي):");
            sb.AppendLine($"• مبيعات آخر 7 أيام: {thisW:N0} د.ع ({thisCnt} طلب)");
            sb.AppendLine($"• مبيعات الأسبوع السابق: {lastW:N0} د.ع ({lastCnt} طلب)");
            sb.AppendLine($"• نسبة التغيّر: {(diffPct >= 0 ? "+" : "")}{diffPct}%");
            sb.AppendLine($"• الطلبات الملغاة/المرفوضة هذا الأسبوع: {cancelled}");
            if (diffPct < 0) sb.AppendLine("💡 التوصية: أرسل عرضاً فورياً من قسم «التسويق الذكي» للزبائن المتوقفين عن الطلب وراجع أسباب الإلغاء.");
            else sb.AppendLine("✅ أداؤك هذا الأسبوع أفضل أو مستقر مقارنة بالأسبوع السابق!");
        }
        // 4. What to buy tomorrow? (ما الأصناف التي يجب أن أشتريها غداً / چی بکڕم بۆ سبەی)
        else if (ql.Contains("شراء") || ql.Contains("أشتري") || ql.Contains("اشتري") || ql.Contains("مخزون") || ql.Contains("بکڕم") || ql.Contains("سبەی"))
        {
            var fastMovers = new List<(string Name, int DailyAvg)>();
            await using (var cmd = new MySqlCommand(@"
                SELECT p.ProductName, CEIL(SUM(od.Quantity) / 7) AS DailyNeed
                FROM orderdetails od
                INNER JOIN orders o ON o.OrderID = od.OrderID
                INNER JOIN products p ON p.ProductID = od.ProductID
                WHERE o.Status NOT IN ('Cancelled','Rejected') AND o.OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY)
                GROUP BY p.ProductID, p.ProductName
                ORDER BY SUM(od.Quantity) DESC LIMIT 6;", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync()) fastMovers.Add((r.GetString(0), Convert.ToInt32(r.GetValue(1))));

            var outOfStock = new List<string>();
            await using (var oc = new MySqlCommand("SELECT ProductName FROM products WHERE ProductName NOT LIKE '__DELETED__%' AND IsAvailable = 0 LIMIT 10;", conn))
            await using (var or = await oc.ExecuteReaderAsync())
                while (await or.ReadAsync()) outOfStock.Add(or.GetString(0));

            sb.AppendLine(isKurdish ? "🛒 پێشنیاری کڕین و ئامادەکاری بۆ سبەی (لەسەر بنەمای فرۆشتنی 7 ڕۆژ):" : "🛒 توصية المشتريات والتجهيز ليوم غد (بناءً على وتيرة السحب الفعلية):");
            if (outOfStock.Count > 0) sb.AppendLine("⚠️ أصناف متوقفة حالياً (تحتاج تجهيز عاجل): " + string.Join("، ", outOfStock));
            foreach (var fm in fastMovers) sb.AppendLine($"• جهّز مواد لـ «{fm.Name}» بمعدل ~{Math.Max(5, fm.DailyAvg)} وجبة/يوم.");
        }
        // 5. Cost vs Sales ratio (تكلفة المواد مقارنة بالمبيعات / تێچوو بەرامبەر فرۆشتن)
        else if (ql.Contains("تكلفة") || ql.Contains("مصاريف") || ql.Contains("نسبة") || ql.Contains("تێچوو") || ql.Contains("خەرجی"))
        {
            decimal s30 = 0, e30 = 0;
            await using (var c1 = new MySqlCommand("SELECT COALESCE(SUM(TotalAmount),0) FROM orders WHERE Status NOT IN ('Cancelled','Rejected') AND OrderDate >= DATE_SUB(NOW(), INTERVAL 30 DAY);", conn))
                s30 = Convert.ToDecimal(await c1.ExecuteScalarAsync() ?? 0m);
            try
            {
                await using var c2 = new MySqlCommand("SELECT COALESCE(SUM(Amount),0) FROM expenses WHERE ExpenseDate >= DATE_SUB(NOW(), INTERVAL 30 DAY);", conn);
                e30 = Convert.ToDecimal(await c2.ExecuteScalarAsync() ?? 0m);
            }
            catch { }
            decimal ratio = s30 > 0 ? Math.Round((e30 / s30) * 100m, 1) : 0;
            sb.AppendLine(isKurdish ? "💰 ڕێژەی تێچوو بەرامبەر فرۆشتن (30 ڕۆژی ڕابردوو):" : "💰 نسبة تكلفة المواد والمصاريف إلى المبيعات (آخر 30 يوم):");
            sb.AppendLine($"• إجمالي المبيعات: {s30:N0} د.ع");
            sb.AppendLine($"• إجمالي المصاريف والمواد: {e30:N0} د.ع");
            sb.AppendLine($"• نسبة التكلفة: {ratio}% | هامش الربح: {Math.Max(0, 100 - ratio)}%");
        }
        // 6. Forecast next week sales (توقع المبيعات للأسبوع القادم / پێشبینی فرۆشتنی هەفتەی داهاتوو)
        else if (ql.Contains("توقع") || ql.Contains("القادم") || ql.Contains("پێشبینی") || ql.Contains("داهاتوو"))
        {
            decimal s28 = 0; int cnt28 = 0;
            await using (var cmd = new MySqlCommand("SELECT COUNT(*), COALESCE(SUM(TotalAmount),0) FROM orders WHERE Status NOT IN ('Cancelled','Rejected') AND OrderDate >= DATE_SUB(NOW(), INTERVAL 28 DAY);", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync()) { cnt28 = Convert.ToInt32(r.GetValue(0)); s28 = Convert.ToDecimal(r.GetValue(1)); }

            decimal forecastSales = Math.Round(s28 / 4m, 0);
            int forecastOrders = (int)Math.Ceiling(cnt28 / 4.0);
            sb.AppendLine(isKurdish ? "🔮 پێشبینی فرۆشتن بۆ هەفتەی داهاتوو (لەسەر بنەمای 4 هەفتەی ڕابردوو):" : "🔮 توقع مبيعات الأسبوع القادم (بناءً على متوسط آخر 4 أسابيع فعلية):");
            sb.AppendLine($"• المبيعات المتوقعة: ~{forecastSales:N0} د.ع");
            sb.AppendLine($"• عدد الطلبات المتوقع: ~{forecastOrders} طلب (~{Math.Ceiling(forecastOrders / 7.0)} طلب يومياً)");
        }
        // 7. Delayed delivery drivers (عامل توصيل لديه طلبات متأخرة / کارمەندی گەیاندنی دواکەوتوو)
        else if (ql.Contains("توصيل") || ql.Contains("متأخر") || ql.Contains("عامل") || ql.Contains("دواکەوتوو") || ql.Contains("گەیاندن"))
        {
            var delayed = new List<string>();
            await using (var cmd = new MySqlCommand(@"
                SELECT o.OrderID, COALESCE(dw.FullName, 'غير معيّن') AS WorkerName,
                       TIMESTAMPDIFF(MINUTE, o.OrderDate, NOW()) AS Mins, o.Status
                FROM orders o
                LEFT JOIN deliveryworkers dw ON dw.DeliveryWorkerID = o.DeliveryWorkerID
                WHERE o.IsArchived = 0 AND o.Status IN ('Preparing','Ready','OutForDelivery')
                  AND TIMESTAMPDIFF(MINUTE, o.OrderDate, NOW()) >= 30
                ORDER BY Mins DESC LIMIT 10;", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                while (await r.ReadAsync())
                    delayed.Add($"• طلب #{r.GetInt32(0)} — العامل: {r.GetString(1)} — متأخر {Convert.ToInt32(r.GetValue(2))} دقيقة ({r.GetString(3)})");

            if (delayed.Count == 0)
                sb.AppendLine(isKurdish ? "✅ لە ئێستادا هیچ کارمەندێکی گەیاندن داواکاری دواکەوتووی نییە!" : "✅ ممتاز! لا يوجد حالياً أي عامل توصيل لديه طلبات متأخرة (أكثر من 30 دقيقة).");
            else
            {
                sb.AppendLine(isKurdish ? $"⚠️ {delayed.Count} داواکاری دواکەوتوو هەیە:" : $"⚠️ تنبيه — يوجد {delayed.Count} طلب متأخر حالياً:");
                foreach (var d in delayed) sb.AppendLine(d);
            }
        }
        else
        {
            // General live executive snapshot
            decimal tSales = 0, wSales = 0; int tCnt = 0;
            await using (var cmd = new MySqlCommand("SELECT SUM(CASE WHEN DATE(OrderDate)=CURDATE() THEN TotalAmount ELSE 0 END), SUM(CASE WHEN DATE(OrderDate)=CURDATE() THEN 1 ELSE 0 END), COALESCE(SUM(TotalAmount),0) FROM orders WHERE Status NOT IN ('Cancelled','Rejected') AND OrderDate >= DATE_SUB(NOW(), INTERVAL 7 DAY);", conn))
            await using (var r = await cmd.ExecuteReaderAsync())
                if (await r.ReadAsync())
                {
                    tSales = r.IsDBNull(0) ? 0 : Convert.ToDecimal(r.GetValue(0));
                    tCnt = r.IsDBNull(1) ? 0 : Convert.ToInt32(r.GetValue(1));
                    wSales = r.IsDBNull(2) ? 0 : Convert.ToDecimal(r.GetValue(2));
                }
            sb.AppendLine("🤖 ملخص سريع من بيانات مطعم قامشلو الآن:");
            sb.AppendLine($"• مبيعات اليوم: {tSales:N0} د.ع ({tCnt} طلب)");
            sb.AppendLine($"• مبيعات آخر 7 أيام: {wSales:N0} د.ع");
            sb.AppendLine();
            sb.AppendLine("يمكنك سؤالي بالعربية أو الكردية عن: أرباح اليوم، أكثر وجبة مبيعاً، سبب انخفاض المبيعات، المشتريات المقترحة لغد، نسبة التكلفة، توقع الأسبوع القادم، أو الطلبات المتأخرة.");
        }

        return Ok(new { success = true, answer = sb.ToString().Trim() });
    }
}

public sealed class AssistantQuestionRequest
{
    public string? Question { get; set; }
    public string? Lang { get; set; }
}
