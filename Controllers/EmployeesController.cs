using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class EmployeesController : ControllerBase
    {
        // GET: api/Employees
        // Returns employees visible in the Customer App.
        [HttpGet]
        public async Task<IActionResult> GetEmployees()
        {
            var employees = new List<EmployeeResponse>();

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            // Auto-sync delivery workers into restaurantemployees so they appear in Staff & Customer Team Ratings
            try
            {
                const string syncDriversSql = @"
                    INSERT INTO restaurantemployees
                        (FullName, JobTitleEN, JobTitleAR, JobTitleKU, DescriptionEN, DescriptionAR, DescriptionKU, IsActive, ShowInCustomerApp)
                    SELECT
                        dw.FullName,
                        'Delivery Driver',
                        'عامل توصيل',
                        'شۆفێری گەیاندن',
                        'Qamishlo Fast Food Delivery Team',
                        'فريق توصيل مطعم قامشلو',
                        'تیمی گەیاندنی خواردنگەی قامشلو',
                        dw.IsActive,
                        1
                    FROM deliveryworkers dw
                    WHERE NOT EXISTS (
                        SELECT 1 FROM restaurantemployees re
                        WHERE LOWER(TRIM(re.FullName)) = LOWER(TRIM(dw.FullName))
                    );";
                await using var syncCmd = new MySqlCommand(syncDriversSql, connection);
                await syncCmd.ExecuteNonQueryAsync();
            }
            catch { }

            const string sql = @"
                SELECT
                    e.EmployeeID,
                    e.FullName,
                    e.JobTitleEN,
                    e.JobTitleAR,
                    e.JobTitleKU,
                    e.DescriptionEN,
                    e.DescriptionAR,
                    e.DescriptionKU,
                    e.PhotoURL,
                    e.IsActive,
                    e.ShowInCustomerApp,
                    COALESCE(AVG(r.Rating), 0) AS AverageRating,
                    COUNT(r.ReviewID) AS ReviewCount
                FROM restaurantemployees e
                LEFT JOIN employeereviews r
                    ON r.EmployeeID = e.EmployeeID
                WHERE e.IsActive = 1
                  AND e.ShowInCustomerApp = 1
                GROUP BY
                    e.EmployeeID,
                    e.FullName,
                    e.JobTitleEN,
                    e.JobTitleAR,
                    e.JobTitleKU,
                    e.DescriptionEN,
                    e.DescriptionAR,
                    e.DescriptionKU,
                    e.PhotoURL,
                    e.IsActive,
                    e.ShowInCustomerApp
                ORDER BY e.FullName;";

            await using var command = new MySqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                employees.Add(ReadEmployee(reader));
            }

            return Ok(new
            {
                success = true,
                employees
            });
        }

        // GET: api/Employees/1
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetEmployee(int id)
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            const string employeeSql = @"
                SELECT
                    e.EmployeeID,
                    e.FullName,
                    e.JobTitleEN,
                    e.JobTitleAR,
                    e.JobTitleKU,
                    e.DescriptionEN,
                    e.DescriptionAR,
                    e.DescriptionKU,
                    e.PhotoURL,
                    e.IsActive,
                    e.ShowInCustomerApp,
                    COALESCE(AVG(r.Rating), 0) AS AverageRating,
                    COUNT(r.ReviewID) AS ReviewCount
                FROM restaurantemployees e
                LEFT JOIN employeereviews r
                    ON r.EmployeeID = e.EmployeeID
                WHERE e.EmployeeID = @EmployeeID
                  AND e.IsActive = 1
                  AND e.ShowInCustomerApp = 1
                GROUP BY
                    e.EmployeeID,
                    e.FullName,
                    e.JobTitleEN,
                    e.JobTitleAR,
                    e.JobTitleKU,
                    e.DescriptionEN,
                    e.DescriptionAR,
                    e.DescriptionKU,
                    e.PhotoURL,
                    e.IsActive,
                    e.ShowInCustomerApp;";

            await using var command = new MySqlCommand(employeeSql, connection);
            command.Parameters.AddWithValue("@EmployeeID", id);

            await using var reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return NotFound(new
                {
                    success = false,
                    message = "Employee not found."
                });
            }

            var employee = ReadEmployee(reader);
            await reader.CloseAsync();

            const string reviewsSql = @"
                SELECT
                    r.ReviewID,
                    r.CustomerID,
                    c.FullName AS CustomerName,
                    r.Rating,
                    r.Comment,
                    r.CreatedAt
                FROM employeereviews r
                LEFT JOIN customers c
                    ON c.CustomerID = r.CustomerID
                WHERE r.EmployeeID = @EmployeeID
                ORDER BY r.CreatedAt DESC;";

            await using var reviewCommand = new MySqlCommand(reviewsSql, connection);
            reviewCommand.Parameters.AddWithValue("@EmployeeID", id);
            await using var reviewReader = await reviewCommand.ExecuteReaderAsync();

            var reviews = new List<EmployeeReviewResponse>();

            while (await reviewReader.ReadAsync())
            {
                reviews.Add(new EmployeeReviewResponse
                {
                    ReviewID = reviewReader.GetInt32(reviewReader.GetOrdinal("ReviewID")),
                    CustomerID = reviewReader.IsDBNull(reviewReader.GetOrdinal("CustomerID"))
                        ? (int?)null
                        : reviewReader.GetInt32(reviewReader.GetOrdinal("CustomerID")),
                    CustomerName = reviewReader.IsDBNull(reviewReader.GetOrdinal("CustomerName"))
                        ? "زبون المطعم"
                        : reviewReader.GetString(reviewReader.GetOrdinal("CustomerName")),
                    Rating = reviewReader.GetInt32(reviewReader.GetOrdinal("Rating")),
                    Comment = reviewReader.IsDBNull(reviewReader.GetOrdinal("Comment"))
                        ? null
                        : reviewReader.GetString(reviewReader.GetOrdinal("Comment")),
                    CreatedAt = reviewReader.GetDateTime(reviewReader.GetOrdinal("CreatedAt"))
                });
            }

            return Ok(new
            {
                success = true,
                employee,
                reviews
            });
        }

        [HttpGet("photo/{fileName}")]
        public async Task<IActionResult> GetEmployeePhotoByRoute(string fileName)
        {
            string safeName = Path.GetFileName(fileName ?? "");
            if (string.IsNullOrWhiteSpace(safeName))
                return NotFound();

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            const string sql = "SELECT ContentType, ImageData FROM employeephotostorage WHERE FileName=@fileName LIMIT 1;";
            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@fileName", safeName);
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                string contentType = reader.IsDBNull(0) ? EmployeeManagementController.GetEmployeePhotoContentType(safeName) : reader.GetString(0);
                byte[] bytes = (byte[])reader["ImageData"];
                return File(bytes, contentType);
            }

            return NotFound();
        }

        private static EmployeeResponse ReadEmployee(MySqlDataReader reader)
        {
            return new EmployeeResponse
            {
                EmployeeID = reader.GetInt32(reader.GetOrdinal("EmployeeID")),
                FullName = reader.GetString(reader.GetOrdinal("FullName")),
                JobTitleEN = GetNullableString(reader, "JobTitleEN"),
                JobTitleAR = GetNullableString(reader, "JobTitleAR"),
                JobTitleKU = GetNullableString(reader, "JobTitleKU"),
                DescriptionEN = GetNullableString(reader, "DescriptionEN"),
                DescriptionAR = GetNullableString(reader, "DescriptionAR"),
                DescriptionKU = GetNullableString(reader, "DescriptionKU"),
                PhotoURL = GetNullableString(reader, "PhotoURL"),
                IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                ShowInCustomerApp = reader.GetBoolean(reader.GetOrdinal("ShowInCustomerApp")),
                AverageRating = reader.GetDecimal(reader.GetOrdinal("AverageRating")),
                ReviewCount = reader.GetInt32(reader.GetOrdinal("ReviewCount"))
            };
        }

        private static string? GetNullableString(MySqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
    }

    public class EmployeeResponse
    {
        public int EmployeeID { get; set; }
        public string FullName { get; set; } = "";
        public string? JobTitleEN { get; set; }
        public string? JobTitleAR { get; set; }
        public string? JobTitleKU { get; set; }
        public string? DescriptionEN { get; set; }
        public string? DescriptionAR { get; set; }
        public string? DescriptionKU { get; set; }
        public string? PhotoURL { get; set; }
        public bool IsActive { get; set; }
        public bool ShowInCustomerApp { get; set; }
        public decimal AverageRating { get; set; }
        public int ReviewCount { get; set; }
    }

    public class EmployeeReviewResponse
    {
        public int ReviewID { get; set; }
        public int? CustomerID { get; set; }
        public string CustomerName { get; set; } = "";
        public int Rating { get; set; }
        public string? Comment { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
