using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
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

                string cleanPhone = NormalizePhoneDigits(request.Phone);
                const string checkSql = @"
                    SELECT CustomerID
                    FROM customers
                    WHERE Phone = @Phone
                       OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(IFNULL(Phone,''),' ',''),'-',''),'+964','0'),'00964','0'),'(','') = @CleanPhone
                    LIMIT 1;";

                using (MySqlCommand checkCommand =
                       new MySqlCommand(checkSql, connection))
                {
                    checkCommand.Parameters.AddWithValue("@Phone", request.Phone.Trim());
                    checkCommand.Parameters.AddWithValue("@CleanPhone", cleanPhone);

                    object? existing = await checkCommand.ExecuteScalarAsync();
                    if (existing != null)
                    {
                        return Conflict(new
                        {
                            success = false,
                            message = "رقم الهاتف هذا مسجّل مسبقاً! لا يمكن إنشاء أكثر من حساب بنفس الرقم، يرجى تسجيل الدخول."
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

                    // Verify Email OTP when SMTP is configured (Env OR restaurantsettings)
                    var (smtpConfigured, _) = await ResolveSmtpCredentialsAsync(connection);
                    if (!string.IsNullOrWhiteSpace(smtpConfigured))
                    {
                        string reqEmail = request.Email?.Trim().ToLowerInvariant() ?? "";
                        string reqOtp = request.OtpCode?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(reqEmail) || string.IsNullOrWhiteSpace(reqOtp))
                            return BadRequest(new { success = false, message = "يرجى إدخال البريد الإلكتروني وكود التحقق المرسل إليه أولاً." });

                        const string chkOtpSql = @"
                            SELECT OtpID FROM customerotps
                            WHERE Email = @email AND OtpCode = @code AND IsUsed = 0 AND ExpiresAt >= UTC_TIMESTAMP()
                            ORDER BY OtpID DESC LIMIT 1;";
                        await using var chkOtpCmd = new MySqlCommand(chkOtpSql, connection);
                        chkOtpCmd.Parameters.AddWithValue("@email", reqEmail);
                        chkOtpCmd.Parameters.AddWithValue("@code", reqOtp);
                        object? otpIdObj = await chkOtpCmd.ExecuteScalarAsync();
                        if (otpIdObj == null || otpIdObj == DBNull.Value)
                            return BadRequest(new { success = false, message = "كود التحقق غير صحيح أو منتهي الصلاحية. اطلب كوداً جديداً." });

                        await using var markCmd = new MySqlCommand("UPDATE customerotps SET IsUsed = 1 WHERE OtpID = @oid;", connection);
                        markCmd.Parameters.AddWithValue("@oid", Convert.ToInt32(otpIdObj));
                        await markCmd.ExecuteNonQueryAsync();
                    }

                    string insertSql = @"
                    INSERT INTO customers
                    (
                        FullName,
                        Phone,
                        BirthDate,
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
                        @BirthDate,
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
                var parsedBd = ParseBirthDate(request.BirthDate);
                insertCommand.Parameters.AddWithValue("@BirthDate", parsedBd.HasValue ? parsedBd.Value : DBNull.Value);

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
                        birthDate = parsedBd?.ToString("yyyy-MM-dd"),
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
                        BirthDate,
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
                string? birthDate = null;
                try { if (reader["BirthDate"] != DBNull.Value) birthDate = Convert.ToDateTime(reader["BirthDate"]).ToString("yyyy-MM-dd"); } catch { }

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
                        birthDate = birthDate,
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



        // POST: api/customers/send-otp (Free Gmail SMTP Email OTP)
        [HttpPost("send-otp")]
        public async Task<IActionResult> SendEmailOtp([FromBody] SendEmailOtpRequest? request)
        {
            string email = request?.Email?.Trim().ToLowerInvariant() ?? "";
            if (string.IsNullOrWhiteSpace(email) || !email.Contains("@") || !email.Contains("."))
                return BadRequest(new { success = false, message = "يرجى إدخال بريد إلكتروني صحيح." });

            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            // Check duplicate phone early before sending OTP
            if (!string.IsNullOrWhiteSpace(request?.Phone))
            {
                string cp = NormalizePhoneDigits(request.Phone);
                await using var dupCmd = new MySqlCommand(@"
                    SELECT CustomerID FROM customers
                    WHERE Phone = @Phone
                       OR REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(IFNULL(Phone,''),' ',''),'-',''),'+964','0'),'00964','0'),'(','') = @CleanPhone
                    LIMIT 1;", connection);
                dupCmd.Parameters.AddWithValue("@Phone", request.Phone.Trim());
                dupCmd.Parameters.AddWithValue("@CleanPhone", cp);
                if (await dupCmd.ExecuteScalarAsync() != null)
                    return Conflict(new { success = false, message = "رقم الهاتف هذا مسجّل مسبقاً! يرجى تسجيل الدخول بدلاً من إنشاء حساب جديد." });
            }

            var (smtpUser, smtpPass) = await ResolveSmtpCredentialsAsync(connection);
            string smtpHost = Environment.GetEnvironmentVariable("SMTP_HOST")?.Trim() ?? "smtp.gmail.com";
            int smtpPort = int.TryParse(Environment.GetEnvironmentVariable("SMTP_PORT"), out var p) ? p : 587;

            if (string.IsNullOrWhiteSpace(smtpUser) || string.IsNullOrWhiteSpace(smtpPass))
                return StatusCode(503, new { success = false, message = "خدمة إرسال كود الإيميل غير مفعّلة بعد. يرجى إدخال إيميل Gmail وكلمة مرور التطبيق من تطبيق المطعم (الإعدادات -> إعدادات إيميل OTP) ثم الحفظ." });

            await using (var ensureOtp = new MySqlCommand(@"
                CREATE TABLE IF NOT EXISTS customerotps (
                  OtpID INT AUTO_INCREMENT PRIMARY KEY,
                  Email VARCHAR(190) NOT NULL,
                  Phone VARCHAR(40) NULL,
                  OtpCode VARCHAR(12) NOT NULL,
                  ExpiresAt DATETIME NOT NULL,
                  IsUsed TINYINT(1) NOT NULL DEFAULT 0,
                  CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;", connection))
            {
                await ensureOtp.ExecuteNonQueryAsync();
            }

            string code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            DateTime nowUtc = DateTime.UtcNow;
            DateTime expUtc = nowUtc.AddMinutes(10);
            await using (var ins = new MySqlCommand(@"
                INSERT INTO customerotps (Email, Phone, OtpCode, ExpiresAt, IsUsed, CreatedAt)
                VALUES (@email, @phone, @code, @exp, 0, @now);", connection))
            {
                ins.Parameters.AddWithValue("@email", email);
                ins.Parameters.AddWithValue("@phone", string.IsNullOrWhiteSpace(request?.Phone) ? DBNull.Value : request!.Phone!.Trim());
                ins.Parameters.AddWithValue("@code", code);
                ins.Parameters.AddWithValue("@exp", expUtc);
                ins.Parameters.AddWithValue("@now", nowUtc);
                await ins.ExecuteNonQueryAsync();
            }

            string htmlBody = $@"
                <div dir='rtl' style='font-family:Tahoma,Arial,sans-serif;background:#14110F;color:#ffffff;padding:24px;border-radius:14px;max-width:460px;margin:auto;border:1px solid #F37A1F'>
                  <h2 style='color:#F37A1F;margin-top:0'>🍔 مطعم قامشلو — Qamishlo Fast Food</h2>
                  <p style='font-size:15px'>مرحباً، استخدم كود التحقق التالي لإكمال إنشاء حسابك بأمان:</p>
                  <div style='background:#25201D;border:2px dashed #F37A1F;border-radius:12px;padding:16px;text-align:center;font-size:32px;font-weight:900;letter-spacing:8px;color:#FFD54F;margin:18px 0'>{code}</div>
                  <p style='font-size:12px;color:#B9B0A8'>الكود صالح لمدة 10 دقائق فقط. إذا لم تطلب هذا الكود يمكنك تجاهل الرسالة.</p>
                </div>";
            string mailSubject = $"كود التحقق الخاص بك: {code} — مطعم قامشلو";

            try
            {
                // Mode 1: Free Google Apps Script Gmail Bridge over HTTPS Port 443 (NEVER blocked by cloud hosts!)
                if (smtpPass.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var payload = System.Text.Json.JsonSerializer.Serialize(new { to = email, subject = mailSubject, html = htmlBody });
                    using var resp = await http.PostAsync(smtpPass, new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
                    if (!resp.IsSuccessStatusCode)
                    {
                        string errBody = await resp.Content.ReadAsStringAsync();
                        return StatusCode(500, new { success = false, message = $"تعذر الإرسال عبر جسر Gmail: {(int)resp.StatusCode}" });
                    }
                    return Ok(new { success = true, message = "تم إرسال كود التحقق إلى بريدك الإلكتروني بنجاح." });
                }

                // Mode 2: Free Brevo HTTP API over HTTPS Port 443 (300 free emails/day)
                if (smtpPass.StartsWith("xkeysib-", StringComparison.OrdinalIgnoreCase))
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    http.DefaultRequestHeaders.Add("api-key", smtpPass);
                    var payload = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        sender = new { name = "مطعم قامشلو", email = smtpUser },
                        to = new[] { new { email = email } },
                        subject = mailSubject,
                        htmlContent = htmlBody
                    });
                    using var resp = await http.PostAsync("https://api.brevo.com/v3/smtp/email", new StringContent(payload, System.Text.Encoding.UTF8, "application/json"));
                    if (!resp.IsSuccessStatusCode)
                        return StatusCode(500, new { success = false, message = $"تعذر الإرسال عبر Brevo: {(int)resp.StatusCode}" });
                    return Ok(new { success = true, message = "تم إرسال كود التحقق إلى بريدك الإلكتروني بنجاح." });
                }

                // Mode 3: Direct SMTP fallback (works if host allows outbound port 587)
                using var mail = new MailMessage();
                mail.From = new MailAddress(smtpUser, "مطعم قامشلو — Qamishlo Fast Food");
                mail.To.Add(email);
                mail.Subject = mailSubject;
                mail.IsBodyHtml = true;
                mail.Body = htmlBody;

                using var client = new SmtpClient(smtpHost, smtpPort)
                {
                    EnableSsl = true,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(smtpUser, smtpPass),
                    Timeout = 9000
                };
                await client.SendMailAsync(mail);
                return Ok(new { success = true, message = "تم إرسال كود التحقق إلى بريدك الإلكتروني بنجاح." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"منفذ SMTP (587) محجوب على الاستضافة السحابية. ضع رابط Google Apps Script المجاني (https://...) في إعدادات التطبيق ليعمل الإرسال فوراً. ({ex.Message})" });
            }
        }

        public sealed class SendEmailOtpRequest
        {
            public string Email { get; set; } = "";
            public string? Phone { get; set; }
        }

        // GET: api/customers and api/customer
        [HttpGet]
        [HttpGet("/api/customer")]
        public async Task<IActionResult> GetAllCustomers()
        {
            var customers = new List<object>();
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();

            const string sql = @"
                SELECT c.CustomerID, c.FullName, c.Phone, c.Address, c.Latitude, c.Longitude, c.Notes, c.IsActive, c.CreatedAt,
                       (SELECT COUNT(*) FROM orders o WHERE o.CustomerID = c.CustomerID AND o.Status NOT IN ('Cancelled','Rejected')) AS OrderCount
                FROM customers c
                WHERE IFNULL(c.IsActive, 1) = 1
                  AND c.PasswordHash IS NOT NULL AND c.PasswordHash <> ''
                  AND IFNULL(c.FullName, '') NOT IN ('Walk-in Customer', 'زبون مباشر', 'زبون داخل المطعم')
                ORDER BY c.CustomerID DESC
                LIMIT 1000;";
            await using var cmd = new MySqlCommand(sql, connection);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                customers.Add(new
                {
                    customerID = r.GetInt32("CustomerID"),
                    fullName = r.IsDBNull(r.GetOrdinal("FullName")) ? "" : r.GetString("FullName"),
                    phone = r.IsDBNull(r.GetOrdinal("Phone")) ? "" : r.GetString("Phone"),
                    address = r.IsDBNull(r.GetOrdinal("Address")) ? "" : r.GetString("Address"),
                    latitude = r.IsDBNull(r.GetOrdinal("Latitude")) ? (double?)null : Convert.ToDouble(r["Latitude"]),
                    longitude = r.IsDBNull(r.GetOrdinal("Longitude")) ? (double?)null : Convert.ToDouble(r["Longitude"]),
                    notes = r.IsDBNull(r.GetOrdinal("Notes")) ? "" : r.GetString("Notes"),
                    orderCount = r.IsDBNull(r.GetOrdinal("OrderCount")) ? 0 : Convert.ToInt32(r["OrderCount"]),
                    createdAt = r.IsDBNull(r.GetOrdinal("CreatedAt")) ? DateTime.UtcNow : r.GetDateTime("CreatedAt")
                });
            }

            return Ok(new { success = true, customers });
        }


        private static DateTime? ParseBirthDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (DateTime.TryParse(raw.Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
                return dt.Date;
            return null;
        }

        [HttpGet("birthdays")]
        public async Task<IActionResult> GetBirthdays()
        {
            await using var connection = DatabaseManager.GetConnection();
            await connection.OpenAsync();
            var baghdadNow = DateTime.UtcNow.AddHours(3);
            var todayList = new List<object>();
            var monthList = new List<object>();
            try
            {
                const string sql = @"
                    SELECT CustomerID, FullName, Phone, BirthDate
                    FROM customers
                    WHERE IFNULL(IsActive, 1) = 1
                      AND BirthDate IS NOT NULL
                      AND MONTH(BirthDate) = @m
                    ORDER BY DAY(BirthDate) ASC, FullName ASC
                    LIMIT 100;";
                await using var cmd = new MySqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("@m", baghdadNow.Month);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var bd = r.GetDateTime("BirthDate");
                    var item = new
                    {
                        customerID = r.GetInt32("CustomerID"),
                        fullName = r.IsDBNull(r.GetOrdinal("FullName")) ? "زبون" : r.GetString("FullName"),
                        phone = r.IsDBNull(r.GetOrdinal("Phone")) ? "" : r.GetString("Phone"),
                        birthDate = bd.ToString("yyyy-MM-dd"),
                        day = bd.Day,
                        month = bd.Month,
                        isToday = bd.Month == baghdadNow.Month && bd.Day == baghdadNow.Day
                    };
                    monthList.Add(item);
                    if (bd.Month == baghdadNow.Month && bd.Day == baghdadNow.Day)
                        todayList.Add(item);
                }
            }
            catch { }
            return Ok(new
            {
                success = true,
                month = baghdadNow.Month,
                today = baghdadNow.ToString("yyyy-MM-dd"),
                todayCount = todayList.Count,
                monthCount = monthList.Count,
                todayBirthdays = todayList,
                monthBirthdays = monthList
            });
        }

        private static string NormalizePhoneDigits(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (char ch in raw)
                if (char.IsDigit(ch)) sb.Append(ch);
            string d = sb.ToString();
            if (d.StartsWith("00964")) d = "0" + d.Substring(5);
            else if (d.StartsWith("964") && d.Length >= 12) d = "0" + d.Substring(3);
            return d;
        }

        private static async Task<(string Email, string AppPassword)> ResolveSmtpCredentialsAsync(MySqlConnection connection)
        {
            string envUser = Environment.GetEnvironmentVariable("SMTP_EMAIL")?.Trim() ?? "";
            string envPass = Environment.GetEnvironmentVariable("SMTP_APP_PASSWORD")?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(envUser) && !string.IsNullOrWhiteSpace(envPass))
                return (envUser, envPass);
            try
            {
                await using var cmd = new MySqlCommand("SELECT SmtpEmail, SmtpAppPassword FROM restaurantsettings ORDER BY SettingID LIMIT 1;", connection);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    string dbUser = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    string dbPass = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    if (!string.IsNullOrWhiteSpace(dbUser) && !string.IsNullOrWhiteSpace(dbPass))
                        return (dbUser, dbPass);
                }
            }
            catch { }
            return ("", "");
        }

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
                        BirthDate,
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
                        birthDate = reader["BirthDate"] == DBNull.Value ? null : Convert.ToDateTime(reader["BirthDate"]).ToString("yyyy-MM-dd"),

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
                        BirthDate = COALESCE(BirthDate, @BirthDate),
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
                var updBd = ParseBirthDate(request.BirthDate);
                command.Parameters.AddWithValue("@BirthDate", updBd.HasValue ? updBd.Value : DBNull.Value);

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

        public string? Email { get; set; }
        public string? BirthDate { get; set; }
        public string? OtpCode { get; set; }
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
        public string? BirthDate { get; set; }

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