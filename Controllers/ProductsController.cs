
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
        [HttpGet]
        public async Task<IActionResult> GetProducts()
        {
            try
            {
                await using var connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string query = """
                    SELECT * FROM products
                    ORDER BY ProductID DESC;
                    """;

                await using var command =
                    new MySqlCommand(query, connection);

                await using var reader =
                    await command.ExecuteReaderAsync();

                var products = new List<Dictionary<string, object?>>();

                while (await reader.ReadAsync())
                {
                    var product = new Dictionary<string, object?>();

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
                return StatusCode(500, new
                {
                    message = "Database error",
                    error = ex.Message
                });
            }
        }

        // POST: api/products
        [HttpPost]
        public async Task<IActionResult> AddProduct(
            [FromBody] ProductRequest product)
        {
            if (product.CategoryID <= 0 ||
                string.IsNullOrWhiteSpace(product.ProductName) ||
                product.Price <= 0)
            {
                return BadRequest(new
                {
                    message = "Invalid product data."
                });
            }

            try
            {
                await using var connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string query = """
                    INSERT INTO products
                    (
                        CategoryID,
                        ProductName,
                        Price,
                        Description,
                        ImageURL,
                        IsAvailable
                    )
                    VALUES
                    (
                        @CategoryID,
                        @ProductName,
                        @Price,
                        @Description,
                        @ImageURL,
                        @IsAvailable
                    );

                    SELECT LAST_INSERT_ID();
                    """;

                await using var command =
                    new MySqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "@CategoryID", product.CategoryID);

                command.Parameters.AddWithValue(
                    "@ProductName", product.ProductName.Trim());

                command.Parameters.AddWithValue(
                    "@Price", product.Price);

                command.Parameters.AddWithValue(
                    "@Description",
                    (object?)product.Description ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@ImageURL",
                    (object?)product.ImageURL ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@IsAvailable", product.IsAvailable);

                var result = await command.ExecuteScalarAsync();

                int newId = Convert.ToInt32(result);

                return Ok(new
                {
                    message = "Product added successfully.",
                    ProductID = newId
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not add product.",
                    error = ex.Message
                });
            }
        }

        // PUT: api/products/5
        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateProduct(
            int id,
            [FromBody] ProductRequest product)
        {
            if (id <= 0 ||
                product.CategoryID <= 0 ||
                string.IsNullOrWhiteSpace(product.ProductName) ||
                product.Price <= 0)
            {
                return BadRequest(new
                {
                    message = "Invalid product data."
                });
            }

            try
            {
                await using var connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string query = """
                    UPDATE products
                    SET
                        CategoryID = @CategoryID,
                        ProductName = @ProductName,
                        Price = @Price,
                        Description = @Description,
                        ImageURL = COALESCE(@ImageURL, ImageURL),
                        IsAvailable = @IsAvailable
                    WHERE ProductID = @ProductID;
                    """;

                await using var command =
                    new MySqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "@ProductID", id);

                command.Parameters.AddWithValue(
                    "@CategoryID", product.CategoryID);

                command.Parameters.AddWithValue(
                    "@ProductName", product.ProductName.Trim());

                command.Parameters.AddWithValue(
                    "@Price", product.Price);

                command.Parameters.AddWithValue(
                    "@Description",
                    (object?)product.Description ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@ImageURL",
                    (object?)product.ImageURL ?? DBNull.Value);

                command.Parameters.AddWithValue(
                    "@IsAvailable", product.IsAvailable);

                int affectedRows =
                    await command.ExecuteNonQueryAsync();

                if (affectedRows == 0)
                {
                    return NotFound(new
                    {
                        message = "Product not found."
                    });
                }

                return Ok(new
                {
                    message = "Product updated successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not update product.",
                    error = ex.Message
                });
            }
        }

        // DELETE: api/products/5
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            if (id <= 0)
                return BadRequest("Invalid product ID.");

            try
            {
                await using var connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string query = """
                    DELETE FROM products
                    WHERE ProductID = @ProductID;
                    """;

                await using var command =
                    new MySqlCommand(query, connection);

                command.Parameters.AddWithValue(
                    "@ProductID", id);

                int affectedRows =
                    await command.ExecuteNonQueryAsync();

                if (affectedRows == 0)
                {
                    return NotFound(new
                    {
                        message = "Product not found."
                    });
                }

                return Ok(new
                {
                    message = "Product deleted successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = "Could not delete product.",
                    error = ex.Message
                });
            }
        }
    }

    public class ProductRequest
    {
        public int CategoryID { get; set; }

        public string ProductName { get; set; } = string.Empty;

        public decimal Price { get; set; }

        public string? Description { get; set; }

        public string? ImageURL { get; set; }

        public bool IsAvailable { get; set; } = true;
    }
}
