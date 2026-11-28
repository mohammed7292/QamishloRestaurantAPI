using MySqlConnector;
using QamishloRestaurantAPI.Controllers;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Services;

/// <summary>
/// V27.1 Automatic Monthly Reset Service (Baghdad Business Time):
/// At the start of every new calendar month (yyyy-MM), automatically clears all transactional data:
/// orders, orderdetails, expenses, reports/ledger, cash sessions, employee & order reviews,
/// Qamishlo Coins balances & transactions, notifications, attendance, and audit logs —
/// while 100% preserving core master data: customers, employees, users, delivery workers,
/// products, categories, delivery zones, settings, rewards, coupons, and debts.
/// </summary>
public sealed class MonthlyResetBackgroundService : BackgroundService
{
    private readonly ILogger<MonthlyResetBackgroundService> _logger;

    private static readonly string[] MonthlyResetTables =
    {
        "orderdetails",
        "kitchenordertracking",
        "offlinesyncorders",
        "orders",
        "expenses",
        "cashmovements",
        "cashsessions",
        "financialledger",
        "inventoryorderdeductions",
        "stockmovements",
        "employeereviews",
        "orderreviews",
        "loyaltytransactions",
        "customerloyalty",
        "customernotifications",
        "broadcastnotifications",
        "marketingcampaigns",
        "customerotps",
        "employeeattendance",
        "employeeshifts",
        "auditlogs"
    };

    public MonthlyResetBackgroundService(ILogger<MonthlyResetBackgroundService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly after startup so DatabaseInitializer completes first
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndRunMonthlyResetIfDueAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Monthly reset check failed.");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch { break; }
        }
    }

    public static async Task<(bool WasReset, string MonthKey, int ClearedTables)> CheckAndRunMonthlyResetIfDueAsync(CancellationToken ct = default)
    {
        DateTime businessDate = ExpensesController.BusinessDate(ExpensesController.BaghdadNow());
        string currentYM = businessDate.ToString("yyyy-MM");

        await using var conn = DatabaseManager.GetConnection();
        await conn.OpenAsync(ct);

        string? lastYM = null;
        await using (var cmd = new MySqlCommand("SELECT LastMonthlyResetYM FROM restaurantsettings ORDER BY SettingID LIMIT 1;", conn))
        {
            object? val = await cmd.ExecuteScalarAsync(ct);
            if (val != null && val != DBNull.Value)
                lastYM = val.ToString()?.Trim();
        }

        // First-time initialization: record current month WITHOUT wiping ongoing month's data
        if (string.IsNullOrWhiteSpace(lastYM))
        {
            await using var initCmd = new MySqlCommand("UPDATE restaurantsettings SET LastMonthlyResetYM = @ym;", conn);
            initCmd.Parameters.AddWithValue("@ym", currentYM);
            await initCmd.ExecuteNonQueryAsync(ct);
            return (false, currentYM, 0);
        }

        if (string.Equals(lastYM, currentYM, StringComparison.Ordinal))
            return (false, currentYM, 0);

        int cleared = await ExecuteResetAsync(conn, currentYM, ct);
        return (true, currentYM, cleared);
    }

    public static async Task<int> ExecuteResetAsync(MySqlConnection conn, string targetYM, CancellationToken ct = default)
    {
        int clearedTables = 0;
        await using (var fkOff = new MySqlCommand("SET FOREIGN_KEY_CHECKS = 0;", conn))
            await fkOff.ExecuteNonQueryAsync(ct);

        try
        {
            foreach (string table in MonthlyResetTables)
            {
                try
                {
                    await using var delCmd = new MySqlCommand($"DELETE FROM `{table}`;", conn);
                    delCmd.CommandTimeout = 60;
                    await delCmd.ExecuteNonQueryAsync(ct);
                    clearedTables++;
                }
                catch
                {
                    // Skip any optional table that doesn't exist in this deployment
                }
            }

            await using var updCmd = new MySqlCommand("UPDATE restaurantsettings SET LastMonthlyResetYM = @ym;", conn);
            updCmd.Parameters.AddWithValue("@ym", targetYM);
            await updCmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            try
            {
                await using var fkOn = new MySqlCommand("SET FOREIGN_KEY_CHECKS = 1;", conn);
                await fkOn.ExecuteNonQueryAsync(ct);
            }
            catch { }
        }

        return clearedTables;
    }
}
