using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using System.Data;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class productsController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Getproducts()
        {
            try
            {
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                string query = "SELECT * FROM products";

                using MySqlCommand command =
                    new MySqlCommand(query, connection);

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                List<Dictionary<string, object?>> products = new();

                while (await reader.ReadAsync())
                {
                    Dictionary<string, object?> product = new();

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.GetValue(i);

                        product[reader.GetName(i)] =
                            value == DBNull.Value ? null : value;
                    }

                    products.Add(product);
                }

                return Ok(products);
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