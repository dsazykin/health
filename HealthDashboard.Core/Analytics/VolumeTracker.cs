using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Core.Analytics
{
    public class VolumeTracker
    {
        public class WeeklyVolume
        {
            public string MuscleGroup { get; set; } = string.Empty;
            public string YearWeek { get; set; } = string.Empty;
            public int HardSetsCount { get; set; }
        }

        public class Exercise1RmPoint
        {
            public string Date { get; set; } = string.Empty;
            public double OneRepMax { get; set; }
        }

        public static double? CalculateEpley1Rm(double weightKg, int reps)
        {
            if (reps <= 0 || reps > 10) return null;
            return weightKg * (1.0 + reps / 30.0);
        }

        public static string GetYearWeek(DateTime date)
        {
            DateTime firstDayOfYear = new DateTime(date.Year, 1, 1);
            int daysOffset = ((int)DayOfWeek.Monday - (int)firstDayOfYear.DayOfWeek + 7) % 7;
            DateTime firstMonday = firstDayOfYear.AddDays(daysOffset);

            int weekNum;
            if (date < firstMonday)
            {
                weekNum = 0;
            }
            else
            {
                weekNum = ((date - firstMonday).Days / 7) + 1;
            }

            return $"{date.Year}-{weekNum:D2}";
        }

        public async Task<List<WeeklyVolume>> GetWeeklyHypertrophyVolumeAsync(AppDbContext db)
        {
            var sets = await db.WorkoutSets
                .Include(s => s.Workout)
                .Include(s => s.Exercise)
                .Where(s => s.IsHardSet)
                .ToListAsync();

            var grouped = sets
                .Where(s => s.Workout != null && s.Exercise != null && !string.IsNullOrEmpty(s.Workout.Date))
                .Select(s => {
                    if (DateTime.TryParse(s.Workout.Date, out var date))
                    {
                        return new { s.Exercise.TargetMuscleGroup, YearWeek = GetYearWeek(date), s.SetId };
                    }
                    return null;
                })
                .Where(x => x != null)
                .GroupBy(x => new { x!.TargetMuscleGroup, x!.YearWeek })
                .Select(g => new WeeklyVolume
                {
                    MuscleGroup = g.Key.TargetMuscleGroup,
                    YearWeek = g.Key.YearWeek,
                    HardSetsCount = g.Count()
                })
                .OrderBy(v => v.YearWeek)
                .ThenBy(v => v.MuscleGroup)
                .ToList();

            return grouped;
        }

        public async Task<List<Exercise1RmPoint>> GetExercise1RmHistoryAsync(AppDbContext db, string exerciseName)
        {
            var sets = await db.WorkoutSets
                .Include(s => s.Workout)
                .Where(s => s.ExerciseName == exerciseName && s.Reps > 0 && s.Reps <= 10)
                .ToListAsync();

            var points = sets
                .Where(s => s.Workout != null && !string.IsNullOrEmpty(s.Workout.Date))
                .Select(s => new
                {
                    s.Workout!.Date,
                    OneRepMax = CalculateEpley1Rm(s.WeightKg, s.Reps)
                })
                .Where(x => x.OneRepMax.HasValue)
                .GroupBy(x => x.Date)
                .Select(g => new Exercise1RmPoint
                {
                    Date = g.Key,
                    OneRepMax = g.Max(x => x.OneRepMax!.Value)
                })
                .OrderBy(p => p.Date)
                .ToList();

            return points;
        }
    }
}
