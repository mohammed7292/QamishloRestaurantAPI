using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace QamishloRestaurantAPI.Services;

public class FirebaseNotificationService
{
    private readonly FirebaseMessaging _messaging;

    public FirebaseNotificationService(IConfiguration configuration)
    {
        string? credentialPath =
            configuration["Firebase:ServiceAccountPath"];

        if (string.IsNullOrWhiteSpace(credentialPath) ||
            !File.Exists(credentialPath))
        {
            throw new FileNotFoundException(
                "Firebase service account file was not found."
            );
        }

        var app = FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromFile(credentialPath)
        });

        _messaging = FirebaseMessaging.GetMessaging(app);
    }

    public async Task<string> SendNotificationAsync(
        string deviceToken,
        string title,
        string body)
    {
        var message = new Message
        {
            Token = deviceToken,

            Notification = new Notification
            {
                Title = title,
                Body = body
            },

            Android = new AndroidConfig
            {
                Priority = Priority.High
            }
        };

        return await _messaging.SendAsync(message);
    }
}