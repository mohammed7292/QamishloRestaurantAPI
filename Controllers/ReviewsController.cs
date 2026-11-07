
using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReviewsController : ControllerBase
    {
        // ==========================================
        // GET: api/reviews/employee
        // Get all employee reviews
        // ==========================================
        [HttpGet("employee")]
        public async Task<IActionResult> GetEmployeeReviews()
        {
            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string sql = @"
                    SELECT
                        ReviewID,
                        EmployeeID,
                        CustomerID,
                        Rating,
                        Comment,
                        CreatedAt
                    FROM employeereviews
                    ORDER BY CreatedAt DESC;";

                await using var command = new MySqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync();

                var reviews = new List<Dictionary<string, object?>>();

                while (await reader.ReadAsync())
                {
                    var review = new Dictionary<string, object?>();

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.GetValue(i);

                        review[reader.GetName(i)] =
                            value == DBNull.Value ? null : value;
                    }

                    reviews.Add(review);
                }

                return Ok(new
                {
                    success = true,
                    total = reviews.Count,
                    data = reviews
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not load employee reviews.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // ==========================================
        // GET: api/reviews/order
        // Get all order reviews
        // ==========================================
        [HttpGet("order")]
        public async Task<IActionResult> GetOrderReviews()
        {
            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string sql = @"
                    SELECT
                        RatingID,
                        OrderID,
                        CustomerID,
                        FoodRating,
                        FoodComment,
                        WorkerRating,
                        WorkerComment,
                        CreatedAt
                    FROM orderratings
                    ORDER BY CreatedAt DESC;";

                await using var command = new MySqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync();

                var reviews = new List<Dictionary<string, object?>>();

                while (await reader.ReadAsync())
                {
                    var review = new Dictionary<string, object?>();

                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        object value = reader.GetValue(i);

                        review[reader.GetName(i)] =
                            value == DBNull.Value ? null : value;
                    }

                    reviews.Add(review);
                }

                return Ok(new
                {
                    success = true,
                    total = reviews.Count,
                    data = reviews
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not load order reviews.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // ==========================================
        // GET: api/reviews/statistics
        // Get review statistics
        // ==========================================
        [HttpGet("statistics")]
        public async Task<IActionResult> GetReviewStatistics()
        {
            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string employeeSql = @"
                    SELECT
                        COUNT(*) AS TotalReviews,
                        COALESCE(AVG(Rating), 0) AS AverageRating
                    FROM employeereviews;";

                await using var employeeCommand =
                    new MySqlCommand(employeeSql, connection);

                await using var employeeReader =
                    await employeeCommand.ExecuteReaderAsync();

                await employeeReader.ReadAsync();

                var employeeStatistics = new
                {
                    TotalReviews = employeeReader.GetInt32("TotalReviews"),
                    AverageRating = employeeReader.GetDecimal("AverageRating")
                };

                await employeeReader.CloseAsync();

                const string orderSql = @"
                    SELECT
                        COUNT(*) AS TotalReviews,
                        COALESCE(AVG(FoodRating), 0) AS AverageFoodRating,
                        COALESCE(AVG(WorkerRating), 0) AS AverageWorkerRating
                    FROM orderratings;";

                await using var orderCommand =
                    new MySqlCommand(orderSql, connection);

                await using var orderReader =
                    await orderCommand.ExecuteReaderAsync();

                await orderReader.ReadAsync();

                var orderStatistics = new
                {
                    TotalReviews = orderReader.GetInt32("TotalReviews"),
                    AverageFoodRating = orderReader.GetDecimal("AverageFoodRating"),
                    AverageWorkerRating = orderReader.GetDecimal("AverageWorkerRating")
                };

                return Ok(new
                {
                    success = true,
                    EmployeeReviews = employeeStatistics,
                    OrderReviews = orderStatistics
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not load review statistics.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // ==========================================
        // POST: api/reviews/employee
        // Add employee review
        // ==========================================
        [HttpPost("employee")]
        public async Task<IActionResult> AddEmployeeReview(
            [FromBody] EmployeeReviewRequest request)
        {
            if (request == null ||
                request.EmployeeID <= 0 ||
                request.CustomerID <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid review data."
                });
            }

            if (request.Rating < 1 || request.Rating > 5)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Rating must be between 1 and 5."
                });
            }

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string employeeSql = @"
                    SELECT EmployeeID
                    FROM restaurantemployees
                    WHERE EmployeeID = @EmployeeID
                      AND IsActive = 1;";

                await using var employeeCommand =
                    new MySqlCommand(employeeSql, connection);

                employeeCommand.Parameters.AddWithValue(
                    "@EmployeeID", request.EmployeeID);

                object? employeeExists =
                    await employeeCommand.ExecuteScalarAsync();

                if (employeeExists == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Employee not found."
                    });
                }

                const string customerSql = @"
                    SELECT CustomerID
                    FROM customers
                    WHERE CustomerID = @CustomerID
                      AND IsActive = 1;";

                await using var customerCommand =
                    new MySqlCommand(customerSql, connection);

                customerCommand.Parameters.AddWithValue(
                    "@CustomerID", request.CustomerID);

                object? customerExists =
                    await customerCommand.ExecuteScalarAsync();

                if (customerExists == null)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Customer not found."
                    });
                }

                const string duplicateSql = @"
                    SELECT ReviewID
                    FROM employeereviews
                    WHERE EmployeeID = @EmployeeID
                      AND CustomerID = @CustomerID
                    LIMIT 1;";

                await using var duplicateCommand =
                    new MySqlCommand(duplicateSql, connection);

                duplicateCommand.Parameters.AddWithValue(
                    "@EmployeeID", request.EmployeeID);

                duplicateCommand.Parameters.AddWithValue(
                    "@CustomerID", request.CustomerID);

                object? existingReview =
                    await duplicateCommand.ExecuteScalarAsync();

                if (existingReview != null)
                {
                    return Conflict(new
                    {
                        success = false,
                        message = "You have already reviewed this employee."
                    });
                }

                const string insertSql = @"
                    INSERT INTO employeereviews
                    (
                        EmployeeID,
                        CustomerID,
                        Rating,
                        Comment,
                        CreatedAt
                    )
                    VALUES
                    (
                        @EmployeeID,
                        @CustomerID,
                        @Rating,
                        @Comment,
                        CURRENT_TIMESTAMP
                    );

                    SELECT LAST_INSERT_ID();";

                await using var insertCommand =
                    new MySqlCommand(insertSql, connection);

                insertCommand.Parameters.AddWithValue(
                    "@EmployeeID", request.EmployeeID);

                insertCommand.Parameters.AddWithValue(
                    "@CustomerID", request.CustomerID);

                insertCommand.Parameters.AddWithValue(
                    "@Rating", request.Rating);

                insertCommand.Parameters.AddWithValue(
                    "@Comment",
                    string.IsNullOrWhiteSpace(request.Comment)
                        ? DBNull.Value
                        : request.Comment.Trim());

                long reviewId = Convert.ToInt64(
                    await insertCommand.ExecuteScalarAsync());

                return Ok(new
                {
                    success = true,
                    reviewID = reviewId,
                    message = "Employee review submitted successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not submit employee review.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }

        // ==========================================
        // POST: api/reviews/order
        // Add order rating
        // ==========================================
        [HttpPost("order")]
        public async Task<IActionResult> AddOrderRating(
            [FromBody] OrderRatingRequest request)
        {
            if (request == null ||
                request.OrderID <= 0 ||
                request.CustomerID <= 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid rating data."
                });
            }

            if (request.FoodRating < 1 || request.FoodRating > 5)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Food rating must be between 1 and 5."
                });
            }

            if (request.WorkerRating.HasValue &&
                (request.WorkerRating.Value < 1 ||
                 request.WorkerRating.Value > 5))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Worker rating must be between 1 and 5."
                });
            }

            try
            {
                await using var connection = DatabaseManager.GetConnection();
                await connection.OpenAsync();

                const string orderSql = @"
                    SELECT
                        OrderID,
                        CustomerID,
                        Status,
                        DeliveryWorkerID
                    FROM orders
                    WHERE OrderID = @OrderID
                    LIMIT 1;";

                await using var orderCommand =
                    new MySqlCommand(orderSql, connection);

                orderCommand.Parameters.AddWithValue(
                    "@OrderID", request.OrderID);

                int? orderCustomerId;
                string status;
                int? deliveryWorkerId;

                await using (var reader =
                    await orderCommand.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        return NotFound(new
                        {
                            success = false,
                            message = "Order not found."
                        });
                    }

                    int customerOrdinal =
                        reader.GetOrdinal("CustomerID");

                    orderCustomerId = reader.IsDBNull(customerOrdinal)
                        ? null
                        : reader.GetInt32(customerOrdinal);

                    status = reader.GetString(
                        reader.GetOrdinal("Status"));

                    int workerOrdinal =
                        reader.GetOrdinal("DeliveryWorkerID");

                    deliveryWorkerId = reader.IsDBNull(workerOrdinal)
                        ? null
                        : reader.GetInt32(workerOrdinal);
                }

                if (!orderCustomerId.HasValue ||
                    orderCustomerId.Value != request.CustomerID)
                {
                    return Forbid();
                }

                if (!string.Equals(
                    status,
                    "Delivered",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "An order can be rated only after it is delivered."
                    });
                }

                const string duplicateSql = @"
                    SELECT RatingID
                    FROM orderratings
                    WHERE OrderID = @OrderID
                      AND CustomerID = @CustomerID
                    LIMIT 1;";

                await using var duplicateCommand =
                    new MySqlCommand(duplicateSql, connection);

                duplicateCommand.Parameters.AddWithValue(
                    "@OrderID", request.OrderID);

                duplicateCommand.Parameters.AddWithValue(
                    "@CustomerID", request.CustomerID);

                object? existingRating =
                    await duplicateCommand.ExecuteScalarAsync();

                if (existingRating != null)
                {
                    return Conflict(new
                    {
                        success = false,
                        message = "This order has already been rated."
                    });
                }

                if (request.WorkerRating.HasValue &&
                    !deliveryWorkerId.HasValue)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "This order has no delivery worker to rate."
                    });
                }

                const string insertSql = @"
                    INSERT INTO orderratings
                    (
                        OrderID,
                        CustomerID,
                        FoodRating,
                        FoodComment,
                        WorkerRating,
                        WorkerComment,
                        CreatedAt
                    )
                    VALUES
                    (
                        @OrderID,
                        @CustomerID,
                        @FoodRating,
                        @FoodComment,
                        @WorkerRating,
                        @WorkerComment,
                        CURRENT_TIMESTAMP
                    );

                    SELECT LAST_INSERT_ID();";

                await using var insertCommand =
                    new MySqlCommand(insertSql, connection);

                insertCommand.Parameters.AddWithValue(
                    "@OrderID", request.OrderID);

                insertCommand.Parameters.AddWithValue(
                    "@CustomerID", request.CustomerID);

                insertCommand.Parameters.AddWithValue(
                    "@FoodRating", request.FoodRating);

                insertCommand.Parameters.AddWithValue(
                    "@FoodComment",
                    string.IsNullOrWhiteSpace(request.FoodComment)
                        ? DBNull.Value
                        : request.FoodComment.Trim());

                insertCommand.Parameters.AddWithValue(
                    "@WorkerRating",
                    request.WorkerRating.HasValue
                        ? request.WorkerRating.Value
                        : DBNull.Value);

                insertCommand.Parameters.AddWithValue(
                    "@WorkerComment",
                    string.IsNullOrWhiteSpace(request.WorkerComment)
                        ? DBNull.Value
                        : request.WorkerComment.Trim());

                long ratingId = Convert.ToInt64(
                    await insertCommand.ExecuteScalarAsync());

                return Ok(new
                {
                    success = true,
                    ratingID = ratingId,
                    deliveryWorkerID = deliveryWorkerId,
                    message = "Order rating submitted successfully."
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "Could not submit order rating.",
                    error = "تعذر إكمال العملية بسبب خطأ داخلي."
                });
            }
        }
    }

    // ==========================================
    // Request Models
    // ==========================================

    public class EmployeeReviewRequest
    {
        public int EmployeeID { get; set; }
        public int CustomerID { get; set; }
        public int Rating { get; set; }
        public string? Comment { get; set; }
    }

    public class OrderRatingRequest
    {
        public int OrderID { get; set; }
        public int CustomerID { get; set; }
        public int FoodRating { get; set; }
        public string? FoodComment { get; set; }
        public int? WorkerRating { get; set; }
        public string? WorkerComment { get; set; }
    }
}
