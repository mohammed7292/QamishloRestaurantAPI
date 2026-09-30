using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using System;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ReviewsController : ControllerBase
    {
        // POST: api/Reviews/employee
        [HttpPost("employee")]
        public async Task<IActionResult> AddEmployeeReview(
            [FromBody] EmployeeReviewRequest request)
        {
            if (request == null || request.EmployeeID <= 0 || request.CustomerID <= 0)
            {
                return BadRequest(new { success = false, message = "Invalid review data." });
            }

            if (request.Rating < 1 || request.Rating > 5)
            {
                return BadRequest(new { success = false, message = "Rating must be between 1 and 5." });
            }

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            const string employeeSql = @"
                SELECT EmployeeID
                FROM restaurantemployees
                WHERE EmployeeID = @EmployeeID
                  AND IsActive = 1;";

            await using var employeeCommand = new MySqlCommand(employeeSql, connection);
            employeeCommand.Parameters.AddWithValue("@EmployeeID", request.EmployeeID);

            object? employeeExists = await employeeCommand.ExecuteScalarAsync();

            if (employeeExists == null)
            {
                return NotFound(new { success = false, message = "Employee not found." });
            }

            const string customersql = @"
                SELECT CustomerID
                FROM customers
                WHERE CustomerID = @CustomerID
                  AND IsActive = 1;";

            await using var customerCommand = new MySqlCommand(customersql, connection);
            customerCommand.Parameters.AddWithValue("@CustomerID", request.CustomerID);

            object? customerExists = await customerCommand.ExecuteScalarAsync();

            if (customerExists == null)
            {
                return BadRequest(new { success = false, message = "Customer not found." });
            }

            // One current general review per customer/employee.
            const string duplicateSql = @"
                SELECT ReviewID
                FROM employeereviews
                WHERE EmployeeID = @EmployeeID
                  AND CustomerID = @CustomerID
                LIMIT 1;";

            await using var duplicateCommand = new MySqlCommand(duplicateSql, connection);
            duplicateCommand.Parameters.AddWithValue("@EmployeeID", request.EmployeeID);
            duplicateCommand.Parameters.AddWithValue("@CustomerID", request.CustomerID);

            object? existingReview = await duplicateCommand.ExecuteScalarAsync();

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

            await using var insertCommand = new MySqlCommand(insertSql, connection);
            insertCommand.Parameters.AddWithValue("@EmployeeID", request.EmployeeID);
            insertCommand.Parameters.AddWithValue("@CustomerID", request.CustomerID);
            insertCommand.Parameters.AddWithValue("@Rating", request.Rating);
            insertCommand.Parameters.AddWithValue(
                "@Comment",
                string.IsNullOrWhiteSpace(request.Comment) ? DBNull.Value : request.Comment.Trim());

            long reviewId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync());

            return Ok(new
            {
                success = true,
                reviewID = reviewId,
                message = "Employee review submitted successfully."
            });
        }

        // POST: api/Reviews/order
        [HttpPost("order")]
        public async Task<IActionResult> AddOrderRating(
            [FromBody] OrderRatingRequest request)
        {
            if (request == null || request.OrderID <= 0 || request.CustomerID <= 0)
            {
                return BadRequest(new { success = false, message = "Invalid rating data." });
            }

            if (request.FoodRating < 1 || request.FoodRating > 5)
            {
                return BadRequest(new { success = false, message = "Food rating must be between 1 and 5." });
            }

            if (request.WorkerRating.HasValue &&
                (request.WorkerRating.Value < 1 || request.WorkerRating.Value > 5))
            {
                return BadRequest(new { success = false, message = "Worker rating must be between 1 and 5." });
            }

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            const string ordersql = @"
                SELECT
                    OrderID,
                    CustomerID,
                    Status,
                    DeliveryWorkerID
                FROM orders
                WHERE OrderID = @OrderID
                LIMIT 1;";

            await using var orderCommand = new MySqlCommand(ordersql, connection);
            orderCommand.Parameters.AddWithValue("@OrderID", request.OrderID);

            await using var reader = await orderCommand.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return NotFound(new { success = false, message = "Order not found." });
            }

            int? orderCustomerId = reader.IsDBNull(reader.GetOrdinal("CustomerID"))
                ? (int?)null
                : reader.GetInt32(reader.GetOrdinal("CustomerID"));

            string status = reader.GetString(reader.GetOrdinal("Status"));

            int? deliveryWorkerId = reader.IsDBNull(reader.GetOrdinal("DeliveryWorkerID"))
                ? (int?)null
                : reader.GetInt32(reader.GetOrdinal("DeliveryWorkerID"));

            await reader.CloseAsync();

            if (!orderCustomerId.HasValue || orderCustomerId.Value != request.CustomerID)
            {
                return Forbid();
            }

            if (!string.Equals(status, "Delivered", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "An order can be rated only after it is delivered."
                });
            }

            const string duplicateSql = @"
                SELECT RatingID
                FROM OrderRatings
                WHERE OrderID = @OrderID
                  AND CustomerID = @CustomerID
                LIMIT 1;";

            await using var duplicateCommand = new MySqlCommand(duplicateSql, connection);
            duplicateCommand.Parameters.AddWithValue("@OrderID", request.OrderID);
            duplicateCommand.Parameters.AddWithValue("@CustomerID", request.CustomerID);

            object? existingRating = await duplicateCommand.ExecuteScalarAsync();

            if (existingRating != null)
            {
                return Conflict(new
                {
                    success = false,
                    message = "This order has already been rated."
                });
            }

            if (request.WorkerRating.HasValue && !deliveryWorkerId.HasValue)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "This order has no delivery worker to rate."
                });
            }

            const string insertSql = @"
                INSERT INTO OrderRatings
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

            await using var insertCommand = new MySqlCommand(insertSql, connection);
            insertCommand.Parameters.AddWithValue("@OrderID", request.OrderID);
            insertCommand.Parameters.AddWithValue("@CustomerID", request.CustomerID);
            insertCommand.Parameters.AddWithValue("@FoodRating", request.FoodRating);
            insertCommand.Parameters.AddWithValue(
                "@FoodComment",
                string.IsNullOrWhiteSpace(request.FoodComment) ? DBNull.Value : request.FoodComment.Trim());
            insertCommand.Parameters.AddWithValue(
                "@WorkerRating",
                request.WorkerRating.HasValue ? request.WorkerRating.Value : DBNull.Value);
            insertCommand.Parameters.AddWithValue(
                "@WorkerComment",
                string.IsNullOrWhiteSpace(request.WorkerComment) ? DBNull.Value : request.WorkerComment.Trim());

            long ratingId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync());

            return Ok(new
            {
                success = true,
                ratingID = ratingId,
                deliveryWorkerID = deliveryWorkerId,
                message = "Order rating submitted successfully."
            });
        }
    }

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
