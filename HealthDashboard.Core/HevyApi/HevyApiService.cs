using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.CsvIngestion;

namespace HealthDashboard.Core.HevyApi
{
    public class HevyApiService
    {
        private readonly HttpClient _client;

        public HevyApiService(HttpClient? client = null)
        {
            _client = client ?? new HttpClient();
        }

        /// <summary>
        /// Validates the personal Hevy API key against Hevy's GET /v1/user/info endpoint.
        /// </summary>
        public async Task<bool> ValidateApiKeyAsync(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return false;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.hevyapp.com/v1/user/info");
                request.Headers.Add("api-key", apiKey);

                using var response = await _client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Synchronizes workout logs directly from the Hevy API into our database.
        /// Handles pagination, date filtering, exercise auto-seeding, and CSV-to-API deduplication.
        /// </summary>
        public async Task<ImportResult> SyncWorkoutsAsync(
            AppDbContext db,
            string apiKey,
            DateTime? sinceDate = null,
            DateTime? beforeDate = null,
            bool forceOverwrite = false)
        {
            var imported = 0;
            var skipped = 0;
            var errors = new List<string>();
            var newExercises = new List<string>();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new ImportResult(0, 0, new[] { "API key is required for syncing workouts." });
            }

            // Load existing exercises for O(1) lookup
            var existingExercises = await db.Exercises.Select(e => e.ExerciseName).ToListAsync();
            var exerciseCache = new HashSet<string>(existingExercises, StringComparer.OrdinalIgnoreCase);

            var page = 1;
            var keepFetching = true;

            await using var transaction = await db.Database.BeginTransactionAsync();

            try
            {
                while (keepFetching)
                {
                    var url = $"https://api.hevyapp.com/v1/workouts?page={page}&pageSize=10";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Add("api-key", apiKey);

                    using var response = await _client.SendAsync(request);
                    if (!response.IsSuccessStatusCode)
                    {
                        errors.Add($"Failed to retrieve workouts from Hevy API. Status: {response.StatusCode}");
                        break;
                    }

                    var responseBody = await response.Content.ReadAsStringAsync();
                    var hevyResponse = JsonSerializer.Deserialize<HevyWorkoutResponse>(responseBody);

                    if (hevyResponse == null || hevyResponse.Workouts == null || hevyResponse.Workouts.Count == 0)
                    {
                        break;
                    }

                    foreach (var workout in hevyResponse.Workouts)
                    {
                        try
                        {
                            // 1. Parse start and end times
                            if (!DateTime.TryParse(workout.StartTime, out var parsedStart))
                            {
                                errors.Add($"Could not parse start_time '{workout.StartTime}' for workout '{workout.Title}'");
                                skipped++;
                                continue;
                            }

                            DateTime parsedEnd = parsedStart;
                            if (!string.IsNullOrEmpty(workout.EndTime))
                            {
                                DateTime.TryParse(workout.EndTime, out parsedEnd);
                            }

                            // 2. Perform Date Range filtering optimizations
                            // Since workouts are returned sorted newest first:
                            // If a workout is OLDER than the 'sinceDate', we can completely stop fetching pages!
                            if (sinceDate.HasValue && parsedStart.ToUniversalTime() < sinceDate.Value.ToUniversalTime())
                            {
                                keepFetching = false;
                                break;
                            }

                            // If a workout is NEWER than the 'beforeDate', we skip it but keep traversing the page
                            if (beforeDate.HasValue && parsedStart.ToUniversalTime() > beforeDate.Value.ToUniversalTime())
                            {
                                skipped++;
                                continue;
                            }

                            var dateStr = parsedStart.ToString("yyyy-MM-dd");

                            // 3. Deduplication & Overwrite Logic
                            // 3a. Check for duplicate UUID (standard API record)
                            var existingWorkout = await db.Workouts.FirstOrDefaultAsync(w => w.WorkoutId == workout.Id);
                            if (existingWorkout != null)
                            {
                                if (forceOverwrite)
                                {
                                    db.Workouts.Remove(existingWorkout);
                                    await db.SaveChangesAsync();
                                }
                                else
                                {
                                    skipped++;
                                    continue;
                                }
                            }

                            // 3b. CSV Deduplication - Check for matching workouts on the same day with same name or timestamp
                            var dailyWorkouts = await db.Workouts.Where(w => w.Date == dateStr).ToListAsync();
                            var csvWorkout = dailyWorkouts.FirstOrDefault(w =>
                                w.HevyWorkoutName.Equals(workout.Title, StringComparison.OrdinalIgnoreCase) ||
                                (DateTime.TryParse(w.TimestampStart, out var existingStart) && 
                                 Math.Abs((existingStart.ToUniversalTime() - parsedStart.ToUniversalTime()).TotalMinutes) <= 5)
                            );

                            if (csvWorkout != null)
                            {
                                // Old CSV workout found; delete it so it can be replaced by the clean API UUID-based record
                                db.Workouts.Remove(csvWorkout);
                                await db.SaveChangesAsync();
                            }

                            // 4. Ensure DailyMetric skeleton exists
                            var metricExists = await db.DailyMetrics.AnyAsync(dm => dm.Date == dateStr);
                            if (!metricExists)
                            {
                                var localExists = db.DailyMetrics.Local.Any(dm => dm.Date == dateStr);
                                if (!localExists)
                                {
                                    db.DailyMetrics.Add(new DailyMetric
                                    {
                                        Date = dateStr,
                                        TdeeCalculated = 0,
                                        SuuntoActiveCals = 0,
                                        SuuntoHrv = 0,
                                        SuuntoSleepHours = 0,
                                        SuuntoRestingHr = 0,
                                        CronoCalsIn = 0,
                                        CronoProteinG = 0,
                                        CronoCarbsG = 0,
                                        CronoFatG = 0
                                    });
                                    await db.SaveChangesAsync();
                                }
                            }

                            // 5. Automatically seed missing exercises discovered from API
                            foreach (var exercise in workout.Exercises)
                            {
                                if (!exerciseCache.Contains(exercise.Title))
                                {
                                    var newExercise = new Exercise
                                    {
                                        ExerciseName = exercise.Title,
                                        TargetMuscleGroup = "Other",
                                        MovementPattern = "Other"
                                    };
                                    db.Exercises.Add(newExercise);
                                    exerciseCache.Add(exercise.Title);
                                    newExercises.Add(exercise.Title);
                                }
                            }
                            await db.SaveChangesAsync();

                            // 6. Create the Workout record with native Hevy UUID
                            var newWorkout = new Workout
                            {
                                WorkoutId = workout.Id,
                                Date = dateStr,
                                TimestampStart = parsedStart.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                TimestampEnd = parsedEnd.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                                HevyWorkoutName = workout.Title,
                                AvgHeartRate = 0,
                                MaxHeartRate = 0,
                                StrainScore = 0
                            };
                            db.Workouts.Add(newWorkout);

                            // 7. Add WorkoutSets
                            foreach (var exercise in workout.Exercises)
                            {
                                foreach (var set in exercise.Sets)
                                {
                                    // IsHardSet: RPE >= 7, or set type failure/dropset
                                    var isHardSet = (set.Rpe.HasValue && set.Rpe.Value >= 7) ||
                                                    set.Type.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                                                    set.Type.Equals("dropset", StringComparison.OrdinalIgnoreCase);

                                    var workoutSet = new WorkoutSet
                                    {
                                        WorkoutId = workout.Id,
                                        ExerciseName = exercise.Title,
                                        SetOrder = set.Index + 1, // Store as 1-indexed
                                        WeightKg = set.WeightKg ?? 0,
                                        Reps = set.Reps ?? 0,
                                        Rpe = set.Rpe ?? 0,
                                        IsHardSet = isHardSet
                                    };
                                    db.WorkoutSets.Add(workoutSet);
                                }
                            }

                            await db.SaveChangesAsync();
                            imported++;
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"Error importing workout '{workout.Title}' ({workout.Id}) on {workout.StartTime}: {ex.Message}");
                            skipped++;
                        }
                    }

                    if (!keepFetching || page >= hevyResponse.PageCount)
                    {
                        break;
                    }

                    page++;
                }

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                errors.Add($"General database transaction failure: {ex.Message}");
            }

            return new ImportResult(imported, skipped, errors, newExercises.Distinct().ToList());
        }
    }
}
