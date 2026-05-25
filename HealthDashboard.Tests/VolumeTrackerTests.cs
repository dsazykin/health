using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HealthDashboard.Core;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.Analytics;

namespace HealthDashboard.Tests
{
    public class VolumeTrackerTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public VolumeTrackerTests()
        {
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "PRAGMA foreign_keys = ON;";
                command.ExecuteNonQuery();
            }

            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(_connection)
                .Options;
        }

        public void Dispose()
        {
            _connection.Close();
            _connection.Dispose();
        }

        [Fact]
        public void TestCalculateEpley1Rm_RepCapAndMath()
        {
            // Standard case: 100kg for 5 reps
            // 1RM = 100 * (1 + 5/30) = 116.67
            var normal1Rm = VolumeTracker.CalculateEpley1Rm(100.0, 5);
            Assert.NotNull(normal1Rm);
            Assert.True(Math.Abs(normal1Rm.Value - 116.67) < 0.1);

            // Exactly 10 reps (valid)
            // 1RM = 100 * (1 + 10/30) = 133.33
            var exactlyTen = VolumeTracker.CalculateEpley1Rm(100.0, 10);
            Assert.NotNull(exactlyTen);
            Assert.True(Math.Abs(exactlyTen.Value - 133.33) < 0.1);

            // Greater than 10 reps (should be null)
            var elevenReps = VolumeTracker.CalculateEpley1Rm(100.0, 11);
            Assert.Null(elevenReps);

            // 0 reps or less
            var zeroReps = VolumeTracker.CalculateEpley1Rm(100.0, 0);
            Assert.Null(zeroReps);
        }

        [Fact]
        public void TestGetYearWeek_MondayStartMatchesSqlite()
        {
            // 2026-05-25 is a Monday (First day of week 21)
            var date = new DateTime(2026, 5, 25);
            var yearWeek = VolumeTracker.GetYearWeek(date);
            Assert.Equal("2026-21", yearWeek);

            // 2026-05-24 is a Sunday (Last day of week 20)
            var prevDate = new DateTime(2026, 5, 24);
            var prevYearWeek = VolumeTracker.GetYearWeek(prevDate);
            Assert.Equal("2026-20", prevYearWeek);
        }

        [Fact]
        public async Task TestGetWeeklyHypertrophyVolumeAsync_GroupsAndCountsCorrectly()
        {
            using (var db = new AppDbContext(_options))
            {
                await db.Database.EnsureCreatedAsync();

                // Setup exercise, workouts, and sets
                var chestEx = new Exercise { ExerciseName = "Bench Press", TargetMuscleGroup = "Chest", MovementPattern = "Horizontal Press" };
                var backEx = new Exercise { ExerciseName = "Pullup", TargetMuscleGroup = "Back", MovementPattern = "Vertical Pull" };
                db.Exercises.AddRange(chestEx, backEx);

                db.DailyMetrics.Add(new DailyMetric { Date = "2026-05-25" }); // Monday (Week 21)
                db.DailyMetrics.Add(new DailyMetric { Date = "2026-05-24" }); // Sunday (Week 20)

                db.Workouts.Add(new Workout
                {
                    WorkoutId = "W1",
                    Date = "2026-05-25",
                    TimestampStart = "2026-05-25T10:00:00Z",
                    TimestampEnd = "2026-05-25T11:00:00Z",
                    HevyWorkoutName = "Chest Day"
                });

                db.Workouts.Add(new Workout
                {
                    WorkoutId = "W2",
                    Date = "2026-05-24",
                    TimestampStart = "2026-05-24T10:00:00Z",
                    TimestampEnd = "2026-05-24T11:00:00Z",
                    HevyWorkoutName = "Back Day"
                });

                // Add sets: Chest Day sets (Week 21)
                db.WorkoutSets.Add(new WorkoutSet
                {
                    WorkoutId = "W1",
                    ExerciseName = "Bench Press",
                    SetOrder = 1,
                    WeightKg = 80.0,
                    Reps = 8,
                    Rpe = 8.0,
                    IsHardSet = true // RPE >= 7
                });

                db.WorkoutSets.Add(new WorkoutSet
                {
                    WorkoutId = "W1",
                    ExerciseName = "Bench Press",
                    SetOrder = 2,
                    WeightKg = 80.0,
                    Reps = 9,
                    Rpe = 5.0,
                    IsHardSet = false // RPE < 7
                });

                // Add sets: Back Day sets (Week 20)
                db.WorkoutSets.Add(new WorkoutSet
                {
                    WorkoutId = "W2",
                    ExerciseName = "Pullup",
                    SetOrder = 1,
                    WeightKg = 0.0,
                    Reps = 10,
                    Rpe = 9.0,
                    IsHardSet = true // RPE >= 7
                });

                await db.SaveChangesAsync();

                // Act
                var tracker = new VolumeTracker();
                var volumes = await tracker.GetWeeklyHypertrophyVolumeAsync(db);

                // Assert
                Assert.Equal(2, volumes.Count);

                var backVolume = volumes[0];
                Assert.Equal("Back", backVolume.MuscleGroup);
                Assert.Equal("2026-20", backVolume.YearWeek);
                Assert.Equal(1, backVolume.HardSetsCount);

                var chestVolume = volumes[1];
                Assert.Equal("Chest", chestVolume.MuscleGroup);
                Assert.Equal("2026-21", chestVolume.YearWeek);
                Assert.Equal(1, chestVolume.HardSetsCount);
            }
        }

        [Fact]
        public async Task TestGetExercise1RmHistoryAsync_CalculatesCorrectBest1RmPerDay()
        {
            using (var db = new AppDbContext(_options))
            {
                await db.Database.EnsureCreatedAsync();

                var bench = new Exercise { ExerciseName = "Bench Press", TargetMuscleGroup = "Chest" };
                db.Exercises.Add(bench);

                db.DailyMetrics.Add(new DailyMetric { Date = "2026-05-25" });

                db.Workouts.Add(new Workout
                {
                    WorkoutId = "W1",
                    Date = "2026-05-25",
                    TimestampStart = "2026-05-25T10:00:00Z",
                    TimestampEnd = "2026-05-25T11:00:00Z",
                    HevyWorkoutName = "Chest Day"
                });

                // Multiple sets of Bench Press on the same day:
                // Set 1: 100kg for 5 reps => Epley 1RM = 116.67
                // Set 2: 105kg for 3 reps => Epley 1RM = 115.50
                // Set 3: 80kg for 12 reps => excluded (> 10 reps)
                db.WorkoutSets.Add(new WorkoutSet
                {
                    WorkoutId = "W1",
                    ExerciseName = "Bench Press",
                    SetOrder = 1,
                    WeightKg = 100.0,
                    Reps = 5,
                    Rpe = 9.0
                });

                db.WorkoutSets.Add(new WorkoutSet
                {
                    WorkoutId = "W1",
                    ExerciseName = "Bench Press",
                    SetOrder = 2,
                    WeightKg = 105.0,
                    Reps = 3,
                    Rpe = 9.0
                });

                db.WorkoutSets.Add(new WorkoutSet
                {
                    WorkoutId = "W1",
                    ExerciseName = "Bench Press",
                    SetOrder = 3,
                    WeightKg = 80.0,
                    Reps = 12,
                    Rpe = 9.0
                });

                await db.SaveChangesAsync();

                // Act
                var tracker = new VolumeTracker();
                var history = await tracker.GetExercise1RmHistoryAsync(db, "Bench Press");

                // Assert
                Assert.Single(history);
                var point = history[0];
                Assert.Equal("2026-05-25", point.Date);
                // Daily best 1RM should be 116.67 (the max of valid sets)
                Assert.True(Math.Abs(point.OneRepMax - 116.67) < 0.1);
            }
        }
    }
}
