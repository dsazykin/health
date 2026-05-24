using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.Security;

namespace HealthDashboard.Core.SuuntoApi
{
    public class SuuntoOAuthService
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private static readonly SemaphoreSlim RefreshSemaphore = new SemaphoreSlim(1, 1);
        private readonly ISecureStorage _secureStorage;

        public SuuntoOAuthService(ISecureStorage? secureStorage = null)
        {
            _secureStorage = secureStorage ?? new SecureStorage();
        }

        /// <summary>
        /// Starts the ephemeral HttpListener, launches the system browser, captures the code, and exchanges it for tokens.
        /// </summary>
        public async Task<bool> StartAuthHandshakeAsync(int timeoutSeconds = 120)
        {
            using var db = new AppDbContext();

            var clientId = db.Configs.FirstOrDefault(c => c.Key == "SuuntoClientId")?.Value;
            var clientSecret = db.Configs.FirstOrDefault(c => c.Key == "SuuntoClientSecret")?.Value;
            var redirectUri = db.Configs.FirstOrDefault(c => c.Key == "SuuntoRedirectUri")?.Value;

            if (string.IsNullOrWhiteSpace(clientId) || clientId == "YOUR_SUUNTO_CLIENT_ID" ||
                string.IsNullOrWhiteSpace(clientSecret) || clientSecret == "YOUR_SUUNTO_CLIENT_SECRET" ||
                string.IsNullOrWhiteSpace(redirectUri) || redirectUri == "YOUR_SUUNTO_REDIRECT_URI")
            {
                throw new InvalidOperationException("Suunto API Client ID, Client Secret, and Redirect URI must be configured in the database before starting authentication.");
            }

            var state = Guid.NewGuid().ToString("N");
            var authUrl = $"https://cloud-api.suunto.com/oauth/authorize?response_type=code&client_id={clientId}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={state}";

            // HttpListener requires trailing slash for prefixes
            var listenerPrefix = redirectUri;
            if (!listenerPrefix.EndsWith("/"))
            {
                listenerPrefix += "/";
            }

            using var listener = new HttpListener();
            listener.Prefixes.Add(listenerPrefix);

            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start local callback listener on {listenerPrefix}. Ensure the port is not in use. Error: {ex.Message}", ex);
            }

            Console.WriteLine($"[OAuth] Ephemeral listener started on {listenerPrefix}");
            Console.WriteLine($"[OAuth] Launching system browser for authentication...");

            // Launch browser
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", authUrl);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    Process.Start("xdg-open", authUrl);
                }
                else
                {
                    Console.WriteLine($"[Warning] Unsupported OS. Please open this URL manually in your browser:\n{authUrl}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to launch system browser: {ex.Message}");
                Console.WriteLine($"Please open this URL manually in your browser:\n{authUrl}");
            }

            // Wait for redirect callback with cancellation token timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            HttpListenerContext context;
            try
            {
                // Retrieve the context asynchronously, supporting cancellation
                var getContextTask = listener.GetContextAsync();
                var delayTask = Task.Delay(Timeout.Infinite, cts.Token);
                var completedTask = await Task.WhenAny(getContextTask, delayTask);

                if (completedTask == delayTask)
                {
                    throw new TimeoutException("OAuth handshake timed out waiting for the browser redirect callback.");
                }

                context = await getContextTask;
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("OAuth handshake timed out waiting for the browser redirect callback.");
            }
            finally
            {
                listener.Stop();
            }

            var request = context.Request;
            var response = context.Response;

            var code = request.QueryString["code"];
            var receivedState = request.QueryString["state"];
            var error = request.QueryString["error"];

            if (!string.IsNullOrEmpty(error))
            {
                var errorDesc = request.QueryString["error_description"] ?? error;
                await SendHtmlResponseAsync(response, false, $"Authentication Error: {errorDesc}");
                throw new InvalidOperationException($"Suunto OAuth error: {errorDesc}");
            }

            if (string.IsNullOrEmpty(code))
            {
                await SendHtmlResponseAsync(response, false, "Authorization code not found in redirect URL.");
                throw new InvalidOperationException("Authorization code was missing from the redirect URL.");
            }

            if (receivedState != state)
            {
                await SendHtmlResponseAsync(response, false, "State validation failed. Request may have been tampered with.");
                throw new InvalidOperationException("OAuth state parameter mismatch. Request validation failed.");
            }

            // Send successful response to user's browser
            await SendHtmlResponseAsync(response, true, "Authentication successful! You can safely close this window and return to the console application.");

            Console.WriteLine("[OAuth] Authorization code successfully captured. Exchanging for access tokens...");

            // Exchange authorization code for token
            return await ExchangeAuthorizationCodeAsync(db, clientId, clientSecret, code, redirectUri);
        }

        /// <summary>
        /// Retrieves a valid access token. Automatically refreshes it if it has expired or is about to expire.
        /// </summary>
        public virtual async Task<string?> GetValidAccessTokenAsync()
        {
            using var db = new AppDbContext();

            var encAccessToken = db.Configs.FirstOrDefault(c => c.Key == "SuuntoAccessToken")?.Value;
            var encRefreshToken = db.Configs.FirstOrDefault(c => c.Key == "SuuntoRefreshToken")?.Value;
            var encExpiresAt = db.Configs.FirstOrDefault(c => c.Key == "SuuntoTokenExpiresAt")?.Value;

            if (string.IsNullOrEmpty(encAccessToken) || string.IsNullOrEmpty(encRefreshToken) || string.IsNullOrEmpty(encExpiresAt))
            {
                return null;
            }

            var accessToken = _secureStorage.Decrypt(encAccessToken);
            var refreshToken = _secureStorage.Decrypt(encRefreshToken);
            var expiresAtStr = _secureStorage.Decrypt(encExpiresAt);

            if (!DateTime.TryParse(expiresAtStr, out var expiresAt))
            {
                return null;
            }

            // Check if token is still valid (using a 5-minute buffer)
            if (DateTime.UtcNow.AddMinutes(5) < expiresAt)
            {
                return accessToken;
            }

            // Token is expired or expiring soon, let's refresh
            await RefreshSemaphore.WaitAsync();
            try
            {
                // Double check inside the lock
                using var freshDb = new AppDbContext();
                encAccessToken = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoAccessToken")?.Value;
                encRefreshToken = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoRefreshToken")?.Value;
                encExpiresAt = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoTokenExpiresAt")?.Value;

                if (string.IsNullOrEmpty(encAccessToken) || string.IsNullOrEmpty(encRefreshToken) || string.IsNullOrEmpty(encExpiresAt))
                {
                    return null;
                }

                accessToken = _secureStorage.Decrypt(encAccessToken);
                refreshToken = _secureStorage.Decrypt(encRefreshToken);
                expiresAtStr = _secureStorage.Decrypt(encExpiresAt);

                if (DateTime.TryParse(expiresAtStr, out expiresAt) && DateTime.UtcNow.AddMinutes(5) < expiresAt)
                {
                    return accessToken;
                }

                Console.WriteLine("[OAuth] Access token has expired or is expiring soon. Starting refresh flow...");

                var clientId = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoClientId")?.Value;
                var clientSecret = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoClientSecret")?.Value;

                if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
                {
                    throw new InvalidOperationException("Suunto API Client ID and Client Secret must be configured to refresh tokens.");
                }

                var success = await RefreshTokenAsync(freshDb, clientId, clientSecret, refreshToken);
                if (success)
                {
                    var updatedEncAccessToken = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoAccessToken")?.Value;
                    if (!string.IsNullOrEmpty(updatedEncAccessToken))
                    {
                        return _secureStorage.Decrypt(updatedEncAccessToken);
                    }
                }

                throw new InvalidOperationException("Failed to refresh the Suunto access token. Manual re-authentication may be required.");
            }
            finally
            {
                RefreshSemaphore.Release();
            }
        }

        private async Task<bool> ExchangeAuthorizationCodeAsync(AppDbContext db, string clientId, string clientSecret, string code, string redirectUri)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret)
            });

            var request = new HttpRequestMessage(HttpMethod.Post, "https://cloud-api.suunto.com/oauth/token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = content;

            var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Token exchange failed with status {response.StatusCode}: {errorText}");
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<SuuntoTokenResponse>(responseBody);

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                throw new InvalidOperationException("Failed to parse token response from Suunto API.");
            }

            var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);
            await SaveTokensToDatabaseAsync(db, tokenResponse.AccessToken, tokenResponse.RefreshToken, expiresAt);
            return true;
        }

        private async Task<bool> RefreshTokenAsync(AppDbContext db, string clientId, string clientSecret, string refreshToken)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "refresh_token"),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret)
            });

            var request = new HttpRequestMessage(HttpMethod.Post, "https://cloud-api.suunto.com/oauth/token");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = content;

            var response = await HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[Error] Token refresh failed with status {response.StatusCode}: {errorText}");
                return false;
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var tokenResponse = JsonSerializer.Deserialize<SuuntoTokenResponse>(responseBody);

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                return false;
            }

            // Sometimes the OAuth provider might not return a new refresh token during a refresh,
            // so we fallback to the old one if the new one is null/empty.
            var newRefreshToken = string.IsNullOrEmpty(tokenResponse.RefreshToken) ? refreshToken : tokenResponse.RefreshToken;
            var expiresAt = DateTime.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            await SaveTokensToDatabaseAsync(db, tokenResponse.AccessToken, newRefreshToken, expiresAt);
            return true;
        }

        private async Task SaveTokensToDatabaseAsync(AppDbContext db, string accessToken, string refreshToken, DateTime expiresAt)
        {
            var encAccessToken = _secureStorage.Encrypt(accessToken);
            var encRefreshToken = _secureStorage.Encrypt(refreshToken);
            var encExpiresAt = _secureStorage.Encrypt(expiresAt.ToString("o"));

            SaveConfig(db, "SuuntoAccessToken", encAccessToken);
            SaveConfig(db, "SuuntoRefreshToken", encRefreshToken);
            SaveConfig(db, "SuuntoTokenExpiresAt", encExpiresAt);

            await db.SaveChangesAsync();
            Console.WriteLine("[OAuth] Tokens saved securely in database.");
        }

        private void SaveConfig(AppDbContext db, string key, string value)
        {
            var existing = db.Configs.FirstOrDefault(c => c.Key == key);
            if (existing != null)
            {
                existing.Value = value;
            }
            else
            {
                db.Configs.Add(new Config { Key = key, Value = value });
            }
        }

        private async Task SendHtmlResponseAsync(HttpListenerResponse response, bool isSuccess, string message)
        {
            var title = isSuccess ? "Authentication Successful" : "Authentication Failed";
            var color = isSuccess ? "#2e7d32" : "#c62828";
            var html = $@"
<!DOCTYPE html>
<html>
<head>
    <title>{title}</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;
            text-align: center;
            background-color: #f5f5f5;
            padding: 50px;
            color: #333;
        }}
        .container {{
            max-width: 600px;
            margin: 0 auto;
            background: white;
            padding: 40px;
            border-radius: 8px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.1);
        }}
        h1 {{
            color: {color};
            margin-bottom: 20px;
        }}
        p {{
            font-size: 1.1em;
            line-height: 1.6;
        }}
        .footer {{
            margin-top: 30px;
            font-size: 0.9em;
            color: #888;
        }}
    </style>
</head>
<body>
    <div class='container'>
        <h1>{title}</h1>
        <p>{message}</p>
        <div class='footer'>Health Dashboard Connection Utility</div>
    </div>
</body>
</html>";

            byte[] buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.ContentType = "text/html";
            response.StatusCode = isSuccess ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;

            try
            {
                using var output = response.OutputStream;
                await output.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] Failed to write HTML response to browser: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        private class SuuntoTokenResponse
        {
            [JsonPropertyName("access_token")]
            public string AccessToken { get; set; } = string.Empty;

            [JsonPropertyName("refresh_token")]
            public string RefreshToken { get; set; } = string.Empty;

            [JsonPropertyName("expires_in")]
            public int ExpiresIn { get; set; }
        }
    }
}
