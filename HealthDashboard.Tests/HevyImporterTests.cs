using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HealthDashboard.Core;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.CsvIngestion;

namespace HealthDashboard.Tests
{
    public class HevyImporterTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public HevyImporterTests()
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
        public async Task TestHevyImporter_ImportsSuccessfully_CreatesExercisesAndMetrics()
        {
            const string csvContent =
                "title,start_time,end_time,description,exercise_title,superset_id,exercise_notes,set_index,set_type,weight_kg,reps,distance_km,duration_seconds,rpe\n" +
                "\"Triceps and shoulders\",\"May 22, 2026, 12:57 PM\",\"May 22, 2026, 1:32 PM\",\"\",\"Triceps Dip (Weighted)\",,\"\",0,\"warmup\",,16,,,\n" +
                "\"Triceps and shoulders\",\"May 22, 2026, 12:57 PM\",\"May 22, 2026, 1:32 PM\",\"\",\"Triceps Dip (Weighted)\",,\"\",1,\"failure\",15,9,,,7.5\n" +
                "\"Triceps and shoulders\",\"May 22, 2026, 12:57 PM\",\"May 22, 2026, 1:32 PM\",\"\",\"New Custom Exercise\",,\"\",0,\"normal\",40,10,,,\n";

            var tmpPath = Path.GetTempFileName();
            await File.WriteAllTextAsync(tmpPath, csvContent);

            try
            {
                using var context = new AppDbContext(_options);
                DbInitializer.Initialize(context);

                var result = await HevyImporter.ImportAsync(context, tmpPath);

                Assert.Empty(result.Errors);
                Assert.Equal(1, result.Imported); // 1 workout grouped

                // Validate exercises
                Assert.NotNull(result.NewExercises);
                Assert.Contains("Triceps Dip (Weighted)", result.NewExercises);
                Assert.Contains("New Custom Exercise", result.NewExercises);

                var dbExercise = context.Exercises.FirstOrDefault(e => e.ExerciseName == "New Custom Exercise");
                Assert.NotNull(dbExercise);
                Assert.Equal("Other", dbExercise.TargetMuscleGroup);

                // Validate workout
                var workout = context.Workouts.Include(w => w.WorkoutSets).FirstOrDefault();
                Assert.NotNull(workout);
                Assert.Equal("Triceps and shoulders", workout.HevyWorkoutName);
                Assert.Equal("2026-05-22", workout.Date);

                // Validate Sets
                Assert.Equal(3, workout.WorkoutSets.Count);

                var warmupSet = workout.WorkoutSets.FirstOrDefault(s => s.ExerciseName == "Triceps Dip (Weighted)" && s.SetOrder == 1);
                Assert.NotNull(warmupSet);
                Assert.Equal(0, warmupSet.WeightKg);
                Assert.Equal(16, warmupSet.Reps);
                Assert.False(warmupSet.IsHardSet);

                var failureSet = workout.WorkoutSets.FirstOrDefault(s => s.ExerciseName == "Triceps Dip (Weighted)" && s.SetOrder == 2);
                Assert.NotNull(failureSet);
                Assert.Equal(15, failureSet.WeightKg);
                Assert.Equal(9, failureSet.Reps);
                Assert.True(failureSet.IsHardSet); // Has rpe 7.5 and failure set_type

                // Ensure DailyMetric skeleton was created
                var metric = context.DailyMetrics.FirstOrDefault(m => m.Date == "2026-05-22");
                Assert.NotNull(metric);
            }
            finally
            {
                File.Delete(tmpPath);
            }
        }
    }
}
