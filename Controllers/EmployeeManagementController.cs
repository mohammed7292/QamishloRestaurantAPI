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

        public EmployeeManagementController(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] EmployeeManagementLoginRequest request)
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

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                SELECT UserID, FullName, Username, Role, IsActive
                FROM users
                WHERE Username = @Username
                  AND Password = @Password
                LIMIT 1;";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Username", request.Username.Trim());
            command.Parameters.AddWithValue("@Password", request.Password);

            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid username or password."
                });
            }

            int userId = reader.GetInt32(reader.GetOrdinal("UserID"));
            string fullName = reader.GetString(reader.GetOrdinal("FullName"));
            string username = reader.GetString(reader.GetOrdinal("Username"));
            string role = reader.GetString(reader.GetOrdinal("Role"));
            bool isActive = reader.GetBoolean(reader.GetOrdinal("IsActive"));
            await reader.CloseAsync();

            if (role != "Admin" && role != "Manager")
            {
                return StatusCode(403, new { success = false, message = "هذه الصفحة متاحة للمدير فقط." });
            }

            if (!isActive && (userId == 1 || userId == 2 || role == "Admin"))
            {
                await using var activateCmd = new MySqlCommand("UPDATE users SET IsActive = 1 WHERE UserID = @id;", connection);
                activateCmd.Parameters.AddWithValue("@id", userId);
                await activateCmd.ExecuteNonQueryAsync();
            }
            else if (!isActive)
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "هذا الحساب معطّل."
                });
            }

            return Ok(new
            {
                success = true,
                message = "Login successful.",
                user = new
                {
                    userId,
                    fullName,
                    username,
                    role
                }
            });
        }

        [HttpPost("employees")]
        public async Task<IActionResult> GetAllEmployees([FromBody] EmployeeManagementLoginRequest request)
        {
            if (!await IsAuthorizedAsync(request?.Username, request?.Password))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid or inactive account."
                });
            }

            var employees = new List<EmployeeManagementResponse>();

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

            await using var command = new MySqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                employees.Add(new EmployeeManagementResponse
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
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                    UpdatedAt = reader.IsDBNull(reader.GetOrdinal("UpdatedAt"))
                        ? null
                        : reader.GetDateTime(reader.GetOrdinal("UpdatedAt")),
                    AverageRating = reader.GetDecimal(reader.GetOrdinal("AverageRating")),
                    ReviewCount = reader.GetInt32(reader.GetOrdinal("ReviewCount"))
                });
            }

            return Ok(new
            {
                success = true,
                employees
            });
        }

        [HttpPost("add")]
        public async Task<IActionResult> AddEmployee([FromBody] EmployeeManagementSaveRequest request)
        {
            if (request == null || !await IsAuthorizedAsync(request.Username, request.Password))
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

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            request.PhotoURL = await NormalizeAndPersistPhotoUrlAsync(connection, request.PhotoURL);

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

            await using var command = new MySqlCommand(sql, connection);
            AddEmployeeParameters(command, request);
            await command.ExecuteNonQueryAsync();

            int employeeId = Convert.ToInt32(command.LastInsertedId);

            return Ok(new
            {
                success = true,
                message = "Employee added successfully.",
                employeeId,
                photoUrl = request.PhotoURL
            });
        }

        [HttpPut("update/{id:int}")]
        public async Task<IActionResult> UpdateEmployee(int id, [FromBody] EmployeeManagementSaveRequest request)
        {
            if (request == null || !await IsAuthorizedAsync(request.Username, request.Password))
            {
                return Unauthorized(new
                {
                    success = false,
                    message = "Invalid or inactive account."
                });
            }

            if (id <= 0 || string.IsNullOrWhiteSpace(request.FullName))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Valid employee ID and name are required."
                });
            }

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            request.PhotoURL = await NormalizeAndPersistPhotoUrlAsync(connection, request.PhotoURL);

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
                    PhotoURL = COALESCE(@PhotoURL, PhotoURL),
                    IsActive = @IsActive,
                    ShowInCustomerApp = @ShowInCustomerApp,
                    UpdatedAt = CURRENT_TIMESTAMP
                WHERE EmployeeID = @EmployeeID;";

            await using var command = new MySqlCommand(sql, connection);
            AddEmployeeParameters(command, request);
            command.Parameters.AddWithValue("@EmployeeID", id);

            int affectedRows = await command.ExecuteNonQueryAsync();
            if (affectedRows == 0)
            {
                const string existsSql = "SELECT COUNT(*) FROM restaurantemployees WHERE EmployeeID = @EmployeeID;";
                await using var existsCommand = new MySqlCommand(existsSql, connection);
                existsCommand.Parameters.AddWithValue("@EmployeeID", id);
                long count = Convert.ToInt64(await existsCommand.ExecuteScalarAsync());
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
                message = "Employee updated successfully.",
                photoUrl = request.PhotoURL
            });
        }

        // Serve employee photos from durable MySQL storage. The URL shape stays
        // /uploads/employees/{fileName}, so existing PhotoURL values remain valid.
        [HttpGet("~/uploads/employees/{fileName}")]
        public async Task<IActionResult> GetEmployeePhoto(string fileName)
        {
            string safeName = Path.GetFileName(fileName);
            if (!string.Equals(safeName, fileName, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(safeName))
                return BadRequest();

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            try
            {
                const string sql = "SELECT ContentType, ImageData FROM employeephotostorage WHERE FileName=@fileName LIMIT 1;";
                await using var command = new MySqlCommand(sql, connection);
                command.Parameters.AddWithValue("@fileName", safeName);
                await using var reader = await command.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    string contentType = reader.IsDBNull(0) ? GetEmployeePhotoContentType(safeName) : reader.GetString(0);
                    byte[] imageData = (byte[])reader["ImageData"];
                    return File(imageData, contentType);
                }
            }
            catch
            {
            }

            // Backward compatibility for images uploaded to disk
            string legacyPath = Path.Combine(
                _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                "uploads",
                "employees",
                safeName);
            if (System.IO.File.Exists(legacyPath))
                return PhysicalFile(legacyPath, GetEmployeePhotoContentType(safeName));

            return NotFound();
        }

        public static string GetEmployeePhotoContentType(string fileName)
            => Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };

        [HttpPost("upload-photo")]
        [Consumes("multipart/form-data")]
        [RequestSizeLimit(5 * 1024 * 1024)]
        public async Task<IActionResult> UploadPhoto([FromForm] EmployeePhotoUploadRequest request)
        {
            if (request == null || !await IsAuthorizedAsync(request.Username, request.Password))
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

            string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            if (!allowedExtensions.Contains(extension))
            {
                extension = ".jpg";
            }

            string fileName = $"{Guid.NewGuid():N}{extension}";
            string contentType = GetEmployeePhotoContentType(fileName);
            await using var imageStream = new MemoryStream();
            await file.CopyToAsync(imageStream);
            byte[] imageData = imageStream.ToArray();

            await using (var connection = DatabaseManager.GetConnection())
            {
                await connection.OpenAsync();
                await SavePhotoToStorageAsync(connection, fileName, contentType, imageData);
            }

            // Also save a local copy in wwwroot/uploads/employees as a secondary fallback
            try
            {
                string uploadsFolder = Path.Combine(
                    _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot"),
                    "uploads",
                    "employees");
                Directory.CreateDirectory(uploadsFolder);
                await System.IO.File.WriteAllBytesAsync(Path.Combine(uploadsFolder, fileName), imageData);
            }
            catch
            {
            }

            string photoUrl = $"/uploads/employees/{fileName}";
            return Ok(new
            {
                success = true,
                photoUrl
            });
        }

        public static async Task SavePhotoToStorageAsync(MySqlConnection connection, string fileName, string contentType, byte[] imageData)
        {
            const string saveSql = @"
                INSERT INTO employeephotostorage(FileName, ContentType, ImageData, UploadedAt)
                VALUES(@fileName, @contentType, @imageData, UTC_TIMESTAMP())
                ON DUPLICATE KEY UPDATE
                    ContentType = VALUES(ContentType),
                    ImageData = VALUES(ImageData),
                    UploadedAt = UTC_TIMESTAMP();";
            await using var save = new MySqlCommand(saveSql, connection);
            save.Parameters.AddWithValue("@fileName", fileName);
            save.Parameters.AddWithValue("@contentType", contentType);
            save.Parameters.Add("@imageData", MySqlDbType.LongBlob).Value = imageData;
            await save.ExecuteNonQueryAsync();
        }

        public static async Task<string?> NormalizeAndPersistPhotoUrlAsync(MySqlConnection connection, string? rawPhotoUrl)
        {
            if (string.IsNullOrWhiteSpace(rawPhotoUrl))
                return null;

            string trimmed = rawPhotoUrl.Trim();
            if (trimmed.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int commaIdx = trimmed.IndexOf(',');
                if (commaIdx > 0)
                {
                    string meta = trimmed[..commaIdx].ToLowerInvariant();
                    string ext = meta.Contains("png") ? ".png" : meta.Contains("webp") ? ".webp" : ".jpg";
                    string contentType = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : "image/jpeg";
                    byte[] bytes = Convert.FromBase64String(trimmed[(commaIdx + 1)..]);
                    string fileName = $"{Guid.NewGuid():N}{ext}";
                    await SavePhotoToStorageAsync(connection, fileName, contentType, bytes);
                    return $"/uploads/employees/{fileName}";
                }
            }
            else if (trimmed.Length > 200 && trimmed.All(c => char.IsLetterOrDigit(c) || c is '+' or '/' or '=' or '\r' or '\n'))
            {
                byte[] bytes = Convert.FromBase64String(trimmed);
                string fileName = $"{Guid.NewGuid():N}.jpg";
                await SavePhotoToStorageAsync(connection, fileName, "image/jpeg", bytes);
                return $"/uploads/employees/{fileName}";
            }

            return trimmed;
        }

        private async Task<bool> IsAuthorizedAsync(string? bodyUsername, string? bodyPassword)
        {
            if (await QamishloRestaurantAPI.Security.ManagerCredentialValidator.IsManagerAsync(Request))
                return true;

            if (string.IsNullOrWhiteSpace(bodyUsername) || string.IsNullOrWhiteSpace(bodyPassword))
                return false;

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                SELECT COUNT(*)
                FROM users
                WHERE Username = @Username
                  AND Password = @Password
                  AND Role IN ('Admin', 'Manager');";

            await using var command = new MySqlCommand(sql, connection);
            command.Parameters.AddWithValue("@Username", bodyUsername.Trim());
            command.Parameters.AddWithValue("@Password", bodyPassword);

            long count = Convert.ToInt64(await command.ExecuteScalarAsync());
            return count > 0;
        }

        private static void AddEmployeeParameters(MySqlCommand command, EmployeeManagementSaveRequest request)
        {
            command.Parameters.AddWithValue("@FullName", request.FullName.Trim());
            command.Parameters.AddWithValue("@JobTitleEN", DbValue(request.JobTitleEN));
            command.Parameters.AddWithValue("@JobTitleAR", DbValue(request.JobTitleAR));
            command.Parameters.AddWithValue("@JobTitleKU", DbValue(request.JobTitleKU));
            command.Parameters.AddWithValue("@DescriptionEN", DbValue(request.DescriptionEN));
            command.Parameters.AddWithValue("@DescriptionAR", DbValue(request.DescriptionAR));
            command.Parameters.AddWithValue("@DescriptionKU", DbValue(request.DescriptionKU));
            command.Parameters.AddWithValue("@PhotoURL", DbValue(request.PhotoURL));
            command.Parameters.AddWithValue("@IsActive", request.IsActive);
            command.Parameters.AddWithValue("@ShowInCustomerApp", request.ShowInCustomerApp);
        }

        private static object DbValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

        private static string? GetNullableString(MySqlDataReader reader, string column)
        {
            int ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
    }

    public class EmployeeManagementLoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

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

    public class EmployeePhotoUploadRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public IFormFile? File { get; set; }
    }

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
