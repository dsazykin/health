using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HealthDashboard.Core.Models
{
    public class Exercise
    {
        [Key]
        [Required]
        public string ExerciseName { get; set; } = string.Empty;

        public string TargetMuscleGroup { get; set; } = string.Empty;

        public string MovementPattern { get; set; } = string.Empty;

        // Navigation Property
        public virtual ICollection<WorkoutSet> WorkoutSets { get; set; } = new List<WorkoutSet>();
    }

    public class DailyMetric
    {
        [Key]
        [Required]
        public string Date { get; set; } = string.Empty; // YYYY-MM-DD ISO 8601

        public double? WeightKg { get; set; }

        public double? BodyFatPercent { get; set; }

        public int TdeeCalculated { get; set; }

        public int? CalorieTarget { get; set; }

        public int SuuntoActiveCals { get; set; }

        public double SuuntoHrv { get; set; }

        public double SuuntoSleepHours { get; set; }

        public int SuuntoRestingHr { get; set; }

        public int CronoCalsIn { get; set; }

        public int CronoProteinG { get; set; }

        public int CronoCarbsG { get; set; }

        public int CronoFatG { get; set; }

        // Navigation Property
        public virtual ICollection<Workout> Workouts { get; set; } = new List<Workout>();
    }

    public class Workout
    {
        [Key]
        [Required]
        public string WorkoutId { get; set; } = string.Empty; // Deterministic Hash of TimestampStart + HevyWorkoutName

        [Required]
        [ForeignKey(nameof(DailyMetric))]
        public string Date { get; set; } = string.Empty; // YYYY-MM-DD ISO 8601

        [Required]
        public string TimestampStart { get; set; } = string.Empty; // ISO 8601 UTC

        [Required]
        public string TimestampEnd { get; set; } = string.Empty; // ISO 8601 UTC

        [Required]
        public string HevyWorkoutName { get; set; } = string.Empty;

        public string? SuuntoActivityId { get; set; }

        public int AvgHeartRate { get; set; }

        public int MaxHeartRate { get; set; }

        public int StrainScore { get; set; }

        // Navigation Properties
        public virtual DailyMetric? DailyMetric { get; set; }

        public virtual ICollection<WorkoutSet> WorkoutSets { get; set; } = new List<WorkoutSet>();
    }

    public class WorkoutSet
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int SetId { get; set; }

        [Required]
        [ForeignKey(nameof(Workout))]
        public string WorkoutId { get; set; } = string.Empty;

        [Required]
        [ForeignKey(nameof(Exercise))]
        public string ExerciseName { get; set; } = string.Empty;

        public int SetOrder { get; set; }

        public double WeightKg { get; set; }

        public int Reps { get; set; }

        public double Rpe { get; set; }

        public bool IsHardSet { get; set; }

        // Navigation Properties
        public virtual Workout? Workout { get; set; }

        public virtual Exercise? Exercise { get; set; }
    }

    public class Config
    {
        [Key]
        [Required]
        public string Key { get; set; } = string.Empty;

        [Required]
        public string Value { get; set; } = string.Empty;
    }
}
