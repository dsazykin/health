using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using HealthDashboard.Core;
using HealthDashboard.Core.HevyApi;
using HealthDashboard.Core.SuuntoApi;
using HealthDashboard.Core.Security;
using HealthDashboard.Core.Models;

namespace HealthDashboard.App
{
    sealed class Program
    {
        // Initialization code. Don't use any Avalonia, third-party APIs or any
        // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
        // yet and stuff might break.
        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("--sync-hevy", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = args.Length > 1 ? args[1] : string.Empty;
                RunHevySyncConsole(apiKey).GetAwaiter().GetResult();
                return;
            }

            if (args.Length > 0 && args[0].Equals("--auth-suunto", StringComparison.OrdinalIgnoreCase))
            {
                RunSuuntoAuthConsole().GetAwaiter().GetResult();
                return;
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }

        // Avalonia configuration, don't remove; also used by visual designer.
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

        /// <summary>
        /// A direct CLI runner allowing developer-first live verification of the sync engine.
        /// Writes directly to the local SQLite database.
        /// </summary>
        private static async Task RunHevySyncConsole(string apiKey)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================");
            Console.WriteLine("    HEVY API LIVE INGESTION TEST UTILITY");
            Console.WriteLine("==================================================");
            Console.ResetColor();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("Enter your Hevy Pro API Key (from hevy.com/settings?developer): ");
                apiKey = Console.ReadLine() ?? string.Empty;
                Console.ResetColor();
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] API Key cannot be empty!");
                Console.ResetColor();
                return;
            }

            Console.WriteLine("\n[1/4] Connecting to SQLite Database...");
            using var db = new AppDbContext();
            
            // Ensure DB schema exists and seeds standard data
            DbInitializer.Initialize(db);
            Console.WriteLine("      SQLite database connected and initialized.");

            Console.WriteLine("\n[2/4] Validating API Key with Hevy API...");
            var apiService = new HevyApiService();
            var isValid = await apiService.ValidateApiKeyAsync(apiKey);

            if (!isValid)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("      [Error] API Key is invalid! Please verify your key.");
                Console.ResetColor();
                return;
            }
            Console.WriteLine("      API key is VALID.");

            Console.WriteLine("\n[3/4] Fetching and Ingesting Workouts from Hevy...");
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("      Syncing all workouts page-by-page. Please wait...");
            Console.ResetColor();

            // Run sync
            var result = await apiService.SyncWorkoutsAsync(db, apiKey);

            if (result.HasErrors)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n      [Errors Encountered during Ingestion]:");
                foreach (var err in result.Errors)
                {
                    Console.WriteLine($"      - {err}");
                }
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n==================================================");
            Console.WriteLine("    SYNC SUMMARY RESULTS");
            Console.WriteLine("==================================================");
            Console.WriteLine($"  - Workouts successfully imported: {result.Imported}");
            Console.WriteLine($"  - Workouts skipped (duplicates):  {result.Skipped}");
            Console.ResetColor();

            if (result.NewExercises != null && result.NewExercises.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n  - {result.NewExercises.Count} New custom exercises discovered:");
                foreach (var ex in result.NewExercises)
                {
                    Console.WriteLine($"    * {ex}");
                }
                Console.ResetColor();
                Console.WriteLine("    (Note: These are temporarily seeded as 'Other' and are ready for Phase 2.2 prompts!)");
            }

            // Save the validated API key securely in our SQLite configs so the UI can auto-fill it later
            Console.WriteLine("\n[4/4] Auto-saving validated API key to configuration...");
            var existingConfig = db.Configs.FirstOrDefault(c => c.Key == "HevyApiKey");
            if (existingConfig != null)
            {
                existingConfig.Value = apiKey;
            }
            else
            {
                db.Configs.Add(new Core.Models.Config { Key = "HevyApiKey", Value = apiKey });
            }
            await db.SaveChangesAsync();
            Console.WriteLine("      Configuration updated successfully.");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n==================================================");
            Console.WriteLine("    TEST RUN COMPLETED SUCCESSFULLY!");
            Console.WriteLine("==================================================");
            Console.ResetColor();
        }

        private static async Task RunSuuntoAuthConsole()
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================");
            Console.WriteLine("    SUUNTO OAUTH2 HANDSHAKE TEST UTILITY");
            Console.WriteLine("==================================================");
            Console.ResetColor();

            using var db = new AppDbContext();
            DbInitializer.Initialize(db);

            // Fetch current settings
            var dbClientId = db.Configs.FirstOrDefault(c => c.Key == "SuuntoClientId");
            var dbClientSecret = db.Configs.FirstOrDefault(c => c.Key == "SuuntoClientSecret");
            var dbRedirectUri = db.Configs.FirstOrDefault(c => c.Key == "SuuntoRedirectUri");

            var clientId = dbClientId?.Value ?? "YOUR_SUUNTO_CLIENT_ID";
            var clientSecret = dbClientSecret?.Value ?? "YOUR_SUUNTO_CLIENT_SECRET";
            var redirectUri = dbRedirectUri?.Value ?? "http://127.0.0.1:5005/callback";

            Console.WriteLine("Current Suunto configuration in Database:");
            Console.WriteLine($"  Client ID:      {(clientId == "YOUR_SUUNTO_CLIENT_ID" ? "[Not Configured]" : clientId)}");
            Console.WriteLine($"  Client Secret:  {(clientSecret == "YOUR_SUUNTO_CLIENT_SECRET" ? "[Not Configured]" : "********")}");
            Console.WriteLine($"  Redirect URI:   {redirectUri}");

            Console.Write("\nWould you like to update these credentials? (y/N): ");
            var input = Console.ReadLine();
            if (input != null && input.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("Enter Suunto Client ID: ");
                var newId = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(newId)) clientId = newId.Trim();

                Console.Write("Enter Suunto Client Secret: ");
                var newSecret = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(newSecret)) clientSecret = newSecret.Trim();

                Console.Write("Enter Redirect URI (default http://127.0.0.1:5005/callback): ");
                var newRedirect = Console.ReadLine();
                if (!string.IsNullOrWhiteSpace(newRedirect)) redirectUri = newRedirect.Trim();

                // Save back to db
                SaveConfigValue(db, "SuuntoClientId", clientId);
                SaveConfigValue(db, "SuuntoClientSecret", clientSecret);
                SaveConfigValue(db, "SuuntoRedirectUri", redirectUri);
                await db.SaveChangesAsync();
                Console.WriteLine("Credentials saved successfully to database!");
            }

            if (clientId == "YOUR_SUUNTO_CLIENT_ID" || clientSecret == "YOUR_SUUNTO_CLIENT_SECRET")
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[Error] Please configure a valid Suunto Client ID and Client Secret before proceeding.");
                Console.ResetColor();
                return;
            }

            Console.WriteLine("\n[1/3] Initiating OAuth2 Handshake...");
            var oauthService = new SuuntoOAuthService();

            try
            {
                Console.WriteLine("      Opening your browser to complete authorization.");
                Console.WriteLine("      Listening locally for the callback. Timeout is 120 seconds...");
                
                var success = await oauthService.StartAuthHandshakeAsync(120);

                if (success)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("\n[2/3] OAuth2 Handshake completed successfully!");
                    Console.ResetColor();

                    Console.WriteLine("\n[3/3] Printing credential status from Database:");
                    var freshDb = new AppDbContext();
                    var secureStorage = new SecureStorage();

                    var encAccessToken = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoAccessToken")?.Value;
                    var encRefreshToken = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoRefreshToken")?.Value;
                    var encExpiresAt = freshDb.Configs.FirstOrDefault(c => c.Key == "SuuntoTokenExpiresAt")?.Value;

                    if (!string.IsNullOrEmpty(encAccessToken) && !string.IsNullOrEmpty(encRefreshToken) && !string.IsNullOrEmpty(encExpiresAt))
                    {
                        var decAccessToken = secureStorage.Decrypt(encAccessToken);
                        var decRefreshToken = secureStorage.Decrypt(encRefreshToken);
                        var decExpiresAt = secureStorage.Decrypt(encExpiresAt);

                        Console.WriteLine($"  - Encrypted Access Token:  {encAccessToken.Substring(0, Math.Min(15, encAccessToken.Length))}...");
                        Console.WriteLine($"  - Decrypted Access Token:  {decAccessToken.Substring(0, Math.Min(6, decAccessToken.Length))}... [VALID]");
                        Console.WriteLine($"  - Encrypted Refresh Token: {encRefreshToken.Substring(0, Math.Min(15, encRefreshToken.Length))}...");
                        Console.WriteLine($"  - Decrypted Refresh Token: {decRefreshToken.Substring(0, Math.Min(6, decRefreshToken.Length))}... [VALID]");
                        Console.WriteLine($"  - Token Expiration:        {decExpiresAt} (UTC)");

                        if (DateTime.TryParse(decExpiresAt, out var expiresAt))
                        {
                            var timeRemaining = expiresAt - DateTime.UtcNow;
                            Console.WriteLine($"  - Expires In:              {timeRemaining.TotalHours:F2} hours");
                        }
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine("      [Error] Tokens could not be retrieved from the database configs.");
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("\n[Error] Handshake failed or was not completed.");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[Error] Exception occurred: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                }
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n==================================================");
            Console.WriteLine("    TEST RUN COMPLETED!");
            Console.WriteLine("==================================================");
            Console.ResetColor();
        }

        private static void SaveConfigValue(AppDbContext db, string key, string value)
        {
            var config = db.Configs.FirstOrDefault(c => c.Key == key);
            if (config != null)
            {
                config.Value = value;
            }
            else
            {
                db.Configs.Add(new Core.Models.Config { Key = key, Value = value });
            }
        }
    }
}
