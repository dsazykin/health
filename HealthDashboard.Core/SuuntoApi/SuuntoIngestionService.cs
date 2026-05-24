using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.Security;

namespace HealthDashboard.Core.SuuntoApi
{
    public class SuuntoIngestionService
    {
        private readonly HttpClient _client;
        private readonly SuuntoOAuthService _oauthService;
        private readonly ISecureStorage _secureStorage;
        private static readonly SemaphoreSlim SyncSemaphore = new SemaphoreSlim(1, 1);

        public SuuntoIngestionService(HttpClient? client = null, SuuntoOAuthService? oauthService = null, ISecureStorage? secureStorage = null)
        {
            _client = client ?? new HttpClient();
            _oauthService = oauthService ?? new SuuntoOAuthService(secureStorage);
            _secureStorage = secureStorage ?? new SecureStorage();
        }

        public async Task<SuuntoSyncResult> SyncAsync(AppDbContext db)
        {
            // Thread safety via semaphore
            await SyncSemaphore.WaitAsync();
            try
            {
                var result = new SuuntoSyncResult();

                // 1. Fetch valid OAuth Access Token
                string? accessToken;
                try
                {
                    accessToken = await _oauthService.GetValidAccessTokenAsync();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("OAuth access token is not configured. Please authorize Suunto first using --auth-suunto.", ex);
                }

                if (string.IsNullOrEmpty(accessToken))
                {
                    throw new InvalidOperationException("OAuth access token is not configured. Please authorize Suunto first using --auth-suunto.");
                }

                // 2. Fetch Suunto API Subscription Key from database configuration
                var subscriptionKey = db.Configs.FirstOrDefault(c => c.Key == "SuuntoSubscriptionKey")?.Value;
                if (string.IsNullOrWhiteSpace(subscriptionKey) || subscriptionKey == "YOUR_SUUNTO_SUBSCRIPTION_KEY")
                {
                    throw new InvalidOperationException("Suunto API Subscription Key is not configured. Please seed a valid Ocp-Apim-Subscription-Key in Database Config.");
                }

                // Attempt to decrypt if it was encrypted, fallback to raw
                try
                {
                    var decrypted = _secureStorage.Decrypt(subscriptionKey);
                    if (!string.IsNullOrEmpty(decrypted) && decrypted != subscriptionKey)
                    {
                        subscriptionKey = decrypted;
                    }
                }
                catch
                {
                    // Stored raw
                }

                // 3. Retrieve activities from Suunto Cloud API
                List<SuuntoActivity> suuntoActivities = new List<SuuntoActivity>();
                try
                {
                    var workoutsUrl = "https://cloud-api.suunto.com/v2/workouts";
                    using var request = new HttpRequestMessage(HttpMethod.Get, workoutsUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

                    using var response = await _client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        throw new InvalidOperationException($"Suunto API activities request failed with status {response.StatusCode}: {errorBody}");
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    suuntoActivities = DeserializeList<SuuntoActivity>(responseBody);
                    result.ActivitiesFetched = suuntoActivities.Count;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Error fetching Suunto activities: {ex.Message}");
                }

                // 4. Rate limiting: 1-second delay between outbound HTTP requests to prevent API throttling
                await Task.Delay(1000);

                // 5. Retrieve daily wellness metrics from Suunto Cloud API
                List<SuuntoWellnessMetric> suuntoWellness = new List<SuuntoWellnessMetric>();
                try
                {
                    var dailyUrl = "https://cloud-api.suunto.com/v2/daily";
                    using var request = new HttpRequestMessage(HttpMethod.Get, dailyUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

                    using var response = await _client.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        suuntoWellness = DeserializeList<SuuntoWellnessMetric>(responseBody);
                    }
                    else
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        result.Errors.Add($"Suunto API wellness metrics request failed with status {response.StatusCode}: {errorBody}");
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Error fetching Suunto wellness metrics: {ex.Message}");
                }

                // 6. DB transaction safety (BeginTransactionAsync and CommitAsync)
                await using var transaction = await db.Database.BeginTransactionAsync();
                try
                {
                    // 6a. Stitching Algorithm
                    if (suuntoActivities.Any())
                    {
                        // Fetch workouts where SuuntoActivityId is null
                        var unstitchedWorkouts = await db.Workouts
                            .Where(w => w.SuuntoActivityId == null)
                            .ToListAsync();

                        foreach (var workout in unstitchedWorkouts)
                        {
                            if (!DateTimeOffset.TryParse(workout.TimestampStart, out var workoutStartUtc))
                            {
                                continue;
                            }

                            // Compare with fetched Suunto activities
                            foreach (var sa in suuntoActivities)
                            {
                                var saStartOpt = sa.GetStartTimeUtc();
                                if (!saStartOpt.HasValue) continue;

                                var saStartUtc = saStartOpt.Value;
                                var diffMinutes = Math.Abs((workoutStartUtc - saStartUtc).TotalMinutes);

                                // ±15-minute start time window
                                if (diffMinutes <= 15)
                                {
                                    workout.SuuntoActivityId = sa.Id;
                                    workout.AvgHeartRate = sa.AverageHr;
                                    workout.MaxHeartRate = sa.MaxHr;

                                    // Strain score with TRIMP fallback calculation
                                    if (sa.Tss.HasValue && sa.Tss.Value > 0)
                                    {
                                        workout.StrainScore = (int)Math.Round(sa.Tss.Value);
                                    }
                                    else if (sa.MaxHr > 0 && sa.AverageHr > 0 && sa.Duration > 0)
                                    {
                                        double durationMins = sa.Duration / 60.0;
                                        // Fallback TRIMP formula: duration * (AvgHR/MaxHR) * IntensityFactor (1.5)
                                        workout.StrainScore = (int)Math.Round(durationMins * ((double)sa.AverageHr / sa.MaxHr) * 1.5);
                                    }
                                    else
                                    {
                                        workout.StrainScore = 0;
                                    }

                                    result.WorkoutsStitched++;
                                    break; // Matched this workout, move to the next
                                }
                            }
                        }
                        await db.SaveChangesAsync();
                    }

                    // 6b. Wellness updates: Execute column-level safe updates in DailyMetrics (preventing overwrites of other fields)
                    foreach (var wellness in suuntoWellness)
                    {
                        if (string.IsNullOrWhiteSpace(wellness.Date)) continue;

                        var activeCals = wellness.ActiveCalories;
                        var hrv = wellness.Hrv;
                        var sleepHours = wellness.SleepDurationSeconds / 3600.0;
                        var restingHr = wellness.RestingHeartRate;

                        await db.Database.ExecuteSqlAsync($@"
                            INSERT INTO DailyMetrics
                                (Date, TdeeCalculated, SuuntoActiveCals, SuuntoHrv, SuuntoSleepHours, SuuntoRestingHr,
                                 CronoCalsIn, CronoProteinG, CronoCarbsG, CronoFatG)
                            VALUES
                                ({wellness.Date}, 0, {activeCals}, {hrv}, {sleepHours}, {restingHr},
                                 0, 0, 0, 0)
                            ON CONFLICT(Date) DO UPDATE SET
                                SuuntoActiveCals = excluded.SuuntoActiveCals,
                                SuuntoHrv = excluded.SuuntoHrv,
                                SuuntoSleepHours = excluded.SuuntoSleepHours,
                                SuuntoRestingHr = excluded.SuuntoRestingHr;
                        ");

                        result.WellnessDaysUpdated++;
                    }

                    await transaction.CommitAsync();
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync();
                    result.Errors.Add($"Database transactional operation failed: {ex.Message}");
                }

                return result;
            }
            finally
            {
                SyncSemaphore.Release();
            }
        }

        private List<T> DeserializeList<T>(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    // Try to find any property that is an array
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            return JsonSerializer.Deserialize<List<T>>(prop.Value.GetRawText()) ?? new List<T>();
                        }
                    }
                }
            }
            catch
            {
                // Fallback
            }
            return new List<T>();
        }
    }

    public class SuuntoSyncResult
    {
        public int ActivitiesFetched { get; set; }
        public int WorkoutsStitched { get; set; }
        public int WellnessDaysUpdated { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public bool HasErrors => Errors.Any();
    }

    public class SuuntoActivity
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("startTime")]
        public object? StartTime { get; set; }

        [JsonPropertyName("duration")]
        public double Duration { get; set; } // in seconds

        [JsonPropertyName("averageHr")]
        public int AverageHr { get; set; }

        [JsonPropertyName("maxHr")]
        public int MaxHr { get; set; }

        [JsonPropertyName("calories")]
        public int Calories { get; set; }

        [JsonPropertyName("tss")]
        public double? Tss { get; set; }

        public DateTimeOffset? GetStartTimeUtc()
        {
            if (StartTime == null) return null;

            if (StartTime is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var ms))
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(ms);
                }
                if (element.ValueKind == JsonValueKind.String)
                {
                    var str = element.GetString();
                    if (DateTimeOffset.TryParse(str, out var parsed))
                    {
                        return parsed.ToUniversalTime();
                    }
                }
            }
            else if (StartTime is long longVal)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(longVal);
            }
            else if (StartTime is string strVal && DateTimeOffset.TryParse(strVal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }
            return null;
        }
    }

    public class SuuntoWellnessMetric
    {
        [JsonPropertyName("date")]
        public string Date { get; set; } = string.Empty;

        [JsonPropertyName("sleepDurationSeconds")]
        public double SleepDurationSeconds { get; set; }

        [JsonPropertyName("restingHeartRate")]
        public int RestingHeartRate { get; set; }

        [JsonPropertyName("hrv")]
        public double Hrv { get; set; }

        [JsonPropertyName("activeCalories")]
        public int ActiveCalories { get; set; }
    }
}
