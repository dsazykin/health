using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core.Models;

namespace HealthDashboard.Core
{
    public class AppDbContext : DbContext
    {
        public DbSet<Exercise> Exercises => Set<Exercise>();
        public DbSet<DailyMetric> DailyMetrics => Set<DailyMetric>();
        public DbSet<Workout> Workouts => Set<Workout>();
        public DbSet<WorkoutSet> WorkoutSets => Set<WorkoutSet>();
        public DbSet<Config> Configs => Set<Config>();

        private readonly string? _dbPath;

        // Standard constructor for production/runtime
        public AppDbContext()
        {
            // Build absolute path to native local AppData folder
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appDir = Path.Combine(appData, "HealthDashboard");
            
            if (!Directory.Exists(appDir))
            {
                Directory.CreateDirectory(appDir);
            }
            
            _dbPath = Path.Combine(appDir, "health_dashboard.db");
        }

        // Custom constructor for testing (e.g. SQLite in-memory or custom path)
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
        {
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured && _dbPath != null)
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    ForeignKeys = true // Enable foreign keys in connection builder
                }.ToString();

                optionsBuilder.UseSqlite(connectionString);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure Exercise entity
            modelBuilder.Entity<Exercise>(entity =>
            {
                entity.HasKey(e => e.ExerciseName);
            });

            // Configure DailyMetric entity
            modelBuilder.Entity<DailyMetric>(entity =>
            {
                entity.HasKey(d => d.Date);
            });

            // Configure Workout entity
            modelBuilder.Entity<Workout>(entity =>
            {
                entity.HasKey(w => w.WorkoutId);
                
                entity.HasOne(w => w.DailyMetric)
                      .WithMany(d => d.Workouts)
                      .HasForeignKey(w => w.Date)
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure WorkoutSet entity
            modelBuilder.Entity<WorkoutSet>(entity =>
            {
                entity.HasKey(s => s.SetId);

                entity.HasOne(s => s.Workout)
                      .WithMany(w => w.WorkoutSets)
                      .HasForeignKey(s => s.WorkoutId)
                      .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(s => s.Exercise)
                      .WithMany(e => e.WorkoutSets)
                      .HasForeignKey(s => s.ExerciseName)
                      .OnDelete(DeleteBehavior.Restrict); // Prevent deleting an exercise if it has associated sets
            });

            // Configure Config entity
            modelBuilder.Entity<Config>(entity =>
            {
                entity.HasKey(c => c.Key);
            });
        }
    }
}
