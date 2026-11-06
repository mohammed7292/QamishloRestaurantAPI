
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class categoriesController : ControllerBase
    {
        // GET: api/categories
        [HttpGet]
        public async Task<IActionResult> GetCategories()
        {
            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string query = """
                    SELECT CategoryID, CategoryName, Description, IsActive
                    FROM categories
                    ORDER BY CategoryID;
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

                return Ok(categories);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Database error",
                    error = ex.Message
                });
            }
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
                    error = ex.Message
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
                    error = ex.Message
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

                const string query = """
                    UPDATE categories
                    SET IsActive = 0
                    WHERE CategoryID = @ID;
                    """;

                await using var command = new MySqlCommand(query, connection);
                command.Parameters.AddWithValue("@ID", id);

                int affected = await command.ExecuteNonQueryAsync();

                if (affected == 0)
                    return NotFound("Category not found.");

                return Ok(new
                {
                    message = "Category deactivated successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not deactivate category.",
                    error = ex.Message
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
