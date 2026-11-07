using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using System.Security.Cryptography;

namespace QamishloRestaurantAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class customersController : ControllerBase
    {
        // =========================================================
        // REGISTER
        // =========================================================

        [HttpPost("register")]
        public async Task<IActionResult> Register(
            [FromBody] RegisterRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.FullName))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Full name is required."
                    });
                }

                if (string.IsNullOrWhiteSpace(request.Phone))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Phone is required."
                    });
                }

                if (string.IsNullOrWhiteSpace(request.Password))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Password is required."
                    });
                }

                if (request.Password.Length < 6)
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Password must be at least 6 characters."
                    });
                }

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                // -------------------------------------------------
                // Check phone
                // -------------------------------------------------

                const string checkSql = @"
                    SELECT CustomerID
                    FROM customers
                    WHERE Phone = @Phone
                    LIMIT 1;";

                using (MySqlCommand checkCommand =
                       new MySqlCommand(checkSql, connection))
                {
                    checkCommand.Parameters.AddWithValue(
                        "@Phone",
                        request.Phone.Trim());

                    object? existing =
                        await checkCommand.ExecuteScalarAsync();

                    if (existing != null)
                    {
                        return Conflict(new
                        {
                            success = false,
                            message = "Phone number already registered."
                        });
                    }
                }

                // -------------------------------------------------
                // Hash password
                // -------------------------------------------------

                string passwordHash =
                    PasswordHelper.HashPassword(
                        request.Password);

                // -------------------------------------------------
                // Insert customer
                // -------------------------------------------------

                const string insertSql = @"
                    INSERT INTO customers
                    (
                        FullName,
                        Phone,
                        PasswordHash,
                        Address,
                        Latitude,
                        Longitude,
                        Notes,
                        IsActive
                    )
                    VALUES
                    (
                        @FullName,
                        @Phone,
                        @PasswordHash,
                        @Address,
                        @Latitude,
                        @Longitude,
                        @Notes,
                        1
                    );

                    SELECT LAST_INSERT_ID();";

                using MySqlCommand insertCommand =
                    new MySqlCommand(insertSql, connection);

                insertCommand.Parameters.AddWithValue(
                    "@FullName",
                    request.FullName.Trim());

                insertCommand.Parameters.AddWithValue(
                    "@Phone",
                    request.Phone.Trim());

                insertCommand.Parameters.AddWithValue(
                    "@PasswordHash",
                    passwordHash);

                insertCommand.Parameters.AddWithValue(
                    "@Address",
                    string.IsNullOrWhiteSpace(request.Address)
                        ? DBNull.Value
                        : request.Address.Trim());

                insertCommand.Parameters.AddWithValue(
                    "@Latitude",
                    request.Latitude.HasValue
                        ? request.Latitude.Value
                        : DBNull.Value);

                insertCommand.Parameters.AddWithValue(
                    "@Longitude",
                    request.Longitude.HasValue
                        ? request.Longitude.Value
                        : DBNull.Value);

                insertCommand.Parameters.AddWithValue(
                    "@Notes",
                    string.IsNullOrWhiteSpace(request.Notes)
                        ? DBNull.Value
                        : request.Notes.Trim());

                object? result =
                    await insertCommand.ExecuteScalarAsync();

                int customerId =
                    Convert.ToInt32(result);

                return Ok(new
                {
                    success = true,
                    message = "Registration successful.",
                    customer = new
                    {
                        customerID = customerId,
                        fullName = request.FullName.Trim(),
                        phone = request.Phone.Trim(),
                        address = request.Address,
                        latitude = request.Latitude,
                        longitude = request.Longitude,
                        notes = request.Notes
                    }
                });
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"Customer Register Error: {ex.Message}");
#endif

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Registration failed."
                    });
            }
        }


        // =========================================================
        // LOGIN
        // =========================================================

        [HttpPost("login")]
        public async Task<IActionResult> Login(
            [FromBody] LoginRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.Phone) ||
                    string.IsNullOrWhiteSpace(request.Password))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Phone and password are required."
                    });
                }

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string sql = @"
                    SELECT
                        CustomerID,
                        FullName,
                        Phone,
                        PasswordHash,
                        Address,
                        Latitude,
                        Longitude,
                        Notes,
                        IsActive
                    FROM customers
                    WHERE Phone = @Phone
                    LIMIT 1;";

                using MySqlCommand command =
                    new MySqlCommand(sql, connection);

                command.Parameters.AddWithValue(
                    "@Phone",
                    request.Phone.Trim());

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message = "Invalid phone number or password."
                    });
                }

                bool isActive =
                    Convert.ToBoolean(
                        reader["IsActive"]);

                if (!isActive)
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message = "This account is inactive."
                    });
                }

                string passwordHash =
                    reader["PasswordHash"]?.ToString()
                    ?? "";

                bool passwordValid =
                    PasswordHelper.VerifyPassword(
                        request.Password,
                        passwordHash);

                if (!passwordValid)
                {
                    return Unauthorized(new
                    {
                        success = false,
                        message = "Invalid phone number or password."
                    });
                }

                int customerId =
                    Convert.ToInt32(
                        reader["CustomerID"]);

                string fullName =
                    reader["FullName"]?.ToString()
                    ?? "";

                string phone =
                    reader["Phone"]?.ToString()
                    ?? "";

                string? address =
                    reader["Address"] == DBNull.Value
                        ? null
                        : reader["Address"]?.ToString();

                double? latitude =
                    reader["Latitude"] == DBNull.Value
                        ? null
                        : Convert.ToDouble(
                            reader["Latitude"]);

                double? longitude =
                    reader["Longitude"] == DBNull.Value
                        ? null
                        : Convert.ToDouble(
                            reader["Longitude"]);

                string? notes =
                    reader["Notes"] == DBNull.Value
                        ? null
                        : reader["Notes"]?.ToString();

                return Ok(new
                {
                    success = true,
                    message = "Login successful.",
                    customer = new
                    {
                        customerID = customerId,
                        fullName = fullName,
                        phone = phone,
                        address = address,
                        latitude = latitude,
                        longitude = longitude,
                        notes = notes
                    }
                });
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"Customer Login Error: {ex.Message}");
#endif

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Login failed."
                    });
            }
        }


        // =========================================================
        // GET PROFILE
        // =========================================================

        [HttpGet("{customerId:int}")]
        public async Task<IActionResult> GetProfile(
            int customerId)
        {
            try
            {
                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string sql = @"
                    SELECT
                        CustomerID,
                        FullName,
                        Phone,
                        Address,
                        Latitude,
                        Longitude,
                        Notes,
                        IsActive
                    FROM customers
                    WHERE CustomerID = @CustomerID
                    LIMIT 1;";

                using MySqlCommand command =
                    new MySqlCommand(sql, connection);

                command.Parameters.AddWithValue(
                    "@CustomerID",
                    customerId);

                using MySqlDataReader reader =
                    await command.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Customer not found."
                    });
                }

                return Ok(new
                {
                    success = true,
                    customer = new
                    {
                        customerID =
                            Convert.ToInt32(
                                reader["CustomerID"]),

                        fullName =
                            reader["FullName"]?.ToString()
                            ?? "",

                        phone =
                            reader["Phone"]?.ToString()
                            ?? "",

                        address =
                            reader["Address"] == DBNull.Value
                                ? null
                                : reader["Address"]?.ToString(),

                        latitude =
                            reader["Latitude"] == DBNull.Value
                                ?(double?) null
                                : Convert.ToDouble(
                                    reader["Latitude"]),

                        longitude =
                            reader["Longitude"] == DBNull.Value
                                ?(double?) null
                                : Convert.ToDouble(
                                    reader["Longitude"]),

                        notes =
                            reader["Notes"] == DBNull.Value
                                ? null
                                : reader["Notes"]?.ToString(),

                        isActive =
                            Convert.ToBoolean(
                                reader["IsActive"])
                    }
                });
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"Customer Profile Error: {ex.Message}");
#endif

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Could not load profile."
                    });
            }
        }


        // =========================================================
        // UPDATE PROFILE
        // =========================================================

        [HttpPut("{customerId:int}")]
        public async Task<IActionResult> UpdateProfile(
            int customerId,
            [FromBody] UpdateCustomerRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.FullName))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Full name is required."
                    });
                }

                using MySqlConnection connection =
                    DatabaseManager.GetConnection();

                await connection.OpenAsync();

                const string sql = @"
                    UPDATE customers
                    SET
                        FullName = @FullName,
                        Address = @Address,
                        Latitude = @Latitude,
                        Longitude = @Longitude,
                        Notes = @Notes
                    WHERE CustomerID = @CustomerID;";

                using MySqlCommand command =
                    new MySqlCommand(sql, connection);

                command.Parameters.AddWithValue(
                    "@FullName",
                    request.FullName.Trim());

                command.Parameters.AddWithValue(
                    "@Address",
                    string.IsNullOrWhiteSpace(request.Address)
                        ? DBNull.Value
                        : request.Address.Trim());

                command.Parameters.AddWithValue(
                    "@Latitude",
                    request.Latitude.HasValue
                        ? request.Latitude.Value
                        : DBNull.Value);

                command.Parameters.AddWithValue(
                    "@Longitude",
                    request.Longitude.HasValue
                        ? request.Longitude.Value
                        : DBNull.Value);

                command.Parameters.AddWithValue(
                    "@Notes",
                    string.IsNullOrWhiteSpace(request.Notes)
                        ? DBNull.Value
                        : request.Notes.Trim());

                command.Parameters.AddWithValue(
                    "@CustomerID",
                    customerId);

                int rows =
                    await command.ExecuteNonQueryAsync();

                if (rows == 0)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Customer not found."
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "Profile updated successfully."
                });
            }
            catch (Exception ex)
            {
#if DEBUG
                System.Diagnostics.Debug.WriteLine(
                    $"Customer Update Error: {ex.Message}");
#endif

                return StatusCode(
                    500,
                    new
                    {
                        success = false,
                        message = "Profile update failed."
                    });
            }
        }
    }


    // =============================================================
    // REQUEST MODELS
    // =============================================================

    public class RegisterRequest
    {
        public string FullName { get; set; } = "";

        public string Phone { get; set; } = "";

        public string Password { get; set; } = "";

        public string? Address { get; set; }

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        public string? Notes { get; set; }
    }


    public class LoginRequest
    {
        public string Phone { get; set; } = "";

        public string Password { get; set; } = "";
    }


    public class UpdateCustomerRequest
    {
        public string FullName { get; set; } = "";

        public string? Address { get; set; }

        public double? Latitude { get; set; }

        public double? Longitude { get; set; }

        public string? Notes { get; set; }
    }


    // =============================================================
    // PASSWORD HELPER
    // =============================================================

    public static class PasswordHelper
    {
        private const int SaltSize = 16;

        private const int HashSize = 32;

        private const int Iterations = 600000;


        public static string HashPassword(
            string password)
        {
            byte[] salt =
                RandomNumberGenerator.GetBytes(
                    SaltSize);

            byte[] hash =
                Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    Iterations,
                    HashAlgorithmName.SHA256,
                    HashSize);

            return
                $"PBKDF2${Iterations}$" +
                $"{Convert.ToBase64String(salt)}$" +
                $"{Convert.ToBase64String(hash)}";
        }


        public static bool VerifyPassword(
            string password,
            string storedHash)
        {
            try
            {
                string[] parts =
                    storedHash.Split('$');

                if (parts.Length != 4)
                    return false;

                if (parts[0] != "PBKDF2")
                    return false;

                int iterations =
                    int.Parse(parts[1]);

                byte[] salt =
                    Convert.FromBase64String(
                        parts[2]);

                byte[] expectedHash =
                    Convert.FromBase64String(
                        parts[3]);

                byte[] actualHash =
                    Rfc2898DeriveBytes.Pbkdf2(
                        password,
                        salt,
                        iterations,
                        HashAlgorithmName.SHA256,
                        expectedHash.Length);

                return CryptographicOperations.FixedTimeEquals(
                    actualHash,
                    expectedHash);
            }
            catch
            {
                return false;
            }
        }
    }
}