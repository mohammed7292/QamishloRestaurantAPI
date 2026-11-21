using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/inventory")]
public sealed class InventoryController : ControllerBase
{
    public static async Task DeductStockForOrderAsync(MySqlConnection conn, int orderId)
    {
        try
        {
            await using var chk = new MySqlCommand("SELECT COUNT(*) FROM inventoryorderdeductions WHERE OrderID = @oid;", conn);
            chk.Parameters.AddWithValue("@oid", orderId);
            if (Convert.ToInt64(await chk.ExecuteScalarAsync()) > 0) return;

            var deductions = new List<(int ItemID, decimal Qty, decimal Cost)>();
            await using (var q = new MySqlCommand(@"
                SELECT pr.InventoryItemID,
                       SUM(od.Quantity * pr.QuantityRequired * (1 + (pr.WastePercent / 100.0))) AS NeedQty,
                       ii.AverageCost
                FROM orderdetails od
                INNER JOIN productrecipes pr ON pr.ProductID = od.ProductID
                INNER JOIN inventoryitems ii ON ii.InventoryItemID = pr.InventoryItemID
                WHERE od.OrderID = @oid
                GROUP BY pr.InventoryItemID, ii.AverageCost;", conn))
            {
                q.Parameters.AddWithValue("@oid", orderId);
                await using var r = await q.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    deductions.Add((r.GetInt32(0), Convert.ToDecimal(r.GetValue(1)), Convert.ToDecimal(r.GetValue(2))));
            }
            if (deductions.Count == 0) return;

            decimal totalCogs = 0m;
            await using var tx = await conn.BeginTransactionAsync();
            foreach (var d in deductions)
            {
                totalCogs += d.Qty * d.Cost;
                await using var up = new MySqlCommand("UPDATE inventoryitems SET CurrentQuantity = CurrentQuantity - @q, UpdatedAt = NOW() WHERE InventoryItemID = @id;", conn, tx);
                up.Parameters.AddWithValue("@q", d.Qty);
                up.Parameters.AddWithValue("@id", d.ItemID);
                await up.ExecuteNonQueryAsync();

                await using var mv = new MySqlCommand(@"
                    INSERT INTO stockmovements (InventoryItemID, MovementType, QuantityDelta, UnitCost, ReferenceType, ReferenceID, Notes, CreatedBy)
                    VALUES (@id, 'OrderDeduction', @dq, @cost, 'Order', @oid, @n, 'System');", conn, tx);
                mv.Parameters.AddWithValue("@id", d.ItemID);
                mv.Parameters.AddWithValue("@dq", -d.Qty);
                mv.Parameters.AddWithValue("@cost", d.Cost);
                mv.Parameters.AddWithValue("@oid", orderId);
                mv.Parameters.AddWithValue("@n", $"خصم وصفة طلب #{orderId}");
                await mv.ExecuteNonQueryAsync();
            }
            await using (var mark = new MySqlCommand("INSERT IGNORE INTO inventoryorderdeductions (OrderID, TotalCOGS, DeductedAt, IsReversed) VALUES (@oid, @cogs, NOW(), 0);", conn, tx))
            {
                mark.Parameters.AddWithValue("@oid", orderId);
                mark.Parameters.AddWithValue("@cogs", totalCogs);
                await mark.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        catch { }
    }

    [HttpGet("items")]
    public async Task<IActionResult> GetItems()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var items = new List<object>();
        decimal totalValuation = 0m;
        int lowCount = 0;

        await using var cmd = new MySqlCommand("SELECT InventoryItemID, Name, SKU, CategoryName, BaseUnit, PurchaseUnit, ConversionFactor, CurrentQuantity, MinimumQuantity, MaximumQuantity, AverageCost, ExpiryTracking, ExpiryDate, IsActive FROM inventoryitems WHERE IsActive = 1 ORDER BY Name ASC;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            decimal qty = r.GetDecimal("CurrentQuantity");
            decimal min = r.GetDecimal("MinimumQuantity");
            decimal cost = r.GetDecimal("AverageCost");
            bool isLow = qty <= min;
            if (isLow) lowCount++;
            totalValuation += Math.Max(0m, qty) * cost;
            items.Add(new
            {
                inventoryItemID = r.GetInt32("InventoryItemID"),
                name = r.GetString("Name"),
                sku = r.IsDBNull(r.GetOrdinal("SKU")) ? "" : r.GetString("SKU"),
                categoryName = r.IsDBNull(r.GetOrdinal("CategoryName")) ? "" : r.GetString("CategoryName"),
                baseUnit = r.GetString("BaseUnit"),
                purchaseUnit = r.GetString("PurchaseUnit"),
                conversionFactor = r.GetDecimal("ConversionFactor"),
                currentQuantity = qty,
                minimumQuantity = min,
                maximumQuantity = r.GetDecimal("MaximumQuantity"),
                averageCost = cost,
                stockValueIQD = Math.Round(Math.Max(0m, qty) * cost, 0),
                isLowStock = isLow,
                expiryDate = r.IsDBNull(r.GetOrdinal("ExpiryDate")) ? null : r.GetDateTime("ExpiryDate").ToString("yyyy-MM-dd")
            });
        }
        return Ok(new { success = true, totalItems = items.Count, lowStockCount = lowCount, totalValuationIQD = Math.Round(totalValuation, 0), items });
    }

    [HttpPost("items")]
    public async Task<IActionResult> AddItem([FromBody] InventoryItemDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { success = false, message = "اسم المادة الخام مطلوب." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(@"
            INSERT INTO inventoryitems (Name, SKU, CategoryName, BaseUnit, PurchaseUnit, ConversionFactor, CurrentQuantity, MinimumQuantity, MaximumQuantity, AverageCost, ExpiryTracking, ExpiryDate, IsActive, UpdatedAt)
            VALUES (@n, @sku, @cat, @bu, @pu, @cf, @qty, @min, @max, @cost, @et, @ed, 1, NOW());
            SELECT LAST_INSERT_ID();", conn);
        cmd.Parameters.AddWithValue("@n", req.Name.Trim());
        cmd.Parameters.AddWithValue("@sku", string.IsNullOrWhiteSpace(req.SKU) ? DBNull.Value : req.SKU.Trim());
        cmd.Parameters.AddWithValue("@cat", (object?)req.CategoryName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bu", string.IsNullOrWhiteSpace(req.BaseUnit) ? "Gram" : req.BaseUnit);
        cmd.Parameters.AddWithValue("@pu", string.IsNullOrWhiteSpace(req.PurchaseUnit) ? "Kilogram" : req.PurchaseUnit);
        cmd.Parameters.AddWithValue("@cf", req.ConversionFactor > 0 ? req.ConversionFactor : 1000m);
        cmd.Parameters.AddWithValue("@qty", req.CurrentQuantity);
        cmd.Parameters.AddWithValue("@min", req.MinimumQuantity);
        cmd.Parameters.AddWithValue("@max", req.MaximumQuantity);
        cmd.Parameters.AddWithValue("@cost", Math.Max(0m, req.AverageCost));
        cmd.Parameters.AddWithValue("@et", req.ExpiryTracking);
        cmd.Parameters.AddWithValue("@ed", DateTime.TryParse(req.ExpiryDate, out var ed) ? ed : DBNull.Value);
        int id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return Ok(new { success = true, inventoryItemID = id, message = "تمت إضافة المادة الخام للمخزون بنجاح." });
    }

    [HttpPost("adjust")]
    public async Task<IActionResult> AdjustStock([FromBody] StockAdjustDto req)
    {
        if (req.InventoryItemID <= 0 || req.QuantityDelta == 0) return BadRequest(new { success = false, message = "بيانات التعديل غير صالحة." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var up = new MySqlCommand("UPDATE inventoryitems SET CurrentQuantity = CurrentQuantity + @d, UpdatedAt = NOW() WHERE InventoryItemID = @id;", conn, tx))
        {
            up.Parameters.AddWithValue("@d", req.QuantityDelta);
            up.Parameters.AddWithValue("@id", req.InventoryItemID);
            await up.ExecuteNonQueryAsync();
        }
        await using (var mv = new MySqlCommand("INSERT INTO stockmovements (InventoryItemID, MovementType, QuantityDelta, UnitCost, Notes, CreatedBy) VALUES (@id, @mt, @d, @c, @n, @by);", conn, tx))
        {
            mv.Parameters.AddWithValue("@id", req.InventoryItemID);
            mv.Parameters.AddWithValue("@mt", string.IsNullOrWhiteSpace(req.MovementType) ? "Adjustment" : req.MovementType);
            mv.Parameters.AddWithValue("@d", req.QuantityDelta);
            mv.Parameters.AddWithValue("@c", Math.Max(0m, req.UnitCost));
            mv.Parameters.AddWithValue("@n", (object?)req.Notes ?? DBNull.Value);
            mv.Parameters.AddWithValue("@by", Request.Headers["X-Manager-Username"].FirstOrDefault() ?? "Manager");
            await mv.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
        return Ok(new { success = true, message = "تم تحديث رصيد المخزون وتسجيل الحركة بنجاح." });
    }

    [HttpGet("recipes/{productId:int}")]
    public async Task<IActionResult> GetProductRecipe(int productId)
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var ingredients = new List<object>();
        decimal totalCost = 0m;
        await using var cmd = new MySqlCommand(@"
            SELECT pr.RecipeID, pr.InventoryItemID, ii.Name, pr.QuantityRequired, pr.Unit, pr.WastePercent, ii.AverageCost
            FROM productrecipes pr
            INNER JOIN inventoryitems ii ON ii.InventoryItemID = pr.InventoryItemID
            WHERE pr.ProductID = @pid;", conn);
        cmd.Parameters.AddWithValue("@pid", productId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            decimal qty = r.GetDecimal("QuantityRequired");
            decimal waste = r.GetDecimal("WastePercent");
            decimal unitCost = r.GetDecimal("AverageCost");
            decimal ingCost = qty * (1m + waste / 100m) * unitCost;
            totalCost += ingCost;
            ingredients.Add(new
            {
                recipeID = r.GetInt32("RecipeID"),
                inventoryItemID = r.GetInt32("InventoryItemID"),
                itemName = r.GetString("Name"),
                quantityRequired = qty,
                unit = r.GetString("Unit"),
                wastePercent = waste,
                unitCost,
                ingredientCostIQD = Math.Round(ingCost, 2)
            });
        }
        return Ok(new { success = true, productID = productId, totalIngredientCostIQD = Math.Round(totalCost, 2), ingredients });
    }

    [HttpPost("recipes")]
    public async Task<IActionResult> UpsertRecipeIngredient([FromBody] RecipeIngredientDto req)
    {
        if (req.ProductID <= 0 || req.InventoryItemID <= 0 || req.QuantityRequired <= 0)
            return BadRequest(new { success = false, message = "اختر المنتج والمادة والكمية." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand(@"
            INSERT INTO productrecipes (ProductID, InventoryItemID, QuantityRequired, Unit, WastePercent)
            VALUES (@pid, @iid, @q, @u, @w)
            ON DUPLICATE KEY UPDATE QuantityRequired = VALUES(QuantityRequired), Unit = VALUES(Unit), WastePercent = VALUES(WastePercent);", conn);
        cmd.Parameters.AddWithValue("@pid", req.ProductID);
        cmd.Parameters.AddWithValue("@iid", req.InventoryItemID);
        cmd.Parameters.AddWithValue("@q", req.QuantityRequired);
        cmd.Parameters.AddWithValue("@u", string.IsNullOrWhiteSpace(req.Unit) ? "Gram" : req.Unit);
        cmd.Parameters.AddWithValue("@w", Math.Clamp(req.WastePercent, 0m, 95m));
        await cmd.ExecuteNonQueryAsync();
        return Ok(new { success = true, message = "تم ربط المكون بوصفة المنتج بنجاح." });
    }

    [HttpGet("suppliers")]
    public async Task<IActionResult> GetSuppliers()
    {
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        var list = new List<object>();
        await using var cmd = new MySqlCommand("SELECT SupplierID, Name, Phone, Address, BalanceIQD FROM suppliers WHERE IsActive = 1 ORDER BY Name;", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new { supplierID = r.GetInt32(0), name = r.GetString(1), phone = r.IsDBNull(2) ? "" : r.GetString(2), address = r.IsDBNull(3) ? "" : r.GetString(3), balanceIQD = r.GetDecimal(4) });
        return Ok(new { success = true, suppliers = list });
    }

    [HttpPost("suppliers")]
    public async Task<IActionResult> AddSupplier([FromBody] SupplierDto req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { success = false, message = "اسم المورد مطلوب." });
        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync();
        await using var cmd = new MySqlCommand("INSERT INTO suppliers (Name, Phone, Address, BalanceIQD, IsActive) VALUES (@n, @p, @a, 0, 1); SELECT LAST_INSERT_ID();", conn);
        cmd.Parameters.AddWithValue("@n", req.Name.Trim());
        cmd.Parameters.AddWithValue("@p", (object?)req.Phone ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@a", (object?)req.Address ?? DBNull.Value);
        int id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        return Ok(new { success = true, supplierID = id, message = "تمت إضافة المورد بنجاح." });
    }
}

public sealed class InventoryItemDto { public string Name { get; set; } = ""; public string? SKU { get; set; } public string? CategoryName { get; set; } public string BaseUnit { get; set; } = "Gram"; public string PurchaseUnit { get; set; } = "Kilogram"; public decimal ConversionFactor { get; set; } = 1000m; public decimal CurrentQuantity { get; set; } public decimal MinimumQuantity { get; set; } public decimal MaximumQuantity { get; set; } public decimal AverageCost { get; set; } public bool ExpiryTracking { get; set; } public string? ExpiryDate { get; set; } }
public sealed class StockAdjustDto { public int InventoryItemID { get; set; } public string MovementType { get; set; } = "Adjustment"; public decimal QuantityDelta { get; set; } public decimal UnitCost { get; set; } public string? Notes { get; set; } }
public sealed class RecipeIngredientDto { public int ProductID { get; set; } public int InventoryItemID { get; set; } public decimal QuantityRequired { get; set; } public string Unit { get; set; } = "Gram"; public decimal WastePercent { get; set; } }
public sealed class SupplierDto { public string Name { get; set; } = ""; public string? Phone { get; set; } public string? Address { get; set; } }
