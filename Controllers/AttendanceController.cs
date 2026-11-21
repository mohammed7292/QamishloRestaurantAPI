using Microsoft.AspNetCore.Mvc;
using MySqlConnector;
using QamishloRestaurantAPI.Database;
using QamishloRestaurantAPI.Security;

namespace QamishloRestaurantAPI.Controllers;

[ApiController]
[Route("api/attendance")]
public sealed class AttendanceController : ControllerBase
{
    private static DateTime BaghdadNow() => DateTime.UtcNow.AddHours(3);

    [HttpGet("shifts")]
    public async Task<IActionResult> GetShifts()
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        try
        {
            const string syncSql = @"
                INSERT INTO restaurantemployees (FullName, JobTitleEN, JobTitleAR, JobTitleKU, DescriptionEN, DescriptionAR, DescriptionKU, IsActive, ShowInCustomerApp)
                SELECT dw.FullName, 'Delivery Driver', 'عامل توصيل', 'شۆفێری گەیاندن', 'Qamishlo Delivery Team', 'فريق توصيل مطعم قامشلو', 'تیمی گەیاندنی خواردنگەی قامشلو', dw.IsActive, 1
                FROM deliveryworkers dw
                WHERE NOT EXISTS (SELECT 1 FROM restaurantemployees re WHERE LOWER(TRIM(re.FullName)) = LOWER(TRIM(dw.FullName)));";
            await using var sc = new MySqlCommand(syncSql, c);
            await sc.ExecuteNonQueryAsync();
        }
        catch { }

        const string sql = @"
            SELECT e.EmployeeID, e.FullName, COALESCE(e.JobTitleAR, e.JobTitleEN, 'موظف') AS JobTitle,
                   COALESCE(s.StartTime, '09:00') AS StartTime,
                   COALESCE(s.EndTime, '21:00') AS EndTime,
                   COALESCE(s.GraceMinutes, 15) AS GraceMinutes,
                   COALESCE(s.WorkDays, 'يومياً') AS WorkDays
            FROM restaurantemployees e
            LEFT JOIN employeeshifts s ON s.EmployeeID = e.EmployeeID
            WHERE e.IsActive = 1
            ORDER BY e.FullName ASC;";

        var shifts = new List<object>();
        await using var cmd = new MySqlCommand(sql, c);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            shifts.Add(new
            {
                employeeID = r.GetInt32("EmployeeID"),
                fullName = r.GetString("FullName"),
                jobTitle = r.GetString("JobTitle"),
                startTime = r.GetString("StartTime"),
                endTime = r.GetString("EndTime"),
                graceMinutes = r.GetInt32("GraceMinutes"),
                workDays = r.GetString("WorkDays")
            });
        }

        return Ok(new { success = true, shifts });
    }

    [HttpPost("shifts")]
    public async Task<IActionResult> SaveShift([FromBody] ShiftConfigRequest req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (req == null || req.EmployeeID <= 0 || string.IsNullOrWhiteSpace(req.StartTime) || string.IsNullOrWhiteSpace(req.EndTime))
            return BadRequest(new { success = false, message = "يرجى تحديد الموظف ووقت البداية والنهاية." });

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();
        const string sql = @"
            INSERT INTO employeeshifts (EmployeeID, EmployeeName, StartTime, EndTime, GraceMinutes, WorkDays, UpdatedAt)
            VALUES (@eid, @name, @start, @end, @grace, @days, @now)
            ON DUPLICATE KEY UPDATE
                EmployeeName = VALUES(EmployeeName),
                StartTime = VALUES(StartTime),
                EndTime = VALUES(EndTime),
                GraceMinutes = VALUES(GraceMinutes),
                WorkDays = VALUES(WorkDays),
                UpdatedAt = VALUES(UpdatedAt);";

        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@eid", req.EmployeeID);
        cmd.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(req.EmployeeName) ? $"موظف #{req.EmployeeID}" : req.EmployeeName.Trim());
        cmd.Parameters.AddWithValue("@start", req.StartTime.Trim());
        cmd.Parameters.AddWithValue("@end", req.EndTime.Trim());
        cmd.Parameters.AddWithValue("@grace", Math.Clamp(req.GraceMinutes, 0, 120));
        cmd.Parameters.AddWithValue("@days", string.IsNullOrWhiteSpace(req.WorkDays) ? "يومياً" : req.WorkDays.Trim());
        cmd.Parameters.AddWithValue("@now", BaghdadNow());
        await cmd.ExecuteNonQueryAsync();

        return Ok(new { success = true, message = "تم حفظ جدول دوام الموظف بنجاح." });
    }

    [HttpGet("logs")]
    public async Task<IActionResult> GetLogs([FromQuery] string? date = null)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });

        DateTime workDate = DateTime.TryParse(date, out var parsed) ? parsed.Date : BaghdadNow().Date;

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        const string sql = @"
            SELECT e.EmployeeID, e.FullName, COALESCE(e.JobTitleAR, 'موظف') AS JobTitle,
                   COALESCE(s.StartTime, '09:00') AS ScheduledStart,
                   COALESCE(s.EndTime, '21:00') AS ScheduledEnd,
                   a.AttendanceID, a.CheckInAt, a.CheckOutAt,
                   COALESCE(a.LateMinutes, 0) AS LateMinutes,
                   COALESCE(a.WorkedMinutes, 0) AS WorkedMinutes,
                   COALESCE(a.Status, 'Absent') AS Status,
                   COALESCE(a.VerificationMethod, '') AS VerificationMethod,
                   COALESCE(a.Notes, '') AS Notes
            FROM restaurantemployees e
            LEFT JOIN employeeshifts s ON s.EmployeeID = e.EmployeeID
            LEFT JOIN employeeattendance a ON a.EmployeeID = e.EmployeeID AND a.WorkDate = @wdate
            WHERE e.IsActive = 1
            ORDER BY e.FullName ASC;";

        var rows = new List<object>();
        int presentCount = 0, lateCount = 0, absentCount = 0;

        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@wdate", workDate);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            string st = r.GetString("Status");
            if (st == "Present") presentCount++;
            else if (st == "Late") lateCount++;
            else absentCount++;

            rows.Add(new
            {
                attendanceID = r.IsDBNull(r.GetOrdinal("AttendanceID")) ? 0 : r.GetInt32("AttendanceID"),
                employeeID = r.GetInt32("EmployeeID"),
                fullName = r.GetString("FullName"),
                jobTitle = r.GetString("JobTitle"),
                scheduledStart = r.GetString("ScheduledStart"),
                scheduledEnd = r.GetString("ScheduledEnd"),
                checkInAt = r.IsDBNull(r.GetOrdinal("CheckInAt")) ? (DateTime?)null : r.GetDateTime("CheckInAt"),
                checkOutAt = r.IsDBNull(r.GetOrdinal("CheckOutAt")) ? (DateTime?)null : r.GetDateTime("CheckOutAt"),
                lateMinutes = r.GetInt32("LateMinutes"),
                workedMinutes = r.GetInt32("WorkedMinutes"),
                status = st,
                verificationMethod = r.GetString("VerificationMethod"),
                notes = r.GetString("Notes")
            });
        }

        return Ok(new
        {
            success = true,
            workDate = workDate.ToString("yyyy-MM-dd"),
            summary = new { presentCount, lateCount, absentCount, totalStaff = rows.Count },
            logs = rows
        });
    }

    [HttpPost("checkin")]
    public async Task<IActionResult> CheckIn([FromBody] AttendancePunchRequest req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (req == null || req.EmployeeID <= 0)
            return BadRequest(new { success = false, message = "يرجى اختيار الموظف." });
        if (!req.BiometricVerified)
            return BadRequest(new { success = false, message = "يجب التحقق بالبصمة أولاً لتسجيل الحضور." });

        DateTime now = BaghdadNow();
        DateTime workDate = now.Date;

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        string empName = req.EmployeeName?.Trim() ?? "";
        string schedStart = "09:00";
        string schedEnd = "21:00";
        int grace = 15;

        await using (var info = new MySqlCommand(@"
            SELECT e.FullName, COALESCE(s.StartTime, '09:00') AS StartTime, COALESCE(s.EndTime, '21:00') AS EndTime, COALESCE(s.GraceMinutes, 15) AS GraceMinutes
            FROM restaurantemployees e
            LEFT JOIN employeeshifts s ON s.EmployeeID = e.EmployeeID
            WHERE e.EmployeeID = @eid LIMIT 1;", c))
        {
            info.Parameters.AddWithValue("@eid", req.EmployeeID);
            await using var r = await info.ExecuteReaderAsync();
            if (await r.ReadAsync())
            {
                empName = r.GetString("FullName");
                schedStart = r.GetString("StartTime");
                schedEnd = r.GetString("EndTime");
                grace = r.GetInt32("GraceMinutes");
            }
        }

        int lateMinutes = 0;
        if (TimeSpan.TryParse(schedStart, out var startTs))
        {
            var scheduledDt = workDate.Add(startTs);
            var diff = (int)Math.Floor((now - scheduledDt).TotalMinutes);
            if (diff > grace)
                lateMinutes = diff;
        }
        string status = lateMinutes > 0 ? "Late" : "Present";

        const string sql = @"
            INSERT INTO employeeattendance (EmployeeID, EmployeeName, WorkDate, CheckInAt, ScheduledStart, ScheduledEnd, LateMinutes, Status, VerificationMethod, DeviceInfo, Notes, CreatedAt)
            VALUES (@eid, @name, @wdate, @now, @sstart, @send, @late, @st, @method, @dev, @notes, @now)
            ON DUPLICATE KEY UPDATE
                CheckInAt = COALESCE(CheckInAt, VALUES(CheckInAt)),
                LateMinutes = VALUES(LateMinutes),
                Status = VALUES(Status),
                VerificationMethod = VALUES(VerificationMethod),
                DeviceInfo = VALUES(DeviceInfo);";

        await using var cmd = new MySqlCommand(sql, c);
        cmd.Parameters.AddWithValue("@eid", req.EmployeeID);
        cmd.Parameters.AddWithValue("@name", empName);
        cmd.Parameters.AddWithValue("@wdate", workDate);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@sstart", schedStart);
        cmd.Parameters.AddWithValue("@send", schedEnd);
        cmd.Parameters.AddWithValue("@late", lateMinutes);
        cmd.Parameters.AddWithValue("@st", status);
        cmd.Parameters.AddWithValue("@method", string.IsNullOrWhiteSpace(req.VerificationMethod) ? "Fingerprint" : req.VerificationMethod.Trim());
        cmd.Parameters.AddWithValue("@dev", string.IsNullOrWhiteSpace(req.DeviceInfo) ? DBNull.Value : req.DeviceInfo.Trim());
        cmd.Parameters.AddWithValue("@notes", string.IsNullOrWhiteSpace(req.Notes) ? DBNull.Value : req.Notes.Trim());
        await cmd.ExecuteNonQueryAsync();

        string msg = status == "Late"
            ? $"تم تسجيل حضور {empName} بالبصمة (متأخر {lateMinutes} دقيقة)."
            : $"تم تسجيل حضور {empName} بالبصمة في الوقت المحدد.";
        return Ok(new { success = true, status, lateMinutes, checkInAt = now, message = msg });
    }

    [HttpPost("checkout")]
    public async Task<IActionResult> CheckOut([FromBody] AttendancePunchRequest req)
    {
        if (!await ManagerCredentialValidator.IsManagerAsync(Request))
            return Unauthorized(new { success = false, message = "صلاحية المدير مطلوبة." });
        if (req == null || req.EmployeeID <= 0)
            return BadRequest(new { success = false, message = "يرجى اختيار الموظف." });
        if (!req.BiometricVerified)
            return BadRequest(new { success = false, message = "يجب التحقق بالبصمة أولاً لتسجيل الانصراف." });

        DateTime now = BaghdadNow();
        DateTime workDate = now.Date;

        await using var c = DatabaseManager.GetConnection();
        await c.OpenAsync();

        DateTime? checkIn = null;
        await using (var sel = new MySqlCommand("SELECT CheckInAt FROM employeeattendance WHERE EmployeeID=@eid AND WorkDate=@wdate LIMIT 1;", c))
        {
            sel.Parameters.AddWithValue("@eid", req.EmployeeID);
            sel.Parameters.AddWithValue("@wdate", workDate);
            var obj = await sel.ExecuteScalarAsync();
            if (obj != null && obj != DBNull.Value)
                checkIn = Convert.ToDateTime(obj);
        }

        if (!checkIn.HasValue)
            return BadRequest(new { success = false, message = "لم يتم تسجيل حضور لهذا الموظف اليوم بعد." });

        int worked = Math.Max(0, (int)Math.Round((now - checkIn.Value).TotalMinutes));
        await using var upd = new MySqlCommand(
            "UPDATE employeeattendance SET CheckOutAt=@now, WorkedMinutes=@worked WHERE EmployeeID=@eid AND WorkDate=@wdate;", c);
        upd.Parameters.AddWithValue("@now", now);
        upd.Parameters.AddWithValue("@worked", worked);
        upd.Parameters.AddWithValue("@eid", req.EmployeeID);
        upd.Parameters.AddWithValue("@wdate", workDate);
        await upd.ExecuteNonQueryAsync();

        return Ok(new { success = true, checkOutAt = now, workedMinutes = worked, message = $"تم تسجيل الانصراف بالبصمة (مدة العمل: {worked / 60} س {worked % 60} د)." });
    }

    public sealed class ShiftConfigRequest
    {
        public int EmployeeID { get; set; }
        public string? EmployeeName { get; set; }
        public string StartTime { get; set; } = "09:00";
        public string EndTime { get; set; } = "21:00";
        public int GraceMinutes { get; set; } = 15;
        public string? WorkDays { get; set; } = "يومياً";
    }

    public sealed class AttendancePunchRequest
    {
        public int EmployeeID { get; set; }
        public string? EmployeeName { get; set; }
        public bool BiometricVerified { get; set; }
        public string? VerificationMethod { get; set; } = "Fingerprint";
        public string? DeviceInfo { get; set; }
        public string? Notes { get; set; }
    }
}
