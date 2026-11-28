
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class categoriesController : ControllerBase
    {
        private static readonly object _catCacheLock = new();
        private static List<Dictionary<string, object?>>? _cachedCategories;
        private static DateTime _catCacheExpiresUtc = DateTime.MinValue;
        public static void InvalidateCache() { lock (_catCacheLock) { _cachedCategories = null; _catCacheExpiresUtc = DateTime.MinValue; } }

        // GET: api/categories
        [HttpGet]
        public async Task<IActionResult> GetCategories()
        {
            lock (_catCacheLock)
            {
                if (_cachedCategories != null && DateTime.UtcNow < _catCacheExpiresUtc)
                    return Ok(_cachedCategories);
            }
            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    SELECT CategoryID, CategoryName, Description, IsActive
                    FROM categories
                    WHERE IFNULL(IsActive, 1) = 1
                    ORDER BY COALESCE(SortOrder, 999999) ASC, CategoryID ASC;
                    """;

                await using var command = new MySqlCommand(query, connection);
                await using var reader = await command.ExecuteReaderAsync();

                var categories = new List<Dictionary<string, object?>>();

                while (await reader.ReadAsync())
                {
                    var category = new Dictionary<string, object?>();

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.GetValue(i);
                        category[reader.GetName(i)] =
                            value == DBNull.Value ? null : value;
                    }

                    categories.Add(category);
                }

                lock (_catCacheLock)
                {
                    _cachedCategories = categories;
                    _catCacheExpiresUtc = DateTime.UtcNow.AddSeconds(60);
                }
                return Ok(categories);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Database error",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }


        [HttpPut("reorder")]
        public async Task<IActionResult> ReorderCategories([FromBody] ReorderIdsRequest req)
        {
            if (req?.OrderedIds == null || req.OrderedIds.Count == 0)
                return BadRequest(new { success = false, message = "OrderedIds is required." });
            await using var conn = DatabaseManager.GetConnection();
            await conn.OpenAsync();
            for (int i = 0; i < req.OrderedIds.Count; i++)
            {
                await using var cmd = new MySqlCommand("UPDATE categories SET SortOrder = @so WHERE CategoryID = @id;", conn);
                cmd.Parameters.AddWithValue("@so", i + 1);
                cmd.Parameters.AddWithValue("@id", req.OrderedIds[i]);
                await cmd.ExecuteNonQueryAsync();
            }
            InvalidateCache();
            return Ok(new { success = true, count = req.OrderedIds.Count });
        }

        // POST: api/categories
        [HttpPost]
        public async Task<IActionResult> AddCategory(
            [FromBody] CategoryRequest category)
        {
            if (string.IsNullOrWhiteSpace(category.CategoryName))
                return BadRequest("Category name is required.");

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    INSERT INTO categories
                    (CategoryName, Description, IsActive)
                    VALUES
                    (@Name, @Description, @IsActive);

                    SELECT LAST_INSERT_ID();
                    """;

                await using var command = new MySqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "@Name", category.CategoryName.Trim());

                command.Parameters.AddWithValue(
                    "@Description",
                    (object?)category.Description ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@IsActive", category.IsActive);

                int newId = Convert.ToInt32(
                    await command.ExecuteScalarAsync());

                InvalidateCache();
                return Ok(new
                {
                    message = "Category added successfully.",
                    CategoryID = newId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not add category.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // PUT: api/categories/5
        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateCategory(
            int id,
            [FromBody] CategoryRequest category)
        {
            if (id <= 0 ||
                string.IsNullOrWhiteSpace(category.CategoryName))
                return BadRequest("Invalid category data.");

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    UPDATE categories
                    SET CategoryName = @Name,
                        Description = @Description,
                        IsActive = @IsActive
                    WHERE CategoryID = @ID;
                    """;

                await using var command = new MySqlCommand(query, connection);

                command.Parameters.AddWithValue("@ID", id);
                command.Parameters.AddWithValue(
                    "@Name", category.CategoryName.Trim());

                command.Parameters.AddWithValue(
                    "@Description",
                    (object?)category.Description ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@IsActive", category.IsActive);

                int affected = await command.ExecuteNonQueryAsync();

                if (affected == 0)
                    return NotFound("Category not found.");

                InvalidateCache();
                return Ok(new
                {
                    message = "Category updated successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not update category.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // DELETE: api/categories/5
        // Soft delete: deactivate category without deleting its products.
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteCategory(int id)
        {
            if (id <= 0)
                return BadRequest("Invalid category ID.");

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                int affected = 0;
                await using (var fkOff = new MySqlCommand("SET FOREIGN_KEY_CHECKS = 0;", connection))
                    await fkOff.ExecuteNonQueryAsync();
                try
                {
                    // Remove products belonging exclusively to this category that are already marked deleted
                    await using (var delOrphans = new MySqlCommand("DELETE FROM products WHERE CategoryID = @ID AND ProductName LIKE '__DELETED__%';", connection))
                    {
                        delOrphans.Parameters.AddWithValue("@ID", id);
                        await delOrphans.ExecuteNonQueryAsync();
                    }
                    await using var command = new MySqlCommand("DELETE FROM categories WHERE CategoryID = @ID;", connection);
                    command.Parameters.AddWithValue("@ID", id);
                    affected = await command.ExecuteNonQueryAsync();
                }
                finally
                {
                    await using var fkOn = new MySqlCommand("SET FOREIGN_KEY_CHECKS = 1;", connection);
                    await fkOn.ExecuteNonQueryAsync();
                }

                if (affected == 0)
                    return NotFound("Category not found.");

                InvalidateCache();
                return Ok(new
                {
                    success = true,
                    deleted = true,
                    message = "تم حذف التصنيف نهائياً من قاعدة البيانات."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not deactivate category.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }
    }

    public class CategoryRequest
    {
        public string CategoryName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
