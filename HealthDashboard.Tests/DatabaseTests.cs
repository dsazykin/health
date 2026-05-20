using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HealthDashboard.Core;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Tests
{
    public class DatabaseTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public DatabaseTests()
        {
            // Set up an in-memory SQLite connection
            _connection = new SqliteConnection("Filename=:memory:");
            _connection.Open();

            // Enable foreign key constraints in SQLite explicitly for testing
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
        public void TestDatabaseCreationAndSeeding()
        {
            using (var context = new AppDbContext(_options))
            {
                // Initialize database
                DbInitializer.Initialize(context);

                // Verify exercises were seeded
                var exercises = context.Exercises.ToList();
                Assert.NotEmpty(exercises);
                
                // Assert specific standard exercises are present
                Assert.Contains(exercises, e => e.ExerciseName == "Bench Press (Barbell)");
                Assert.Contains(exercises, e => e.ExerciseName == "Squat (Barbell)");
                Assert.Contains(exercises, e => e.ExerciseName == "Pull Up");
            }
        }

        [Fact]
        public void TestForeignKeyEnforcement_InvalidExerciseName()
        {
            using (var context = new AppDbContext(_options))
            {
                DbInitializer.Initialize(context);

                // Add a valid daily metric
                var metric = new DailyMetric { Date = "2026-05-20", WeightKg = 80.5 };
                context.DailyMetrics.Add(metric);

                // Add a valid workout
                var workout = new Workout
                {
                    WorkoutId = "test-workout-1",
                    Date = "2026-05-20",
                    TimestampStart = "2026-05-20T18:00:00Z",
                    TimestampEnd = "2026-05-20T19:00:00Z",
                    HevyWorkoutName = "Upper A"
                };
                context.Workouts.Add(workout);
                context.SaveChanges();

                // Add a workout set with an INVALID exercise name
                var set = new WorkoutSet
                {
                    WorkoutId = "test-workout-1",
                    ExerciseName = "Non Existent Super Lift", // Does not exist in Exercises table
                    SetOrder = 1,
                    WeightKg = 100,
                    Reps = 5,
                    Rpe = 8.5
                };
                context.WorkoutSets.Add(set);

                // SaveChanges should throw a DbUpdateException because of the SQLite foreign key violation!
                var exception = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
                Assert.NotNull(exception.InnerException);
                Assert.Contains("FOREIGN KEY constraint failed", exception.InnerException.Message);
            }
        }

        [Fact]
        public void TestCascadeDelete_WorkoutDeletesSets()
        {
            using (var context = new AppDbContext(_options))
            {
                DbInitializer.Initialize(context);

                // 1. Add DailyMetric
                var metric = new DailyMetric { Date = "2026-05-20", WeightKg = 80.5 };
                context.DailyMetrics.Add(metric);

                // 2. Add Workout
                var workout = new Workout
                {
                    WorkoutId = "test-workout-cascade",
                    Date = "2026-05-20",
                    TimestampStart = "2026-05-20T18:00:00Z",
                    TimestampEnd = "2026-05-20T19:00:00Z",
                    HevyWorkoutName = "Legs A"
                };
                context.Workouts.Add(workout);

                // 3. Add Workout Set
                var set = new WorkoutSet
                {
                    WorkoutId = "test-workout-cascade",
                    ExerciseName = "Squat (Barbell)", // Exists in seeded exercises
                    SetOrder = 1,
                    WeightKg = 120,
                    Reps = 5,
                    Rpe = 9
                };
                context.WorkoutSets.Add(set);
                context.SaveChanges();

                // Confirm set was added
                Assert.Equal(1, context.WorkoutSets.Count(s => s.WorkoutId == "test-workout-cascade"));

                // 4. Delete the workout
                context.Workouts.Remove(workout);
                context.SaveChanges();

                // 5. Verify the set was deleted automatically via Cascade Delete
                Assert.Empty(context.WorkoutSets.Where(s => s.WorkoutId == "test-workout-cascade").ToList());
            }
        }

        [Fact]
        public void TestSafeUpsertPattern_DailyMetricsSourceIntegration()
        {
            using (var context = new AppDbContext(_options))
            {
                DbInitializer.Initialize(context);

                // Simulating Step 1: Weight input synced first
                var initialMetric = new DailyMetric 
                { 
                    Date = "2026-05-20", 
                    WeightKg = 80.5,
                    BodyFatPercent = 15.2
                };
                context.DailyMetrics.Add(initialMetric);
                context.SaveChanges();

                // Simulating Step 2: Cronometer sync arrives later with nutrition data for the same day.
                // We use standard EF Core check-and-update to simulate a safe upsert without overwriting weight/fat.
                var targetDate = "2026-05-20";
                var existingMetric = context.DailyMetrics.FirstOrDefault(m => m.Date == targetDate);

                if (existingMetric != null)
                {
                    // Safe update: update only nutrition properties
                    existingMetric.CronoCalsIn = 2500;
                    existingMetric.CronoProteinG = 160;
                    existingMetric.CronoCarbsG = 250;
                    existingMetric.CronoFatG = 80;
                }
                else
                {
                    // If it didn't exist, we'd add it
                    context.DailyMetrics.Add(new DailyMetric
                    {
                        Date = targetDate,
                        CronoCalsIn = 2500,
                        CronoProteinG = 160,
                        CronoCarbsG = 250,
                        CronoFatG = 80
                    });
                }
                context.SaveChanges();

                // Fetch database state to verify weight/fat were preserved and nutrition was updated!
                var finalMetric = context.DailyMetrics.Single(m => m.Date == "2026-05-20");
                Assert.Equal(80.5, finalMetric.WeightKg);
                Assert.Equal(15.2, finalMetric.BodyFatPercent);
                Assert.Equal(2500, finalMetric.CronoCalsIn);
                Assert.Equal(160, finalMetric.CronoProteinG);
            }
        }
    }
}
