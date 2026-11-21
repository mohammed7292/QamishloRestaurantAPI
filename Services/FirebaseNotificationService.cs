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

            string? credentialJson = Environment.GetEnvironmentVariable("FIREBASE_SERVICE_ACCOUNT_JSON")
                                     ?? _configuration["Firebase:ServiceAccountJson"];
            string? credentialPath = _configuration["Firebase:ServiceAccountPath"];

            try
            {
                GoogleCredential? credential = null;
                if (!string.IsNullOrWhiteSpace(credentialJson))
                {
                    credential = GoogleCredential.FromJson(credentialJson);
                }
                else if (!string.IsNullOrWhiteSpace(credentialPath) && File.Exists(credentialPath))
                {
                    credential = GoogleCredential.FromFile(credentialPath);
                }
                else
                {
                    _logger.LogWarning("Firebase notifications are disabled: Set FIREBASE_SERVICE_ACCOUNT_JSON env var or Firebase:ServiceAccountPath.");
                    return null;
                }

                FirebaseApp app = FirebaseApp.Create(new AppOptions
                {
                    Credential = credential
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
                                ChannelId = "qamishlo.restaurant.orders.v2",
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

    public async Task SendToAudienceAsync(
        string audienceType,
        int? targetId,
        string title,
        string body,
        Dictionary<string, string>? data = null)
    {
        var messaging = TryGetMessaging();
        if (messaging is null) return;

        try
        {
            var tokens = new List<string>();
            await using var conn = DatabaseManager.GetConnection();
            await conn.OpenAsync();
            string sql = targetId.HasValue
                ? "SELECT TokenOrEndpoint FROM pushsubscriptions WHERE IsActive = 1 AND AudienceType = @aud AND (TargetID = @tid OR TargetID IS NULL) LIMIT 100;"
                : "SELECT TokenOrEndpoint FROM pushsubscriptions WHERE IsActive = 1 AND AudienceType = @aud LIMIT 200;";
            await using (var cmd = new MySqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@aud", audienceType.Trim().ToLowerInvariant());
                if (targetId.HasValue) cmd.Parameters.AddWithValue("@tid", targetId.Value);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var tok = r.GetString(0);
                    if (!string.IsNullOrWhiteSpace(tok) && !tok.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        tokens.Add(tok);
                }
            }

            if (tokens.Count == 0) return;

            var msg = new MulticastMessage
            {
                Tokens = tokens,
                Notification = new Notification { Title = title, Body = body },
                Data = data ?? new Dictionary<string, string> { ["type"] = audienceType },
                Android = new AndroidConfig
                {
                    Priority = Priority.High,
                    Notification = new AndroidNotification
                    {
                        ChannelId = "qamishlo_orders_channel",
                        Sound = "default",
                        DefaultSound = true
                    }
                },
                Webpush = new WebpushConfig
                {
                    Headers = new Dictionary<string, string> { ["Urgency"] = "high" },
                    Notification = new WebpushNotification
                    {
                        Title = title,
                        Body = body,
                        Icon = "/assets/qamishlo-logo.png",
                        Badge = "/assets/qamishlo-logo.png",
                        RequireInteraction = true
                    }
                }
            };

            await messaging.SendEachForMulticastAsync(msg);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SendToAudienceAsync failed for {Audience}", audienceType);
        }
    }

}
