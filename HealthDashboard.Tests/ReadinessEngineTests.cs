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
    public class ReadinessEngineTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public ReadinessEngineTests()
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
        public void TestCalculateCvStrain_NoActivity_ReturnsZero()
        {
            var strain = ReadinessEngine.CalculateCvStrain(false, 70, 120, 180, 45);
            Assert.Equal(0, strain);
        }

        [Fact]
        public void TestCalculateCvStrain_WithTss_UsesTssDirectly()
        {
            var strain1 = ReadinessEngine.CalculateCvStrain(true, 75.5, 120, 180, 45);
            var strain2 = ReadinessEngine.CalculateCvStrain(true, 150, 120, 180, 45);

            Assert.Equal(75.5, strain1);
            Assert.Equal(100, strain2); // Capped at 100
        }

        [Fact]
        public void TestCalculateCvStrain_WithTrimpFallback()
        {
            // Duration: 60 mins, AvgHR: 130, MaxHR: 180
            // TRIMP = 60 * (130/180) * 1.5 = 65
            // CV = 100 * (1 - e^(-0.015 * 65)) = 100 * (1 - e^(-0.975)) = 100 * (1 - 0.37719) = 62.28
            var strain = ReadinessEngine.CalculateCvStrain(true, null, 130, 180, 60);
            Assert.True(Math.Abs(strain - 62.28) < 0.1);
        }

        [Fact]
        public void TestCalculateMsStrain_CapsAndDefaults()
        {
            // Normal volume
            var strain = ReadinessEngine.CalculateMsStrain(1000, 8.0);
            // MS = 100 * (1 - e^(-0.00015 * 1000 * 0.8)) = 100 * (1 - e^(-0.12)) = 11.3
            Assert.True(Math.Abs(strain - 11.3) < 0.1);

            // Default RPE when RPE is 0
            var strainDefaultRpe = ReadinessEngine.CalculateMsStrain(1000, 0);
            // MS = 100 * (1 - e^(-0.00015 * 1000 * 0.7)) = 100 * (1 - e^(-0.105)) = 9.96
            Assert.True(Math.Abs(strainDefaultRpe - 9.96) < 0.1);
        }

        [Fact]
        public void TestCompositeWorkoutStrain()
        {
            // Both CV and MS
            var comp1 = ReadinessEngine.CalculateCompositeWorkoutStrain(true, 80, true, 40);
            Assert.Equal(0.6 * 80 + 0.4 * 40, comp1);

            // CV only
            var comp2 = ReadinessEngine.CalculateCompositeWorkoutStrain(true, 80, false, 40);
            Assert.Equal(80, comp2);

            // MS only
            var comp3 = ReadinessEngine.CalculateCompositeWorkoutStrain(false, 80, true, 40);
            Assert.Equal(40, comp3);
        }

        [Fact]
        public void TestDailyStrain_WorkoutsAndActiveCaloriesFallback()
        {
            // Root-sum-square of workouts
            var ds = ReadinessEngine.CalculateDailyStrain(new double[] { 30, 40 }, 0);
            Assert.Equal(50, ds); // sqrt(900 + 1600) = 50

            // Capped at 100
            var dsCapped = ReadinessEngine.CalculateDailyStrain(new double[] { 80, 80 }, 0);
            Assert.Equal(100, dsCapped);

            // Active Calories fallback
            var dsCals = ReadinessEngine.CalculateDailyStrain(new double[0], 250);
            // 10 + 250/25 = 20
            Assert.Equal(20, dsCals);
        }

        [Fact]
        public void TestRecoveryScores_HrvAndRhrScores()
        {
            // HRV today is 60, baseline mean is 50, stddev is 5
            // Z_HRV = (60 - 50) / 5 = 2
            // Score = 80 + 2 * 15 = 110 => capped at 100
            var hrvScore = ReadinessEngine.CalculateHrvScore(60, new double[] { 50, 50, 50, 50, 50, 50, 50 }); // N >= 7
            Assert.Equal(100, hrvScore);

            // Default HRV score when today is 0
            var hrvDefault = ReadinessEngine.CalculateHrvScore(0, new double[] { 50, 50 });
            Assert.Equal(70, hrvDefault);

            // RHR today is 55, baseline mean is 65
            // Z_RHR = (65 - 55) / 10 = 1
            // Score = 80 + 1 * 15 = 95
            var rhrScore = ReadinessEngine.CalculateRhrScore(55, new double[] { 65, 65 });
            Assert.Equal(95, rhrScore);
        }

        [Fact]
        public void TestReadinessScoresAndAdvice()
        {
            // Recovery today: 80, Yesterday strain: 50
            // Readiness = 80 * (1 - 0.3 * 0.5) = 80 * 0.85 = 68
            var readiness = ReadinessEngine.CalculateReadiness(80, 50);
            Assert.Equal(68, readiness);

            var advice = ReadinessEngine.GetActionableAdvice(68);
            Assert.Contains("Readiness is moderate", advice);
        }

        [Fact]
        public async Task TestUpdateReadinessHistoryAsync_DatabaseOrchestration()
        {
            using (var db = new AppDbContext(_options))
            {
                await db.Database.EnsureCreatedAsync();

                // Add metrics
                db.DailyMetrics.Add(new DailyMetric
                {
                    Date = "2026-05-24",
                    SuuntoHrv = 50,
                    SuuntoRestingHr = 60,
                    SuuntoSleepHours = 8.0,
                    SuuntoActiveCals = 0
                });

                db.DailyMetrics.Add(new DailyMetric
                {
                    Date = "2026-05-25",
                    SuuntoHrv = 55,
                    SuuntoRestingHr = 58,
                    SuuntoSleepHours = 7.5,
                    SuuntoActiveCals = 100
                });

                // Add workout
                db.Workouts.Add(new Workout
                {
                    WorkoutId = "W1",
                    Date = "2026-05-25",
                    TimestampStart = "2026-05-25T08:00:00Z",
                    TimestampEnd = "2026-05-25T09:00:00Z",
                    HevyWorkoutName = "Leg Day",
                    SuuntoActivityId = "S1",
                    AvgHeartRate = 140,
                    MaxHeartRate = 180,
                    StrainScore = 0 // Will trigger TRIMP fallback since it is 0
                });

                await db.SaveChangesAsync();

                // Run ReadinessEngine
                var engine = new ReadinessEngine();
                await engine.UpdateReadinessHistoryAsync(db);

                var metrics = await db.DailyMetrics.ToListAsync();
                Assert.Equal(2, metrics.Count);

                var day1 = metrics[0];
                var day2 = metrics[1];

                // Day 1 has no previous days, so HRV history is empty.
                // HRV score: baseline mean defaults to today's (50), Z = 0, score = 80
                // RHR score: mean defaults to today's (60), Z = 0, score = 80
                // Sleep score: 8/8 = 100
                // Recovery: 0.5 * 80 + 0.3 * 100 + 0.2 * 80 = 86
                // Yesterday strain: 0
                // Readiness: 86 * 1.0 = 86
                Assert.True(Math.Abs(day1.RecoveryScore.Value - 86.0) < 0.1);
                Assert.True(Math.Abs(day1.ReadinessScore.Value - 86.0) < 0.1);

                // Day 2 has Day 1 as history
                Assert.True(day2.DailyStrain.Value > 0);
                Assert.True(day2.RecoveryScore.Value > 0);
                Assert.True(day2.ReadinessScore.Value > 0);
            }
        }
    }
}
