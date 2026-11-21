using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class productsController : ControllerBase
    {
        // GET: api/products
        // IsAvailable is returned as the effective availability. A product whose
        // temporary unavailability period has expired becomes available automatically.
        [HttpGet]
        public async Task<IActionResult> GetProducts()
        {
            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    SELECT p.*,
                           CASE
                               WHEN p.AvailableUntil IS NOT NULL
                                    AND p.AvailableUntil <= UTC_TIMESTAMP() THEN 1
                               ELSE p.IsAvailable
                           END AS IsAvailable
                    FROM products p
                    WHERE p.ProductName NOT LIKE '__DELETED__%'
                    ORDER BY p.ProductID DESC;
                    """;

                await using var command = new MySqlCommand(query, connection);
                await using var reader = await command.ExecuteReaderAsync();
                var products = new List<Dictionary<string, object?>>();

                while (await reader.ReadAsync())
                {
                    var product = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.GetValue(i);
                        product[reader.GetName(i)] = value == DBNull.Value ? null : value;
                    }
                    products.Add(product);
                }

                // Attach active product add-ons (V8)
                try
                {
                    var addonsByProduct = new Dictionary<int, List<object>>();
                    await using var aCmd = new MySqlCommand("SELECT AddonID, ProductID, AddonName, ExtraPrice FROM productaddons WHERE IsActive = 1 ORDER BY AddonID ASC;", connection);
                    await using var ar = await aCmd.ExecuteReaderAsync();
                    while (await ar.ReadAsync())
                    {
                        int pid = ar.GetInt32("ProductID");
                        if (!addonsByProduct.TryGetValue(pid, out var list))
                        {
                            list = new List<object>();
                            addonsByProduct[pid] = list;
                        }
                        list.Add(new
                        {
                            addonID = ar.GetInt32("AddonID"),
                            productID = pid,
                            addonName = ar.GetString("AddonName"),
                            extraPrice = ar.GetDecimal("ExtraPrice")
                        });
                    }
                    foreach (var p in products)
                    {
                        int pid = p.TryGetValue("ProductID", out var v) && v != null ? Convert.ToInt32(v) : 0;
                        var list = addonsByProduct.TryGetValue(pid, out var l) ? l : new List<object>();
                        p["addons"] = list;
                        p["hasAddons"] = list.Count > 0;
                    }
                }
                catch { }

                return Ok(products);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "تعذر تحميل المنتجات.",
                    error = "حدث خطأ داخلي أثناء الاتصال بقاعدة البيانات."
                });
            }
        }

        // POST: api/products
        [HttpPost]
        public async Task<IActionResult> AddProduct([FromBody] ProductRequest product)
        {
            if (!IsValid(product, out string validationMessage))
                return BadRequest(new { success = false, message = validationMessage });

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    INSERT INTO products
                        (CategoryID, ProductName, Price, Description, ImageURL, IsAvailable, AvailableUntil)
                    VALUES
                        (@CategoryID, @ProductName, @Price, @Description, @ImageURL, @IsAvailable, @AvailableUntil);
                    SELECT LAST_INSERT_ID();
                    """;

                await using var command = new MySqlCommand(query, connection);
                AddProductParameters(command, product);
                int newId = Convert.ToInt32(await command.ExecuteScalarAsync());

                return Ok(new
                {
                    success = true,
                    message = "Product added successfully.",
                    ProductID = newId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "تعذرت إضافة المنتج.",
                    error = "حدث خطأ داخلي أثناء حفظ المنتج."
                });
            }
        }

        // PUT: api/products/5
        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateProduct(int id, [FromBody] ProductRequest product)
        {
            if (id <= 0)
                return BadRequest(new { success = false, message = "Invalid product ID." });
            if (!IsValid(product, out string validationMessage))
                return BadRequest(new { success = false, message = validationMessage });

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    UPDATE products
                    SET CategoryID = @CategoryID,
                        ProductName = @ProductName,
                        Price = @Price,
                        Description = @Description,
                        ImageURL = CASE WHEN @ImageURL IS NULL THEN ImageURL ELSE @ImageURL END,
                        IsAvailable = @IsAvailable,
                        AvailableUntil = @AvailableUntil
                    WHERE ProductID = @ProductID;
                    """;

                await using var command = new MySqlCommand(query, connection);
                AddProductParameters(command, product);
                command.Parameters.AddWithValue("@ProductID", id);
                int affectedRows = await command.ExecuteNonQueryAsync();

                if (affectedRows == 0)
                {
                    await using var exists = new MySqlCommand(
                        "SELECT COUNT(*) FROM products WHERE ProductID=@ProductID", connection);
                    exists.Parameters.AddWithValue("@ProductID", id);
                    if (Convert.ToInt32(await exists.ExecuteScalarAsync()) == 0)
                        return NotFound(new { success = false, message = "Product not found." });
                }

                return Ok(new { success = true, message = "Product updated successfully." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "تعذر تحديث المنتج.",
                    error = "حدث خطأ داخلي أثناء حفظ التعديلات."
                });
            }
        }

        // DELETE: api/products/5
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            if (id <= 0)
                return BadRequest(new { success = false, message = "Invalid product ID." });

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();
                const string query = "DELETE FROM products WHERE ProductID = @ProductID;";
                await using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@ProductID", id);
                int affectedRows = await command.ExecuteNonQueryAsync();

                if (affectedRows == 0)
                    return NotFound(new { success = false, message = "Product not found." });

                return Ok(new { success = true, message = "Product deleted successfully." });
            }
            catch (MySqlException ex) when (ex.Number == 1451)
            {
                try
                {
                    await using var archiveConn = DatabaseManager.GetConnection();
                    await archiveConn.OpenAsync();
                    const string softDeleteSql = @"
                        UPDATE products
                        SET IsAvailable = 0,
                            ProductName = CONCAT('__DELETED__', ProductID, '_', LEFT(ProductName, 80))
                        WHERE ProductID = @ProductID;";
                    await using var softCmd = new MySqlCommand(softDeleteSql, archiveConn);
                    softCmd.Parameters.AddWithValue("@ProductID", id);
                    await softCmd.ExecuteNonQueryAsync();
                    return Ok(new
                    {
                        success = true,
                        archived = true,
                        message = "تم حذف المنتج من القائمة بنجاح مع الاحتفاظ بسجل الطلبات السابقة."
                    });
                }
                catch
                {
                    return Conflict(new
                    {
                        success = false,
                        message = "لا يمكن حذف هذا المنتج لأنه مرتبط بطلبات سابقة."
                    });
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "تعذر حذف المنتج.",
                    error = "حدث خطأ داخلي أثناء حذف المنتج."
                });
            }
        }

        private static bool IsValid(ProductRequest product, out string message)
        {
            if (product is null || product.CategoryID <= 0 ||
                string.IsNullOrWhiteSpace(product.ProductName) || product.Price <= 0)
            {
                message = "بيانات المنتج غير صحيحة.";
                return false;
            }

            if (!product.IsAvailable && product.AvailableUntil.HasValue &&
                product.AvailableUntil.Value.ToUniversalTime() <= DateTime.UtcNow)
            {
                message = "وقت انتهاء عدم التوفر يجب أن يكون في المستقبل.";
                return false;
            }

            message = string.Empty;
            return true;
        }

        private static void AddProductParameters(MySqlCommand command, ProductRequest product)
        {
            command.Parameters.AddWithValue("@CategoryID", product.CategoryID);
            command.Parameters.AddWithValue("@ProductName", product.ProductName.Trim());
            command.Parameters.AddWithValue("@Price", product.Price);
            command.Parameters.AddWithValue("@Description", (object?)product.Description ?? DBNull.Value);
            command.Parameters.AddWithValue("@ImageURL", (object?)product.ImageURL ?? DBNull.Value);
            command.Parameters.AddWithValue("@IsAvailable", product.IsAvailable);
            // A manually re-enabled product clears any old expiry timestamp.
            command.Parameters.AddWithValue("@AvailableUntil",
                product.IsAvailable ? DBNull.Value : (object?)product.AvailableUntil ?? DBNull.Value);
        }
    
        // GET: api/products/{id}/addons
        [HttpGet("{id:int}/addons")]
        public async Task<IActionResult> GetProductAddons(int id)
        {
            await using var conn = DatabaseManager.GetConnection();
            await conn.OpenAsync();
            var list = new List<object>();
            await using var cmd = new MySqlCommand("SELECT AddonID, ProductID, AddonName, ExtraPrice, IsActive FROM productaddons WHERE ProductID = @pid AND IsActive = 1 ORDER BY AddonID ASC;", conn);
            cmd.Parameters.AddWithValue("@pid", id);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new
                {
                    addonID = r.GetInt32("AddonID"),
                    productID = r.GetInt32("ProductID"),
                    addonName = r.GetString("AddonName"),
                    extraPrice = r.GetDecimal("ExtraPrice")
                });
            }
            return Ok(new { success = true, addons = list });
        }

        // POST: api/products/{id}/addons
        [HttpPost("{id:int}/addons")]
        public async Task<IActionResult> AddProductAddon(int id, [FromBody] ProductAddonRequest req)
        {
            if (id <= 0 || string.IsNullOrWhiteSpace(req.AddonName))
                return BadRequest(new { success = false, message = "اسم الإضافة مطلوب." });
            await using var conn = DatabaseManager.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("INSERT INTO productaddons (ProductID, AddonName, ExtraPrice, IsActive) VALUES (@pid, @name, @price, 1); SELECT LAST_INSERT_ID();", conn);
            cmd.Parameters.AddWithValue("@pid", id);
            cmd.Parameters.AddWithValue("@name", req.AddonName.Trim());
            cmd.Parameters.AddWithValue("@price", Math.Max(0m, req.ExtraPrice));
            int newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return Ok(new { success = true, addonID = newId, addonName = req.AddonName.Trim(), extraPrice = Math.Max(0m, req.ExtraPrice) });
        }

        // DELETE: api/products/addons/{addonId}
        [HttpDelete("addons/{addonId:int}")]
        public async Task<IActionResult> DeleteProductAddon(int addonId)
        {
            await using var conn = DatabaseManager.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("DELETE FROM productaddons WHERE AddonID = @aid;", conn);
            cmd.Parameters.AddWithValue("@aid", addonId);
            await cmd.ExecuteNonQueryAsync();
            return Ok(new { success = true });
        }

    }

    public sealed class ProductAddonRequest { public string AddonName { get; set; } = ""; public decimal ExtraPrice { get; set; } }

    public class ProductRequest
    {
        public int CategoryID { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public string? Description { get; set; }
        public string? ImageURL { get; set; }
        public bool IsAvailable { get; set; } = true;
        public DateTime? AvailableUntil { get; set; }
    }
}
