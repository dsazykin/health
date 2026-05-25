using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Core.Analytics
{
    public class ReadinessEngine
    {
        public static double CalculateCvStrain(bool isStitched, double? tss, double avgHr, double maxHr, double durationMinutes)
        {
            if (!isStitched) return 0;
            if (tss.HasValue && tss.Value > 0)
            {
                return Math.Min(100, tss.Value);
            }
            if (maxHr > 0 && avgHr > 0 && durationMinutes > 0)
            {
                double trimp = durationMinutes * (avgHr / maxHr) * 1.5;
                return 100 * (1 - Math.Exp(-0.015 * trimp));
            }
            return 0;
        }

        public static double CalculateMsStrain(double totalVolume, double avgRpe)
        {
            if (totalVolume <= 0) return 0;
            double rpeToUse = avgRpe <= 0 ? 7.0 : avgRpe;
            return 100 * (1 - Math.Exp(-0.00015 * totalVolume * (rpeToUse / 10.0)));
        }

        public static double CalculateCompositeWorkoutStrain(bool hasCv, double cvStrain, bool hasMs, double msStrain)
        {
            if (hasCv && hasMs)
            {
                return 0.6 * cvStrain + 0.4 * msStrain;
            }
            if (hasCv)
            {
                return cvStrain;
            }
            if (hasMs)
            {
                return msStrain;
            }
            return 0;
        }

        public static double CalculateDailyStrain(IEnumerable<double> workoutStrains, int suuntoActiveCals)
        {
            var strains = workoutStrains?.ToList() ?? new List<double>();
            if (strains.Count > 0)
            {
                double sumSq = strains.Sum(s => s * s);
                return Math.Min(100, Math.Sqrt(sumSq));
            }
            if (suuntoActiveCals > 0)
            {
                return Math.Min(100, 10 + (suuntoActiveCals / 25.0));
            }
            return 0;
        }

        public static double CalculateSleepScore(double suuntoSleepHours)
        {
            return Math.Max(0, Math.Min(100, (suuntoSleepHours / 8.0) * 100.0));
        }

        public static double CalculateHrvScore(double hrvToday, IEnumerable<double> historicalHrvs)
        {
            if (hrvToday <= 0) return 70.0;
            var history = historicalHrvs?.Where(h => h > 0).ToList() ?? new List<double>();
            
            double mean = history.Count > 0 ? history.Average() : hrvToday;
            
            double stdDev = 10.0;
            if (history.Count >= 7)
            {
                double sumSqDiff = history.Sum(h => Math.Pow(h - mean, 2));
                stdDev = Math.Sqrt(sumSqDiff / (history.Count - 1));
            }
            
            double zHrv = (hrvToday - mean) / Math.Max(1.0, stdDev);
            return Math.Max(0, Math.Min(100, 80 + zHrv * 15.0));
        }

        public static double CalculateRhrScore(double rhrToday, IEnumerable<double> historicalRhrs)
        {
            if (rhrToday <= 0) return 70.0;
            var history = historicalRhrs?.Where(r => r > 0).ToList() ?? new List<double>();
            
            double mean = history.Count > 0 ? history.Average() : rhrToday;
            
            double zRhr = (mean - rhrToday) / 10.0;
            return Math.Max(0, Math.Min(100, 80 + zRhr * 15.0));
        }

        public static double CalculateCompositeRecovery(double hrvScore, double sleepScore, double rhrScore)
        {
            return 0.5 * hrvScore + 0.3 * sleepScore + 0.2 * rhrScore;
        }

        public static double CalculateReadiness(double recoveryToday, double dailyStrainYesterday)
        {
            return Math.Max(0, Math.Min(100, recoveryToday * (1.0 - 0.3 * (dailyStrainYesterday / 100.0))));
        }

        public static string GetActionableAdvice(double readinessScore)
        {
            if (readinessScore >= 80.0)
            {
                return "Readiness is optimal. Excellent recovery and low residual strain. You are fully primed for high-intensity training, maximum volume, or testing a new 1RM.";
            }
            else if (readinessScore >= 50.0)
            {
                return "Readiness is moderate. Balanced physical state. Moderate volume strength training or aerobic base work is appropriate. Monitor fatigue and prioritize form.";
            }
            else
            {
                return "Readiness is low. High residual strain or insufficient recovery. We recommend a dedicated active recovery day (light mobility, zone 1 walk), a deload session, or complete rest to allow adaptation.";
            }
        }

        public async Task UpdateReadinessHistoryAsync(AppDbContext db)
        {
            // 1. Fetch all workouts and daily metrics ordered chronologically
            var allMetrics = await db.DailyMetrics
                .OrderBy(m => m.Date)
                .ToListAsync();

            var allWorkouts = await db.Workouts
                .Include(w => w.WorkoutSets)
                .ToListAsync();

            if (allMetrics.Count == 0) return;

            var workoutsByDate = allWorkouts
                .GroupBy(w => w.Date)
                .ToDictionary(g => g.Key, g => g.ToList());

            // 2. Compute WorkoutStrainScore for all workouts
            foreach (var workout in allWorkouts)
            {
                double cvStrain = 0;
                bool hasCv = !string.IsNullOrEmpty(workout.SuuntoActivityId);
                if (hasCv)
                {
                    double durationMins = 0;
                    if (DateTimeOffset.TryParse(workout.TimestampEnd, out var end) && DateTimeOffset.TryParse(workout.TimestampStart, out var start))
                    {
                        durationMins = (end - start).TotalMinutes;
                    }
                    
                    double rawTrimp = 0;
                    if (workout.MaxHeartRate > 0)
                    {
                        rawTrimp = durationMins * ((double)workout.AvgHeartRate / workout.MaxHeartRate) * 1.5;
                    }

                    bool isTrimpFallback = false;
                    if (workout.MaxHeartRate > 0 && workout.AvgHeartRate > 0 && durationMins > 0)
                    {
                        if (workout.StrainScore == 0 || Math.Abs(workout.StrainScore - rawTrimp) <= 1.01)
                        {
                            isTrimpFallback = true;
                        }
                    }

                    if (isTrimpFallback)
                    {
                        cvStrain = 100 * (1 - Math.Exp(-0.015 * rawTrimp));
                    }
                    else
                    {
                        cvStrain = Math.Min(100, workout.StrainScore);
                    }
                }

                double totalVolume = 0;
                double avgRpe = 7.0;
                bool hasMs = workout.WorkoutSets != null && workout.WorkoutSets.Count > 0;
                if (hasMs)
                {
                    totalVolume = workout.WorkoutSets.Sum(s => s.WeightKg * s.Reps);
                    double rpeSum = 0;
                    int rpeCount = 0;
                    foreach (var s in workout.WorkoutSets)
                    {
                        double rpe = s.Rpe <= 0 ? 7.0 : s.Rpe;
                        rpeSum += rpe;
                        rpeCount++;
                    }
                    avgRpe = rpeCount > 0 ? rpeSum / rpeCount : 7.0;
                }

                double msStrain = CalculateMsStrain(totalVolume, avgRpe);
                workout.WorkoutStrainScore = CalculateCompositeWorkoutStrain(hasCv, cvStrain, hasMs, msStrain);
            }

            // 3. Compute Recovery and Readiness for DailyMetrics
            await using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                for (int i = 0; i < allMetrics.Count; i++)
                {
                    var today = allMetrics[i];
                    
                    // Daily Strain
                    if (workoutsByDate.TryGetValue(today.Date, out var todaysWorkouts) && todaysWorkouts.Count > 0)
                    {
                        today.DailyStrain = CalculateDailyStrain(todaysWorkouts.Select(w => w.WorkoutStrainScore ?? 0.0), 0);
                    }
                    else
                    {
                        today.DailyStrain = CalculateDailyStrain(new double[0], today.SuuntoActiveCals);
                    }

                    // Baselines logic
                    if (DateOnly.TryParse(today.Date, out var todayDate))
                    {
                        var history = new List<DailyMetric>();
                        for (int j = i - 1; j >= 0; j--)
                        {
                            var prev = allMetrics[j];
                            if (DateOnly.TryParse(prev.Date, out var prevDate))
                            {
                                var daysDiff = todayDate.DayNumber - prevDate.DayNumber;
                                if (daysDiff > 60) break; // Sorted ascending, so we can break early
                                if (daysDiff > 0)
                                {
                                    history.Add(prev);
                                }
                            }
                        }

                        var hrvHistory = history.Where(m => m.SuuntoHrv > 0).Select(m => m.SuuntoHrv).ToList();
                        double hrvScore = CalculateHrvScore(today.SuuntoHrv, hrvHistory);

                        var rhrHistory = history.Where(m => m.SuuntoRestingHr > 0).Select(m => (double)m.SuuntoRestingHr).ToList();
                        double rhrScore = CalculateRhrScore(today.SuuntoRestingHr, rhrHistory);

                        double sleepScore = CalculateSleepScore(today.SuuntoSleepHours);

                        today.RecoveryScore = CalculateCompositeRecovery(hrvScore, sleepScore, rhrScore);

                        double yesterdayStrain = 0;
                        if (i > 0)
                        {
                            var prev = allMetrics[i - 1];
                            if (DateOnly.TryParse(prev.Date, out var prevDate))
                            {
                                if (todayDate.DayNumber - prevDate.DayNumber == 1)
                                {
                                    yesterdayStrain = prev.DailyStrain ?? 0.0;
                                }
                            }
                        }

                        today.ReadinessScore = CalculateReadiness(today.RecoveryScore ?? 0.0, yesterdayStrain);
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
}
