using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using CsvHelper;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Core.CsvIngestion;

public static class HevyImporter
{
    private static readonly string[] DateFormats = {
        "MMM dd, yyyy, h:mm tt",
        "MMM d, yyyy, h:mm tt",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "d MMM yyyy, HH:mm",
        "dd MMM yyyy, HH:mm",
        "yyyy-MM-ddTHH:mm:ssZ",
        "yyyy-MM-ddTHH:mm:ss"
    };

    public static async Task<ImportResult> ImportAsync(AppDbContext db, string csvPath)
    {
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();
        var newExercises = new List<string>();

        // 1. Read the CSV
        List<HevyRecord> records;
        try
        {
            using var reader = new StreamReader(csvPath);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            records = csv.GetRecords<HevyRecord>().ToList();
        }
        catch (Exception ex)
        {
            return new ImportResult(0, 0, [$"Failed to read CSV '{csvPath}': {ex.Message}"]);
        }

        // Group sets by workout identity
        var workoutGroups = records
            .GroupBy(r => new { r.StartTime, r.Title })
            .ToList();

        // Load existing exercises for O(1) lookup
        var existingExercises = await db.Exercises.Select(e => e.ExerciseName).ToListAsync();
        var exerciseCache = new HashSet<string>(existingExercises, StringComparer.OrdinalIgnoreCase);

        // 2. Process each workout group in a single transaction
        await using var transaction = await db.Database.BeginTransactionAsync();

        foreach (var group in workoutGroups)
        {
            try
            {
                // Parse date
                if (!DateTime.TryParseExact(group.Key.StartTime, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsedStart))
                {
                    if (!DateTime.TryParse(group.Key.StartTime, out parsedStart))
                    {
                        errors.Add($"Could not parse start_time '{group.Key.StartTime}' for workout '{group.Key.Title}'");
                        skipped++;
                        continue;
                    }
                }

                // Parse end time from the first record in the group
                var firstRecord = group.First();
                DateTime parsedEnd = parsedStart;
                if (!string.IsNullOrEmpty(firstRecord.EndTime))
                {
                    if (!DateTime.TryParseExact(firstRecord.EndTime, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsedEnd))
                    {
                        DateTime.TryParse(firstRecord.EndTime, out parsedEnd);
                    }
                }

                var dateStr = parsedStart.ToString("yyyy-MM-dd");

                // Generate Deterministic ID
                var workoutId = ComputeWorkoutId(group.Key.StartTime, group.Key.Title);

                // Ensure DailyMetric exists
                var metricExists = await db.DailyMetrics.AnyAsync(dm => dm.Date == dateStr);
                if (!metricExists)
                {
                    // Look in local tracker as well in case we just added it in this transaction
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
                        // Save changes to flush this so foreign key constraint passes
                        await db.SaveChangesAsync();
                    }
                }

                // Handle Overwrite (Cascade Delete)
                var existingWorkout = await db.Workouts.FirstOrDefaultAsync(w => w.WorkoutId == workoutId);
                if (existingWorkout != null)
                {
                    db.Workouts.Remove(existingWorkout);
                    await db.SaveChangesAsync();
                }

                // Check and insert new exercises
                foreach (var record in group)
                {
                    if (!exerciseCache.Contains(record.ExerciseTitle))
                    {
                        var newExercise = new Exercise
                        {
                            ExerciseName = record.ExerciseTitle,
                            TargetMuscleGroup = "Other",
                            MovementPattern = "Other"
                        };
                        db.Exercises.Add(newExercise);
                        exerciseCache.Add(record.ExerciseTitle);
                        newExercises.Add(record.ExerciseTitle);
                    }
                }
                await db.SaveChangesAsync();

                // Create the Workout
                var workout = new Workout
                {
                    WorkoutId = workoutId,
                    Date = dateStr,
                    TimestampStart = parsedStart.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    TimestampEnd = parsedEnd.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    HevyWorkoutName = group.Key.Title,
                    AvgHeartRate = 0,
                    MaxHeartRate = 0,
                    StrainScore = 0
                };
                db.Workouts.Add(workout);

                // Add sets
                foreach (var record in group)
                {
                    // Weight calculation
                    double weightKg = 0;
                    if (record.WeightKg.HasValue) weightKg = record.WeightKg.Value;
                    else if (record.WeightLbs.HasValue) weightKg = record.WeightLbs.Value * 0.45359237;
                    else if (record.Weight.HasValue)
                    {
                        weightKg = record.Weight.Value;
                        if (record.WeightUnit.Equals("lbs", StringComparison.OrdinalIgnoreCase))
                        {
                            weightKg *= 0.45359237;
                        }
                    }

                    // IsHardSet
                    bool isHardSet = (record.Rpe.HasValue && record.Rpe.Value >= 7) ||
                                     record.SetType.Equals("failure", StringComparison.OrdinalIgnoreCase) ||
                                     record.SetType.Equals("dropset", StringComparison.OrdinalIgnoreCase);

                    var workoutSet = new WorkoutSet
                    {
                        WorkoutId = workoutId,
                        ExerciseName = record.ExerciseTitle,
                        SetOrder = record.SetIndex + 1, // Store as 1-indexed
                        WeightKg = weightKg,
                        Reps = record.Reps ?? 0,
                        Rpe = record.Rpe ?? 0,
                        IsHardSet = isHardSet
                    };
                    db.WorkoutSets.Add(workoutSet);
                }

                await db.SaveChangesAsync();
                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Error importing workout '{group.Key.Title}' on {group.Key.StartTime}: {ex.Message}");
                skipped++;
            }
        }

        await transaction.CommitAsync();
        return new ImportResult(imported, skipped, errors, newExercises.Distinct().ToList());
    }

    private static string ComputeWorkoutId(string timestampStart, string workoutName)
    {
        var input = $"{timestampStart}_{workoutName}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
