using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HealthDashboard.Core;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.Analytics;

namespace HealthDashboard.Tests;

public class TdeeEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public TdeeEngineTests()
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
    public void TestEmaInitializer()
    {
        // Arrange
        var metrics = new List<DailyMetricDto>
        {
            new() { Date = "2026-05-01", WeightKg = null, CronoCalsIn = 2000 },
            new() { Date = "2026-05-02", WeightKg = 80.0, CronoCalsIn = 2000 },
            new() { Date = "2026-05-03", WeightKg = 82.0, CronoCalsIn = 2000 }
        };

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1);

        // Assert
        Assert.Equal(3, results.Count);
        // Day 1 has null weight, so smoothed should be null
        Assert.Null(results[0].SmoothedWeight);
        // Day 2 is the first actual weight log, so it initializes the EMA to 80.0
        Assert.Equal(80.0, results[1].SmoothedWeight);
        // Day 3 uses the recursive EMA formula: 0.1 * 82.0 + 0.9 * 80.0 = 8.2 + 72.0 = 80.2
        Assert.True(Math.Abs(results[2].SmoothedWeight!.Value - 80.2) < 1e-6);
    }

    [Fact]
    public void TestWaterWeightFiltering()
    {
        // Arrange
        // Initial weight 80.0, then a sudden water spike to 83.0 and then 83.0, but stable tissue.
        // We have 15 days of metrics to verify the TDEE math.
        var metrics = new List<DailyMetricDto>();
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            double weight = 80.0;
            if (i == 13 || i == 14) weight = 83.0; // Rapid sodium/water spike

            metrics.Add(new DailyMetricDto
            {
                Date = dateStr,
                WeightKg = weight,
                CronoCalsIn = 2500
            });
        }

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1);

        // Assert
        Assert.Equal(15, results.Count);
        // Smoothed weight on last day (index 14) should not be the full 83.0 spike.
        // Let's trace smoothed weight starting at 80.0:
        // Day 0: 80.0
        // Day 1-12: remains 80.0 (since raw weight is 80.0)
        // Day 13: 0.1 * 83.0 + 0.9 * 80.0 = 80.3
        // Day 14: 0.1 * 83.0 + 0.9 * 80.3 = 80.57
        Assert.True(Math.Abs(results[14].SmoothedWeight!.Value - 80.57) < 1e-6);

        // Raw weight spiked by 3.0 kg, but smoothed weight only increased by 0.57 kg, filtering water noise!
        // Weight delta = 80.57 - 80.0 = 0.57
        // Expected TDEE = 2500 - ((0.57 * 7700) / 14) = 2500 - 313.5 = 2186.5 -> round to 2187
        Assert.Equal(2187, results[14].Tdee);
    }

    [Fact]
    public void TestIntakeThreshold_FailsWhenTooFewDays()
    {
        // Arrange
        // 15 days of data, but only 10 days have calorie logs > 500 kcal in the last 14 days
        var metrics = new List<DailyMetricDto>();
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            // Calorie logs: Day 1-10 are 2000, Day 11-14 are 0 (which is <= 500)
            // The 14-day window ending on Day 14 [Day 1, Day 14] has:
            // Day 1-10 (10 days) with > 500, Day 11-14 (4 days) with 0.
            // Total logged days in window is exactly 10, which is < 11.
            int cals = (i >= 1 && i <= 10) ? 2000 : 0;

            metrics.Add(new DailyMetricDto
            {
                Date = dateStr,
                WeightKg = 80.0,
                CronoCalsIn = cals
            });
        }

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1, defaultTdee: 2200);

        // Assert
        // Day 14 (index 14) has only 10 logged calorie days, so completion threshold is not met.
        // It must fallback to the previous day's TDEE (which is 2200 since no prior calculations succeeded).
        Assert.Equal(2200, results[14].Tdee);
    }

    [Fact]
    public void TestIntakeThreshold_SucceedsWhenExactly11Days()
    {
        // Arrange
        // 15 days of data. The last 14 days [Day 1, Day 14] has 11 days with 2500 kcal, 3 days with 0 kcal.
        var metrics = new List<DailyMetricDto>();
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            // Day 1 to 11 are 2500, Day 12 to 14 are 0
            // Window [Day 1, Day 14] has exactly 11 logged days (Day 1 to 11)
            int cals = (i >= 1 && i <= 11) ? 2500 : 0;

            metrics.Add(new DailyMetricDto
            {
                Date = dateStr,
                WeightKg = 80.0,
                CronoCalsIn = cals
            });
        }

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1);

        // Assert
        // Smoothed weight delta = 80 - 80 = 0
        // Average Daily Intake = 2500 (since we scale dynamically by dividing sum of logged days by 11)
        // Expected TDEE = 2500 - 0 = 2500
        Assert.Equal(2500, results[14].Tdee);
    }

    [Fact]
    public void TestMissingDayInterpolation()
    {
        // Arrange
        // Metric data with a missing date gap of 1 day between Day 1 and Day 3
        var metrics = new List<DailyMetricDto>
        {
            new() { Date = "2026-05-01", WeightKg = 80.0, CronoCalsIn = 2000 },
            new() { Date = "2026-05-03", WeightKg = 81.0, CronoCalsIn = 2000 }
        };

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1);

        // Assert
        Assert.Equal(2, results.Count);
        // May 1 (index 0)
        Assert.Equal(80.0, results[0].SmoothedWeight);
        // May 3 (index 1) - May 2 is interpolated as missing weight (retains 80.0)
        // Then May 3: 0.1 * 81.0 + 0.9 * 80.0 = 80.1
        Assert.True(Math.Abs(results[1].SmoothedWeight!.Value - 80.1) < 1e-6);
    }

    [Fact]
    public void TestDynamicScaling()
    {
        // Arrange
        // 15 days of data. The last 14 days [Day 1, Day 14] has 12 logged days of 2500 kcal, 2 days with 0.
        // Dynamic Scaling should calculate Average Daily Intake as 2500 kcal (instead of deflating it to 2142 kcal).
        var metrics = new List<DailyMetricDto>();
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            int cals = (i >= 1 && i <= 12) ? 2500 : 0;

            metrics.Add(new DailyMetricDto
            {
                Date = dateStr,
                WeightKg = 80.0,
                CronoCalsIn = cals
            });
        }

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1);

        // Assert
        // Weight delta = 0, average intake = 2500
        Assert.Equal(2500, results[14].Tdee);
    }

    [Fact]
    public void TestTdeeClamping()
    {
        // Arrange
        // Mock a massive weight loss delta to force a very high TDEE calculation, which should clamp to 5000 kcal.
        var metrics = new List<DailyMetricDto>();
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            double weight = 80.0 - (i * 2.0); // Rapid weight loss: -28kg in 14 days!

            metrics.Add(new DailyMetricDto
            {
                Date = dateStr,
                WeightKg = weight,
                CronoCalsIn = 4000
            });
        }

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1);

        // Assert
        // TDEE is clamped to 5000
        Assert.Equal(5000, results[14].Tdee);

        // Mock a massive weight gain delta to force a very low TDEE, which should clamp to 1000 kcal.
        var metricsGain = new List<DailyMetricDto>();
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            double weight = 80.0 + (i * 2.0); // Rapid weight gain: +28kg in 14 days!

            metricsGain.Add(new DailyMetricDto
            {
                Date = dateStr,
                WeightKg = weight,
                CronoCalsIn = 1500
            });
        }

        // Act
        var resultsGain = TdeeEngine.CalculateTdeeSeries(metricsGain, alpha: 0.1);

        // Assert
        // TDEE is clamped to 1000
        Assert.Equal(1000, resultsGain[14].Tdee);
    }

    [Fact]
    public async Task TestDatabaseUpdate()
    {
        // Arrange
        using var db = new AppDbContext(_options);
        DbInitializer.Initialize(db);

        // Seed some configs
        db.Configs.AddRange(
            new Config { Key = "TdeeWeightEmaAlpha", Value = "0.1" },
            new Config { Key = "DefaultTdee", Value = "2200" }
        );

        // Seed 15 consecutive DailyMetrics
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            db.DailyMetrics.Add(new DailyMetric
            {
                Date = dateStr,
                WeightKg = 80.0,
                CronoCalsIn = 2500,
                TdeeCalculated = 0
            });
        }
        await db.SaveChangesAsync();

        // Act
        var engine = new TdeeEngine();
        await engine.UpdateTdeeHistoryAsync(db);

        // Assert
        var updatedMetrics = await db.DailyMetrics.OrderBy(m => m.Date).ToListAsync();
        Assert.Equal(15, updatedMetrics.Count);
        // The first 13 days should have TDEE set to default 2200
        for (int i = 0; i < 14; i++)
        {
            Assert.Equal(2200, updatedMetrics[i].TdeeCalculated);
        }
        // The 15th day (index 14) has a valid 14-day history, so it should calculate 2500
        Assert.Equal(2500, updatedMetrics[14].TdeeCalculated);
    }

    [Fact]
    public void TestCalorieTargetCalculation()
    {
        // Arrange
        var metrics = new List<DailyMetricDto>
        {
            new() { Date = "2026-05-01", WeightKg = 80.0, CronoCalsIn = 2000 }
        };

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metrics, alpha: 0.1, defaultTdee: 2000, targetRateOfChange: 0.5, goalWeightKg: 0.0);

        // Assert
        Assert.Single(results);
        // Raw target: 2000 + ((0.5 * 7700) / 7) = 2000 + 550 = 2550
        Assert.Equal(2550, results[0].CalorieTarget);
    }

    [Fact]
    public void TestGoalWeightMet_StopsDeficitOrSurplus()
    {
        // Arrange: Goal weight 75.0, current weight 74.0, target change -0.5 (cut, but goal met!)
        var metricsCut = new List<DailyMetricDto>
        {
            new() { Date = "2026-05-01", WeightKg = 74.0, CronoCalsIn = 2000 }
        };

        // Act
        var resultsCut = TdeeEngine.CalculateTdeeSeries(metricsCut, alpha: 0.1, defaultTdee: 2000, targetRateOfChange: -0.5, goalWeightKg: 75.0);

        // Assert: Goal met, target rate of change should be 0.0, CalorieTarget = Tdee
        Assert.Equal(2000, resultsCut[0].CalorieTarget);

        // Arrange: Goal weight 85.0, current weight 86.0, target change +0.5 (bulk, but goal met!)
        var metricsBulk = new List<DailyMetricDto>
        {
            new() { Date = "2026-05-01", WeightKg = 86.0, CronoCalsIn = 2000 }
        };

        // Act
        var resultsBulk = TdeeEngine.CalculateTdeeSeries(metricsBulk, alpha: 0.1, defaultTdee: 2000, targetRateOfChange: 0.5, goalWeightKg: 85.0);

        // Assert: Goal met, target rate of change should be 0.0, CalorieTarget = Tdee
        Assert.Equal(2000, resultsBulk[0].CalorieTarget);
    }

    [Fact]
    public void TestWeeklyCheckInCalibrationAndSmoothing()
    {
        // Arrange
        var metricsTdeeChange = new List<DailyMetricDto>();
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            double weight = 80.0;
            if (i == 14) weight = 70.0; // Sudden weight loss to spike calculated TDEE

            metricsTdeeChange.Add(new DailyMetricDto
            {
                Date = baseDate.AddDays(i).ToString("yyyy-MM-dd"),
                WeightKg = weight,
                CronoCalsIn = 3000
            });
        }

        // Act
        var results = TdeeEngine.CalculateTdeeSeries(metricsTdeeChange, alpha: 0.1, defaultTdee: 2000, targetRateOfChange: 0.5, goalWeightKg: 0.0);

        // Assert
        // Day 0: check-in. TDEE = 2000. CalorieTarget = 2000 + 550 = 2550.
        Assert.Equal(2550, results[0].CalorieTarget);

        // Day 1 to 6: carried forward
        for (int i = 1; i <= 6; i++)
        {
            Assert.Equal(2550, results[i].CalorieTarget);
        }

        // Day 7: check-in. TDEE is still default 2000 (since i < 14). CalorieTarget = 2550.
        Assert.Equal(2550, results[7].CalorieTarget);

        // Day 8 to 13: carried forward
        for (int i = 8; i <= 13; i++)
        {
            Assert.Equal(2550, results[i].CalorieTarget);
        }

        // Day 14: check-in day!
        // Calculated TDEE will be significantly higher than 2000.
        // Raw target: Tdee + 550. This raw target will be > 2650.
        // Let's check that the actual CalorieTarget on Day 14 is exactly 2650 (2550 + 100) because it got clamped to shift gradually!
        Assert.True(results[14].Tdee > 2000);
        Assert.Equal(2650, results[14].CalorieTarget);
    }

    [Fact]
    public async Task TestDatabasePersistenceOfCalorieTarget()
    {
        // Arrange
        using var db = new AppDbContext(_options);
        DbInitializer.Initialize(db);

        // Seed some configs
        var alpha = await db.Configs.FirstOrDefaultAsync(c => c.Key == "TdeeWeightEmaAlpha");
        if (alpha == null) db.Configs.Add(new Config { Key = "TdeeWeightEmaAlpha", Value = "0.1" });
        else alpha.Value = "0.1";

        var defTdee = await db.Configs.FirstOrDefaultAsync(c => c.Key == "DefaultTdee");
        if (defTdee == null) db.Configs.Add(new Config { Key = "DefaultTdee", Value = "2000" });
        else defTdee.Value = "2000";

        var rate = await db.Configs.FirstOrDefaultAsync(c => c.Key == "TargetRateOfChange");
        if (rate == null) db.Configs.Add(new Config { Key = "TargetRateOfChange", Value = "0.5" });
        else rate.Value = "0.5";

        var goal = await db.Configs.FirstOrDefaultAsync(c => c.Key == "GoalWeightKg");
        if (goal == null) db.Configs.Add(new Config { Key = "GoalWeightKg", Value = "70.0" });
        else goal.Value = "70.0";

        // Seed 15 consecutive DailyMetrics
        var baseDate = new DateOnly(2026, 5, 1);
        for (int i = 0; i < 15; i++)
        {
            var dateStr = baseDate.AddDays(i).ToString("yyyy-MM-dd");
            db.DailyMetrics.Add(new DailyMetric
            {
                Date = dateStr,
                WeightKg = 80.0,
                CronoCalsIn = 2000,
                TdeeCalculated = 0,
                CalorieTarget = null
            });
        }
        await db.SaveChangesAsync();

        // Act
        var engine = new TdeeEngine();
        await engine.UpdateTdeeHistoryAsync(db);

        // Assert
        var updatedMetrics = await db.DailyMetrics.OrderBy(m => m.Date).ToListAsync();
        Assert.Equal(15, updatedMetrics.Count);
        
        // Target rate of change is +0.5. Goal is 70.0. Current weight is 80.0.
        // Since we are bulking (+0.5) and current weight (80) is already above goal weight (70),
        // we have reached the goal weight for a bulk!
        // Therefore, target rate of change to use should be 0.0 (maintenance), so CalorieTarget = Tdee = 2000.
        for (int i = 0; i < 15; i++)
        {
            Assert.Equal(2000, updatedMetrics[i].CalorieTarget);
        }
    }
}
