using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/EmployeeManagement")]
    public class EmployeeManagementController : ControllerBase
    {
        private readonly IWebHostEnvironment _environment;

        public EmployeeManagementController(
            IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        // =====================================================
        // LOGIN
        // POST: api/EmployeeManagement/login
        // =====================================================

        [HttpPost("login")]
        public async Task<IActionResult> Login(
            [FromBody] EmployeeManagementLoginRequest request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Username) ||
                string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Username and password are required."
                });
            }

            await using var connection =
                DatabaseManager.GetConnection();

            await connection.OpenAsync();

            const string sql = @"
                SELECT UserID, FullName, Username, Role
                FROM users
                WHERE Username = @Username
                  AND Password = @Password
                  AND IsActive = 1
                LIMIT 1;";

            await using var command =
                new MySqlCommand(sql, connection);

            command.Parameters.AddWithValue(
                "@Username", request.Username.Trim());

            command.Parameters.AddWithValue(
                "@Password", request.Password);

            await using var reader =
                await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid username or password."
                });
            }

            string role = reader.GetString(reader.GetOrdinal("Role"));
            if (role != "Admin" && role != "Manager")
            {
                return StatusCode(403, new { success = false, message = "هذه الصفحة متاحة للمدير فقط." });
            }

            return Ok(new
            {
                success = true,
                message = "Login successful.",
                user = new
                {
                    userId = reader.GetInt32(
                        reader.GetOrdinal("UserID")),

                    fullName = reader.GetString(
                        reader.GetOrdinal("FullName")),

                    username = reader.GetString(
                        reader.GetOrdinal("Username")),

                    role = reader.GetString(
                        reader.GetOrdinal("Role"))
                }
            });
        }

        // =====================================================
        // GET ALL EMPLOYEES
        // POST: api/EmployeeManagement/employees
        // =====================================================

        [HttpPost("employees")]
        public async Task<IActionResult> GetAllEmployees(
            [FromBody] EmployeeManagementLoginRequest request)
        {
            if (request == null ||
                !await IsActiveUserAsync(
                    request.Username,
                    request.Password))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid or inactive account."
                });
            }

            var employees =
                new List<EmployeeManagementResponse>();

            await using var connection =
                DatabaseManager.GetConnection();

            await connection.OpenAsync();

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
                    e.CreatedAt,
                    e.UpdatedAt,
                    COALESCE(AVG(r.Rating), 0) AS AverageRating,
                    COUNT(r.ReviewID) AS ReviewCount
                FROM restaurantemployees e
                LEFT JOIN employeereviews r
                    ON r.EmployeeID = e.EmployeeID
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
                    e.ShowInCustomerApp,
                    e.CreatedAt,
                    e.UpdatedAt
                ORDER BY e.FullName;";

            await using var command =
                new MySqlCommand(sql, connection);

            await using var reader =
                await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                employees.Add(
                    new EmployeeManagementResponse
                    {
                        EmployeeID = reader.GetInt32(
                            reader.GetOrdinal("EmployeeID")),

                        FullName = reader.GetString(
                            reader.GetOrdinal("FullName")),

                        JobTitleEN =
                            GetNullableString(reader, "JobTitleEN"),

                        JobTitleAR =
                            GetNullableString(reader, "JobTitleAR"),

                        JobTitleKU =
                            GetNullableString(reader, "JobTitleKU"),

                        DescriptionEN =
                            GetNullableString(reader, "DescriptionEN"),

                        DescriptionAR =
                            GetNullableString(reader, "DescriptionAR"),

                        DescriptionKU =
                            GetNullableString(reader, "DescriptionKU"),

                        PhotoURL =
                            GetNullableString(reader, "PhotoURL"),

                        IsActive = reader.GetBoolean(
                            reader.GetOrdinal("IsActive")),

                        ShowInCustomerApp = reader.GetBoolean(
                            reader.GetOrdinal("ShowInCustomerApp")),

                        CreatedAt = reader.GetDateTime(
                            reader.GetOrdinal("CreatedAt")),

                        UpdatedAt = reader.IsDBNull(
                            reader.GetOrdinal("UpdatedAt"))
                            ? null
                            : reader.GetDateTime(
                                reader.GetOrdinal("UpdatedAt")),

                        AverageRating = reader.GetDecimal(
                            reader.GetOrdinal("AverageRating")),

                        ReviewCount = reader.GetInt32(
                            reader.GetOrdinal("ReviewCount"))
                    });
            }

            return Ok(new
            {
                success = true,
                employees
            });
        }

        // =====================================================
        // ADD EMPLOYEE
        // POST: api/EmployeeManagement/add
        // =====================================================

        [HttpPost("add")]
        public async Task<IActionResult> AddEmployee(
            [FromBody] EmployeeManagementSaveRequest request)
        {
            if (request == null ||
                !await IsActiveUserAsync(
                    request.Username,
                    request.Password))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid or inactive account."
                });
            }

            if (string.IsNullOrWhiteSpace(request.FullName))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Employee name is required."
                });
            }

            await using var connection =
                DatabaseManager.GetConnection();

            await connection.OpenAsync();

            const string sql = @"
                INSERT INTO restaurantemployees
                (
                    FullName,
                    JobTitleEN,
                    JobTitleAR,
                    JobTitleKU,
                    DescriptionEN,
                    DescriptionAR,
                    DescriptionKU,
                    PhotoURL,
                    IsActive,
                    ShowInCustomerApp,
                    CreatedAt,
                    UpdatedAt
                )
                VALUES
                (
                    @FullName,
                    @JobTitleEN,
                    @JobTitleAR,
                    @JobTitleKU,
                    @DescriptionEN,
                    @DescriptionAR,
                    @DescriptionKU,
                    @PhotoURL,
                    @IsActive,
                    @ShowInCustomerApp,
                    CURRENT_TIMESTAMP,
                    CURRENT_TIMESTAMP
                );";

            await using var command =
                new MySqlCommand(sql, connection);

            AddEmployeeParameters(command, request);

            await command.ExecuteNonQueryAsync();

            int employeeId =
                Convert.ToInt32(command.LastInsertedId);

            return Ok(new
            {
                success = true,
                message = "Employee added successfully.",
                employeeId
            });
        }

        // =====================================================
        // UPDATE EMPLOYEE
        // PUT: api/EmployeeManagement/update/{id}
        // =====================================================

        [HttpPut("update/{id:int}")]
        public async Task<IActionResult> UpdateEmployee(
            int id,
            [FromBody] EmployeeManagementSaveRequest request)
        {
            if (request == null ||
                !await IsActiveUserAsync(
                    request.Username,
                    request.Password))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid or inactive account."
                });
            }

            if (id <= 0 ||
                string.IsNullOrWhiteSpace(request.FullName))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Valid employee ID and name are required."
                });
            }

            await using var connection =
                DatabaseManager.GetConnection();

            await connection.OpenAsync();

            const string sql = @"
                UPDATE restaurantemployees
                SET
                    FullName = @FullName,
                    JobTitleEN = @JobTitleEN,
                    JobTitleAR = @JobTitleAR,
                    JobTitleKU = @JobTitleKU,
                    DescriptionEN = @DescriptionEN,
                    DescriptionAR = @DescriptionAR,
                    DescriptionKU = @DescriptionKU,
                    PhotoURL = @PhotoURL,
                    IsActive = @IsActive,
                    ShowInCustomerApp = @ShowInCustomerApp,
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE EmployeeID = @EmployeeID;";

            await using var command =
                new MySqlCommand(sql, connection);

            AddEmployeeParameters(command, request);

            command.Parameters.AddWithValue(
                "@EmployeeID", id);

            int affectedRows =
                await command.ExecuteNonQueryAsync();

            if (affectedRows == 0)
            {
                const string existsSql = @"
                    SELECT COUNT(*)
                    FROM restaurantemployees
                    WHERE EmployeeID = @EmployeeID;";

                await using var existsCommand =
                    new MySqlCommand(existsSql, connection);

                existsCommand.Parameters.AddWithValue(
                    "@EmployeeID", id);

                long count = Convert.ToInt64(
                    await existsCommand.ExecuteScalarAsync());

                if (count == 0)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Employee not found."
                    });
                }
            }

            return Ok(new
            {
                success = true,
                message = "Employee updated successfully."
            });
        }

        // =====================================================
        // UPLOAD EMPLOYEE PHOTO
        // POST: api/EmployeeManagement/upload-photo
        // =====================================================

        [HttpPost("upload-photo")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<IActionResult> UploadPhoto(
            [FromForm] EmployeePhotoUploadRequest request)
        {
            if (request == null ||
                !await IsActiveUserAsync(
                    request.Username,
                    request.Password))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid or inactive account."
                });
            }

            IFormFile? file = request.File;

            if (file == null || file.Length == 0)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Please select an image."
                });
            }

            if (file.Length > 5 * 1024 * 1024)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Image size must not exceed 5 MB."
                });
            }

            string extension =
                Path.GetExtension(file.FileName)
                    .ToLowerInvariant();

            var allowedExtensions = new[]
            {
                ".jpg",
                ".jpeg",
                ".png",
                ".webp"
            };

            if (!allowedExtensions.Contains(extension))
            {
                return BadRequest(new
                {
                    success = false,
                    message =
                        "Only JPG, PNG, and WEBP images are allowed."
                });
            }

            string webRoot = _environment.WebRootPath;

            if (string.IsNullOrWhiteSpace(webRoot))
            {
                webRoot = Path.Combine(
                    _environment.ContentRootPath,
                    "wwwroot");
            }

            string uploadDirectory = Path.Combine(
                webRoot,
                "uploads",
                "employees");

            Directory.CreateDirectory(uploadDirectory);

            string fileName =
                $"{Guid.NewGuid():N}{extension}";

            string filePath = Path.Combine(
                uploadDirectory,
                fileName);

            await using (var stream = new FileStream(
                filePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None))
            {
                await file.CopyToAsync(stream);
            }

            string photoUrl =
                $"/uploads/employees/{fileName}";

            return Ok(new
            {
                success = true,
                photoUrl
            });
        }

        // =====================================================
        // VERIFY ACTIVE USER
        // =====================================================

        private static async Task<bool> IsActiveUserAsync(
            string? username,
            string? password)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password))
            {
                return false;
            }

            await using var connection =
                DatabaseManager.GetConnection();

            await connection.OpenAsync();

            const string sql = @"
                SELECT COUNT(*)
                FROM users
                WHERE Username = @Username
                  AND Password = @Password
                  AND IsActive = 1
                  AND Role IN ('Admin', 'Manager');";

            await using var command =
                new MySqlCommand(sql, connection);

            command.Parameters.AddWithValue(
                "@Username", username.Trim());

            command.Parameters.AddWithValue(
                "@Password", password);

            long count = Convert.ToInt64(
                await command.ExecuteScalarAsync());

            return count > 0;
        }

        // =====================================================
        // EMPLOYEE SQL PARAMETERS
        // =====================================================

        private static void AddEmployeeParameters(
            MySqlCommand command,
            EmployeeManagementSaveRequest request)
        {
            command.Parameters.AddWithValue(
                "@FullName", request.FullName.Trim());

            command.Parameters.AddWithValue(
                "@JobTitleEN", DbValue(request.JobTitleEN));

            command.Parameters.AddWithValue(
                "@JobTitleAR", DbValue(request.JobTitleAR));

            command.Parameters.AddWithValue(
                "@JobTitleKU", DbValue(request.JobTitleKU));

            command.Parameters.AddWithValue(
                "@DescriptionEN", DbValue(request.DescriptionEN));

            command.Parameters.AddWithValue(
                "@DescriptionAR", DbValue(request.DescriptionAR));

            command.Parameters.AddWithValue(
                "@DescriptionKU", DbValue(request.DescriptionKU));

            command.Parameters.AddWithValue(
                "@PhotoURL", DbValue(request.PhotoURL));

            command.Parameters.AddWithValue(
                "@IsActive", request.IsActive);

            command.Parameters.AddWithValue(
                "@ShowInCustomerApp", request.ShowInCustomerApp);
        }

        private static object DbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? DBNull.Value
                : value.Trim();
        }

        private static string? GetNullableString(
            MySqlDataReader reader,
            string column)
        {
            int ordinal = reader.GetOrdinal(column);

            return reader.IsDBNull(ordinal)
                ? null
                : reader.GetString(ordinal);
        }
    }

    // =========================================================
    // LOGIN REQUEST
    // =========================================================

    public class EmployeeManagementLoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    // =========================================================
    // EMPLOYEE SAVE REQUEST
    // =========================================================

    public class EmployeeManagementSaveRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";

        public string FullName { get; set; } = "";

        public string? JobTitleEN { get; set; }
        public string? JobTitleAR { get; set; }
        public string? JobTitleKU { get; set; }

        public string? DescriptionEN { get; set; }
        public string? DescriptionAR { get; set; }
        public string? DescriptionKU { get; set; }

        public string? PhotoURL { get; set; }

        public bool IsActive { get; set; } = true;
        public bool ShowInCustomerApp { get; set; } = true;
    }

    // =========================================================
    // EMPLOYEE PHOTO UPLOAD REQUEST
    // =========================================================

    public class EmployeePhotoUploadRequest
    {
        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        public IFormFile? File { get; set; }
    }

    // =========================================================
    // EMPLOYEE MANAGEMENT RESPONSE
    // =========================================================

    public class EmployeeManagementResponse
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

        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public decimal AverageRating { get; set; }
        public int ReviewCount { get; set; }
    }
}