using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using MySqlConnector;
using QamishloRestaurantAPI.Database;

namespace QamishloRestaurantAPI.Services;

/// <summary>
/// Sends optional Firebase Cloud Messaging alerts to registered restaurant POS devices.
/// If the service-account file is not configured, the order workflow remains successful
/// and the notification is skipped with a warning in the API log.
/// </summary>
public sealed class FirebaseNotificationService
{
    private const string FirebaseAppName = "QamishloRestaurantNotifications";
    private static readonly object FirebaseLock = new();
    private static FirebaseMessaging? _messaging;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FirebaseNotificationService> _logger;

    public FirebaseNotificationService(
        IConfiguration configuration,
        ILogger<FirebaseNotificationService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private FirebaseMessaging? TryGetMessaging()
    {
        if (_messaging is not null)
            return _messaging;

        lock (FirebaseLock)
        {
            if (_messaging is not null)
                return _messaging;

            string? credentialPath = _configuration["Firebase:ServiceAccountPath"];
            if (string.IsNullOrWhiteSpace(credentialPath) || !File.Exists(credentialPath))
            {
                _logger.LogWarning("Firebase notifications are disabled: Firebase:ServiceAccountPath is missing or invalid.");
                return null;
            }

            try
            {
                FirebaseApp app = FirebaseApp.Create(new AppOptions
                {
                    Credential = GoogleCredential.FromFile(credentialPath)
                }, FirebaseAppName);

                _messaging = FirebaseMessaging.GetMessaging(app);
                return _messaging;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Firebase Admin initialization failed.");
                return null;
            }
        }
    }

    public async Task NotifyRestaurantNewOrderAsync(int orderId, string orderType, decimal totalAmount)
    {
        FirebaseMessaging? messaging = TryGetMessaging();
        if (messaging is null)
            return;

        try
        {
            var tokens = new List<string>();
            await using (var connection = DatabaseManager.GetConnection())
            {
                await connection.OpenAsync();
                const string sql = "SELECT DeviceToken FROM restaurantappdevices WHERE IsActive=1";
                await using var command = new MySqlCommand(sql, connection);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    string token = reader.GetString("DeviceToken");
                    if (!string.IsNullOrWhiteSpace(token))
                        tokens.Add(token);
                }
            }

            foreach (string token in tokens.Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await messaging.SendAsync(new Message
                    {
                        Token = token,
                        Notification = new Notification
                        {
                            Title = "طلب جديد من قامشلو",
                            Body = $"طلب #{orderId} - {orderType} - {totalAmount:N0} د.ع"
                        },
                        Data = new Dictionary<string, string>
                        {
                            ["type"] = "new_order",
                            ["orderId"] = orderId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        },
                        Android = new AndroidConfig
                        {
                            Priority = Priority.High,
                            Notification = new AndroidNotification
                            {
                                ChannelId = "qamishlo.restaurant.general",
                                Sound = "default"
                            }
                        }
                    });
                }
                catch (FirebaseMessagingException ex) when (
                    ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
                {
                    await DeactivateTokenAsync(token);
                    _logger.LogWarning("Removed invalid restaurant device token {TokenSuffix}.", token.Length > 8 ? token[^8..] : token);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send new-order notification to a restaurant device.");
                }
            }
        }
        catch (Exception ex)
        {
            // Never roll back or fail an already-created order because push messaging failed.
            _logger.LogError(ex, "New-order push dispatch failed for order {OrderId}.", orderId);
        }
    }

    private static async Task DeactivateTokenAsync(string token)
    {
        await using var connection = DatabaseManager.GetConnection();
        await connection.OpenAsync();
        await using var command = new MySqlCommand(
            "UPDATE restaurantappdevices SET IsActive=0 WHERE DeviceToken=@token", connection);
        command.Parameters.AddWithValue("@token", token);
        await command.ExecuteNonQueryAsync();
    }
}
