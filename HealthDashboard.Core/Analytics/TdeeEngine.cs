using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Core.Analytics;

public class DailyMetricDto
{
    public string Date { get; set; } = string.Empty;
    public double? WeightKg { get; set; }
    public int CronoCalsIn { get; set; }
}

public class TdeeResult
{
    public string Date { get; set; } = string.Empty;
    public double? SmoothedWeight { get; set; }
    public int Tdee { get; set; }
    public int? CalorieTarget { get; set; }
}

public class TdeeEngine
{
    /// <summary>
    /// Pure calculation logic to calculate a historical time series of TDEE and smoothed weights.
    /// Gaps in dates are handled gracefully by filling them in with carry-forward smoothed weight
    /// and 0 calorie intake before calculating windows.
    /// </summary>
    public static List<TdeeResult> CalculateTdeeSeries(
        List<DailyMetricDto> metrics,
        double alpha = 0.1,
        int defaultTdee = 2000,
        double targetRateOfChange = 0.0,
        double goalWeightKg = 0.0)
    {
        if (metrics == null || metrics.Count == 0)
        {
            return new List<TdeeResult>();
        }

        // 1. Sort the input metrics by date to ensure proper ordering and valid dates
        var sortedInput = metrics
            .Select(m => {
                if (DateOnly.TryParseExact(m.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                {
                    return new { Dto = m, Date = d };
                }
                return null;
            })
            .Where(x => x != null)
            .OrderBy(x => x!.Date)
            .ToList();

        if (sortedInput.Count == 0)
        {
            return new List<TdeeResult>();
        }

        var minDate = sortedInput.First()!.Date;
        var maxDate = sortedInput.Last()!.Date;

        // 2. Generate a contiguous list of dates from minDate to maxDate
        var dateMap = sortedInput.ToDictionary(x => x!.Date, x => x!.Dto);
        var contiguousList = new List<DailyMetricDto>();
        for (var d = minDate; d <= maxDate; d = d.AddDays(1))
        {
            var dateStr = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (dateMap.TryGetValue(d, out var existing))
            {
                contiguousList.Add(existing);
            }
            else
            {
                contiguousList.Add(new DailyMetricDto
                {
                    Date = dateStr,
                    WeightKg = null,
                    CronoCalsIn = 0
                });
            }
        }

        // 3. Compute EMA Weight Smoothing
        var smoothedWeights = new double?[contiguousList.Count];
        double? currentSmoothedWeight = null;
        for (int i = 0; i < contiguousList.Count; i++)
        {
            var weight = contiguousList[i].WeightKg;
            if (weight.HasValue)
            {
                if (!currentSmoothedWeight.HasValue)
                {
                    currentSmoothedWeight = weight.Value;
                }
                else
                {
                    currentSmoothedWeight = alpha * weight.Value + (1.0 - alpha) * currentSmoothedWeight.Value;
                }
            }
            smoothedWeights[i] = currentSmoothedWeight;
        }

        // 4. Compute TDEE for each day in the contiguous sequence
        var tdeeResultsContiguous = new List<TdeeResult>();
        for (int i = 0; i < contiguousList.Count; i++)
        {
            var todayDto = contiguousList[i];
            int computedTdee = defaultTdee;

            // TDEE is only calculated if we are at least 14 days into the continuous series (index >= 14)
            if (i >= 14)
            {
                var wSmoothedToday = smoothedWeights[i];
                var wSmoothed14DaysAgo = smoothedWeights[i - 14];

                if (wSmoothedToday.HasValue && wSmoothed14DaysAgo.HasValue)
                {
                    // Calorie completion threshold check: at least 11 of the last 14 days logged > 500 kcal
                    // The last 14 days correspond to the indices [i - 13, i]
                    int loggedDaysCount = 0;
                    long calorieSum = 0;

                    for (int j = i - 13; j <= i; j++)
                    {
                        var cals = contiguousList[j].CronoCalsIn;
                        if (cals > 500)
                        {
                            loggedDaysCount++;
                            calorieSum += cals;
                        }
                    }

                    if (loggedDaysCount >= 11)
                    {
                        double avgDailyIntake = (double)calorieSum / loggedDaysCount;
                        double weightDelta = wSmoothedToday.Value - wSmoothed14DaysAgo.Value;
                        double rawTdee = avgDailyIntake - ((weightDelta * 7700.0) / 14.0);

                        // Clamp TDEE
                        computedTdee = (int)Math.Round(rawTdee, MidpointRounding.AwayFromZero);
                        if (computedTdee < 1000) computedTdee = 1000;
                        if (computedTdee > 5000) computedTdee = 5000;
                    }
                    else
                    {
                        // Fallback to previous day's computed TDEE
                        if (tdeeResultsContiguous.Count > 0)
                        {
                            computedTdee = tdeeResultsContiguous[^1].Tdee;
                        }
                        else
                        {
                            computedTdee = defaultTdee;
                        }
                    }
                }
                else
                {
                    // Smoothed weights not available, fallback to previous day
                    if (tdeeResultsContiguous.Count > 0)
                    {
                        computedTdee = tdeeResultsContiguous[^1].Tdee;
                    }
                    else
                    {
                        computedTdee = defaultTdee;
                    }
                }
            }
            else
            {
                // Fewer than 14 days since the first record, carry forward or use default
                if (tdeeResultsContiguous.Count > 0)
                {
                    computedTdee = tdeeResultsContiguous[^1].Tdee;
                }
                else
                {
                    computedTdee = defaultTdee;
                }
            }

            tdeeResultsContiguous.Add(new TdeeResult
            {
                Date = todayDto.Date,
                SmoothedWeight = smoothedWeights[i],
                Tdee = computedTdee
            });
        }

        // 5. Calculate CalorieTarget for each contiguous day
        int? currentCalorieTarget = null;
        for (int i = 0; i < tdeeResultsContiguous.Count; i++)
        {
            var dayResult = tdeeResultsContiguous[i];
            
            // Weekly check-in calibration: every 7 days (index 0, 7, 14, 21, etc.)
            if (i % 7 == 0)
            {
                double rateOfChangeToUse = targetRateOfChange;
                if (goalWeightKg > 0 && dayResult.SmoothedWeight.HasValue)
                {
                    double currentWeight = dayResult.SmoothedWeight.Value;
                    if (targetRateOfChange < 0 && currentWeight <= goalWeightKg)
                    {
                        rateOfChangeToUse = 0.0;
                    }
                    else if (targetRateOfChange > 0 && currentWeight >= goalWeightKg)
                    {
                        rateOfChangeToUse = 0.0;
                    }
                }

                double rawCalorieTarget = dayResult.Tdee + ((rateOfChangeToUse * 7700.0) / 7.0);
                int rawTargetInt = (int)Math.Round(rawCalorieTarget, MidpointRounding.AwayFromZero);

                if (!currentCalorieTarget.HasValue)
                {
                    currentCalorieTarget = Math.Clamp(rawTargetInt, 1000, 5000);
                }
                else
                {
                    // Shift target gradually: clamp the change to a maximum of 100 kcal
                    int previousTarget = currentCalorieTarget.Value;
                    int calibratedTarget = Math.Clamp(rawTargetInt, previousTarget - 100, previousTarget + 100);
                    currentCalorieTarget = Math.Clamp(calibratedTarget, 1000, 5000);
                }
            }
            
            dayResult.CalorieTarget = currentCalorieTarget;
        }

        // 6. Map the contiguous results back to a dictionary for rapid lookup by date
        var resultsMap = tdeeResultsContiguous.ToDictionary(r => r.Date, r => r);

        // 7. Return results matching the original input list in their original order
        var finalResults = new List<TdeeResult>();
        foreach (var originalMetric in metrics)
        {
            if (resultsMap.TryGetValue(originalMetric.Date, out var res))
            {
                finalResults.Add(res);
            }
            else
            {
                // Safe fallback if date parsing failed in the first step
                finalResults.Add(new TdeeResult
                {
                    Date = originalMetric.Date,
                    SmoothedWeight = null,
                    Tdee = defaultTdee,
                    CalorieTarget = null
                });
            }
        }

        return finalResults;
    }

    /// <summary>
    /// SQLite Database-Facing Orchestration Method.
    /// Reads configs for custom parameters, runs the engine, and updates the SQLite DB.
    /// </summary>
    public async Task UpdateTdeeHistoryAsync(AppDbContext db)
    {
        // Retrieve all DailyMetrics ordered by date
        var allMetrics = await db.DailyMetrics.OrderBy(m => m.Date).ToListAsync();
        if (allMetrics.Count == 0)
        {
            return;
        }

        // Fetch config options
        var alphaConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "TdeeWeightEmaAlpha");
        double alpha = 0.1;
        if (alphaConfig != null && double.TryParse(alphaConfig.Value, CultureInfo.InvariantCulture, out double parsedAlpha))
        {
            alpha = parsedAlpha;
        }

        var defaultTdeeConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "DefaultTdee");
        int defaultTdee = 2000;
        if (defaultTdeeConfig != null && int.TryParse(defaultTdeeConfig.Value, out int parsedTdee))
        {
            defaultTdee = parsedTdee;
        }

        var targetRateOfChangeConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "TargetRateOfChange");
        double targetRateOfChange = 0.0;
        if (targetRateOfChangeConfig != null && double.TryParse(targetRateOfChangeConfig.Value, CultureInfo.InvariantCulture, out double parsedRate))
        {
            targetRateOfChange = parsedRate;
        }

        var goalWeightKgConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "GoalWeightKg");
        double goalWeightKg = 0.0;
        if (goalWeightKgConfig != null && double.TryParse(goalWeightKgConfig.Value, CultureInfo.InvariantCulture, out double parsedGoalWeight))
        {
            goalWeightKg = parsedGoalWeight;
        }

        // Map to DTOs
        var dtos = allMetrics.Select(m => new DailyMetricDto
        {
            Date = m.Date,
            WeightKg = m.WeightKg,
            CronoCalsIn = m.CronoCalsIn
        }).ToList();

        // Calculate TDEE and calorie targets
        var results = CalculateTdeeSeries(dtos, alpha, defaultTdee, targetRateOfChange, goalWeightKg);

        // Map back and update
        var resultsMap = results.ToDictionary(r => r.Date, r => r);

        // Inside transaction, update all matching daily metrics
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            foreach (var metric in allMetrics)
            {
                if (resultsMap.TryGetValue(metric.Date, out var result))
                {
                    metric.TdeeCalculated = result.Tdee;
                    metric.CalorieTarget = result.CalorieTarget;
                }
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }
}
