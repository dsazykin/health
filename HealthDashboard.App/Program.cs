using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using HealthDashboard.Core;
using HealthDashboard.Core.HevyApi;

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
    }
}
