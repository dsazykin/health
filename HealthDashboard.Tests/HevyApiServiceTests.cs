using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HealthDashboard.Core;
using HealthDashboard.Core.Models;
using HealthDashboard.Core.HevyApi;

namespace HealthDashboard.Tests
{
    public class HevyApiServiceTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public HevyApiServiceTests()
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

        // A simple, pure HttpMessageHandler mock to avoid external testing library dependencies
        private class MockHttpMessageHandler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, Task<HttpResponseMessage>> SendAsyncFunc { get; set; } = null!;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return SendAsyncFunc(request);
            }
        }

        [Fact]
        public async Task TestValidateApiKey_Success()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    Assert.Equal("https://api.hevyapp.com/v1/user/info", req.RequestUri?.ToString());
                    Assert.Equal("test-api-key", req.Headers.GetValues("api-key").FirstOrDefault());
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }
            };

            var client = new HttpClient(mockHandler);
            var service = new HevyApiService(client);

            var result = await service.ValidateApiKeyAsync("test-api-key");
            Assert.True(result);
        }

        [Fact]
        public async Task TestValidateApiKey_Failure()
        {
            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
            };

            var client = new HttpClient(mockHandler);
            var service = new HevyApiService(client);

            var result = await service.ValidateApiKeyAsync("bad-key");
            Assert.False(result);
        }

        [Fact]
        public async Task TestSyncWorkouts_Success_ImportsAndSeedsCorrectly()
        {
            using var context = new AppDbContext(_options);
            DbInitializer.Initialize(context);

            var apiWorkout = new HevyApiWorkout
            {
                Id = "api-workout-uuid-1",
                Title = "Leg Day",
                StartTime = "2026-05-20T14:30:00Z",
                EndTime = "2026-05-20T15:30:00Z",
                Exercises = new List<HevyApiExercise>
                {
                    new HevyApiExercise
                    {
                        Index = 0,
                        Title = "Squat (Barbell)", // Seeded exercise
                        Notes = "Heavy sets",
                        ExerciseTemplateId = "squat-template-1",
                        Sets = new List<HevyApiSet>
                        {
                            new HevyApiSet
                            {
                                Index = 0,
                                Type = "normal",
                                WeightKg = 100,
                                Reps = 8,
                                Rpe = 8
                            }
                        }
                    },
                    new HevyApiExercise
                    {
                        Index = 1,
                        Title = "New Brand Lift", // Unseeded exercise
                        Notes = "Trying out new machine",
                        ExerciseTemplateId = "brand-template-1",
                        Sets = new List<HevyApiSet>
                        {
                            new HevyApiSet
                            {
                                Index = 0,
                                Type = "failure", // Trigger hard set
                                WeightKg = 50,
                                Reps = 12,
                                Rpe = 9
                            }
                        }
                    }
                }
            };

            var workoutResponse = new HevyWorkoutResponse
            {
                Page = 1,
                PageCount = 1,
                Workouts = new List<HevyApiWorkout> { apiWorkout }
            };

            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(workoutResponse), Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                }
            };

            var client = new HttpClient(mockHandler);
            var service = new HevyApiService(client);

            var result = await service.SyncWorkoutsAsync(context, "key");

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Skipped);

            // Assert custom exercise automatically added
            Assert.Contains("New Brand Lift", result.NewExercises!);
            var dbExercise = context.Exercises.FirstOrDefault(e => e.ExerciseName == "New Brand Lift");
            Assert.NotNull(dbExercise);
            Assert.Equal("Other", dbExercise.TargetMuscleGroup);

            // Assert workout imported with native UUID
            var workout = context.Workouts.Include(w => w.WorkoutSets).FirstOrDefault(w => w.WorkoutId == "api-workout-uuid-1");
            Assert.NotNull(workout);
            Assert.Equal("Leg Day", workout.HevyWorkoutName);
            Assert.Equal("2026-05-20", workout.Date);
            Assert.Equal(2, workout.WorkoutSets.Count);

            // Assert Set properties mapped
            var squatSet = workout.WorkoutSets.FirstOrDefault(s => s.ExerciseName == "Squat (Barbell)");
            Assert.NotNull(squatSet);
            Assert.Equal(100, squatSet.WeightKg);
            Assert.Equal(8, squatSet.Reps);
            Assert.Equal(8, squatSet.Rpe);
            Assert.True(squatSet.IsHardSet); // Rpe >= 7

            var brandSet = workout.WorkoutSets.FirstOrDefault(s => s.ExerciseName == "New Brand Lift");
            Assert.NotNull(brandSet);
            Assert.Equal(50, brandSet.WeightKg);
            Assert.Equal(12, brandSet.Reps);
            Assert.True(brandSet.IsHardSet); // failure set type

            // Assert DailyMetric skeletal record was generated
            var metric = context.DailyMetrics.FirstOrDefault(m => m.Date == "2026-05-20");
            Assert.NotNull(metric);
        }

        [Fact]
        public async Task TestSyncWorkouts_DateRangeFiltering()
        {
            using var context = new AppDbContext(_options);
            DbInitializer.Initialize(context);

            var workoutResponse = new HevyWorkoutResponse
            {
                Page = 1,
                PageCount = 1,
                Workouts = new List<HevyApiWorkout>
                {
                    new HevyApiWorkout { Id = "w1", Title = "Newest", StartTime = "2026-05-20T10:00:00Z", Exercises = new() },
                    new HevyApiWorkout { Id = "w2", Title = "Middle", StartTime = "2026-05-15T10:00:00Z", Exercises = new() },
                    new HevyApiWorkout { Id = "w3", Title = "Oldest", StartTime = "2026-05-10T10:00:00Z", Exercises = new() }
                }
            };

            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(workoutResponse), Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                }
            };

            var client = new HttpClient(mockHandler);
            var service = new HevyApiService(client);

            // Fetch since May 12, 2026 (excludes w3 entirely and should stop page traversing early)
            var result = await service.SyncWorkoutsAsync(context, "key", sinceDate: new DateTime(2026, 5, 12, 0, 0, 0, DateTimeKind.Utc));

            Assert.Equal(2, result.Imported); // Only w1 and w2
            Assert.NotNull(context.Workouts.FirstOrDefault(w => w.WorkoutId == "w1"));
            Assert.NotNull(context.Workouts.FirstOrDefault(w => w.WorkoutId == "w2"));
            Assert.Null(context.Workouts.FirstOrDefault(w => w.WorkoutId == "w3"));
        }

        [Fact]
        public async Task TestSyncWorkouts_DeduplicatesCsvWorkouts()
        {
            using var context = new AppDbContext(_options);
            DbInitializer.Initialize(context);

            // 1. Add DailyMetric
            var metric = new DailyMetric { Date = "2026-05-22", WeightKg = 80.0 };
            context.DailyMetrics.Add(metric);

            // 2. Pre-populate a CSV-imported workout with deterministic hash ID
            var csvWorkoutId = "deterministic-hash-123456";
            var csvWorkout = new Workout
            {
                WorkoutId = csvWorkoutId,
                Date = "2026-05-22",
                TimestampStart = "2026-05-22T12:57:00Z",
                TimestampEnd = "2026-05-22T13:30:00Z",
                HevyWorkoutName = "Chest and shoulders"
            };
            context.Workouts.Add(csvWorkout);

            var csvSet = new WorkoutSet
            {
                WorkoutId = csvWorkoutId,
                ExerciseName = "Bench Press (Barbell)",
                SetOrder = 1,
                WeightKg = 80,
                Reps = 5
            };
            context.WorkoutSets.Add(csvSet);
            await context.SaveChangesAsync();

            // Confirm CSV record exists
            Assert.NotNull(context.Workouts.FirstOrDefault(w => w.WorkoutId == csvWorkoutId));
            Assert.Equal(1, context.WorkoutSets.Count(s => s.WorkoutId == csvWorkoutId));

            // 3. Mock Hevy API returning the same workout with stable UUID "chest-shoulders-api-uuid"
            var apiWorkout = new HevyApiWorkout
            {
                Id = "chest-shoulders-api-uuid",
                Title = "Chest and shoulders",
                StartTime = "2026-05-22T12:57:00Z",
                EndTime = "2026-05-22T13:30:00Z",
                Exercises = new List<HevyApiExercise>
                {
                    new HevyApiExercise
                    {
                        Index = 0,
                        Title = "Bench Press (Barbell)",
                        Notes = "Good sets",
                        ExerciseTemplateId = "bench-template-id",
                        Sets = new List<HevyApiSet>
                        {
                            new HevyApiSet
                            {
                                Index = 0,
                                Type = "normal",
                                WeightKg = 85, // New stats
                                Reps = 5,
                                Rpe = 9
                            }
                        }
                    }
                }
            };

            var workoutResponse = new HevyWorkoutResponse
            {
                Page = 1,
                PageCount = 1,
                Workouts = new List<HevyApiWorkout> { apiWorkout }
            };

            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(workoutResponse), Encoding.UTF8, "application/json")
                    };
                    return Task.FromResult(response);
                }
            };

            var client = new HttpClient(mockHandler);
            var service = new HevyApiService(client);

            // Sync workouts
            var result = await service.SyncWorkoutsAsync(context, "key");

            Assert.Empty(result.Errors);
            Assert.Equal(1, result.Imported);

            // 4. Assert old CSV workout ID is COMPLETELY deleted
            var oldWorkout = context.Workouts.FirstOrDefault(w => w.WorkoutId == csvWorkoutId);
            Assert.Null(oldWorkout);
            var oldSets = context.WorkoutSets.Where(s => s.WorkoutId == csvWorkoutId).ToList();
            Assert.Empty(oldSets);

            // 5. Assert API workout with UUID "chest-shoulders-api-uuid" is present
            var newWorkout = context.Workouts.Include(w => w.WorkoutSets).FirstOrDefault(w => w.WorkoutId == "chest-shoulders-api-uuid");
            Assert.NotNull(newWorkout);
            Assert.Equal("Chest and shoulders", newWorkout.HevyWorkoutName);
            Assert.Equal(1, newWorkout.WorkoutSets.Count);

            var newSet = newWorkout.WorkoutSets.First();
            Assert.Equal(85, newSet.WeightKg); // Updated weight
            Assert.Equal(9, newSet.Rpe); // Custom RPE from API
        }
    }
}
