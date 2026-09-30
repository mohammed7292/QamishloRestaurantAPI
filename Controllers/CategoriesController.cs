using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class categoriesController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Getcategories()
        {
            try
            {
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string query = """
                    SELECT
                        CategoryID,
                        CategoryName,
                        Description,
                        IsActive
                    FROM categories
                    WHERE IsActive = 1
                    ORDER BY CategoryID;
                    """;

                using MySqlCommand command =
                    new MySqlCommand(
                        query,
                        connection);

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                List<Dictionary<string, object?>> categories =
                    new();

                while (await reader.ReadAsync())
                {
                    Dictionary<string, object?> category =
                        new();

                    for (int i = 0;
                         i < reader.FieldCount;
                         i++)
                    {
                        object value =
                            reader.GetValue(i);

                        category[reader.GetName(i)] =
                            value == DBNull.Value
                                ? null
                                : value;
                    }

                    categories.Add(category);
                }

                return Ok(categories);
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        message = "Database error",
                        error = ex.Message
                    });
            }
        }
    }
}