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
using HealthDashboard.Core.SuuntoApi;
using HealthDashboard.Core.Security;

namespace HealthDashboard.Tests
{
    public class SuuntoIngestionServiceTests : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;

        public SuuntoIngestionServiceTests()
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

        private class MockHttpMessageHandler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, Task<HttpResponseMessage>> SendAsyncFunc { get; set; } = null!;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return SendAsyncFunc(request);
            }
        }

        private class MockSuuntoOAuthService : SuuntoOAuthService
        {
            public string? TokenToReturn { get; set; } = "mock-access-token";

            public override Task<string?> GetValidAccessTokenAsync()
            {
                return Task.FromResult(TokenToReturn);
            }
        }

        [Fact]
        public async Task TestSync_Success_StitchesWorkoutsAndUpdatesWellness()
        {
            using var context = new AppDbContext(_options);
            DbInitializer.Initialize(context);

            // Add or update config values needed
            var subKey = context.Configs.FirstOrDefault(c => c.Key == "SuuntoSubscriptionKey");
            if (subKey != null)
            {
                subKey.Value = "test-sub-key";
            }
            else
            {
                context.Configs.Add(new Config { Key = "SuuntoSubscriptionKey", Value = "test-sub-key" });
            }

            // Ensure DailyMetric exists first
            context.DailyMetrics.Add(new DailyMetric
            {
                Date = "2026-05-24",
                TdeeCalculated = 0,
                SuuntoActiveCals = 0,
                SuuntoHrv = 0,
                SuuntoSleepHours = 0,
                SuuntoRestingHr = 0,
                CronoCalsIn = 0,
                CronoProteinG = 0,
                CronoCarbsG = 0,
                CronoFatG = 0
            });

            // Add an unstitched workout
            // Start time: 2026-05-24T12:00:00Z
            var workoutId = "unstitched-workout-1";
            var workout = new Workout
            {
                WorkoutId = workoutId,
                Date = "2026-05-24",
                TimestampStart = "2026-05-24T12:00:00Z",
                TimestampEnd = "2026-05-24T13:00:00Z",
                HevyWorkoutName = "Chest & Back Strength",
                SuuntoActivityId = null,
                AvgHeartRate = 0,
                MaxHeartRate = 0,
                StrainScore = 0
            };
            context.Workouts.Add(workout);
            await context.SaveChangesAsync();

            // Mock API Responses
            // 1. Suunto activities response (matching ±15 minutes)
            // Start time: 2026-05-24T12:05:00Z (5 minutes off -> matches!)
            var activity = new SuuntoActivity
            {
                Id = "suunto-act-99",
                StartTime = "2026-05-24T12:05:00Z",
                Duration = 3600, // 60 minutes
                AverageHr = 135,
                MaxHr = 175,
                Calories = 450,
                Tss = 78
            };

            var activitiesJson = JsonSerializer.Serialize(new List<SuuntoActivity> { activity });

            // 2. Wellness response (with sleep duration, HRV, active calories)
            var wellness = new SuuntoWellnessMetric
            {
                Date = "2026-05-24",
                SleepDurationSeconds = 28800, // 8 hours
                RestingHeartRate = 54,
                Hrv = 62.5,
                ActiveCalories = 650
            };
            var wellnessJson = JsonSerializer.Serialize(new List<SuuntoWellnessMetric> { wellness });

            var callCount = 0;
            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    callCount++;
                    Assert.Equal("Bearer mock-access-token", req.Headers.Authorization?.ToString());
                    Assert.Equal("test-sub-key", req.Headers.GetValues("Ocp-Apim-Subscription-Key").FirstOrDefault());

                    if (req.RequestUri?.ToString().Contains("workouts") == true)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(activitiesJson, Encoding.UTF8, "application/json")
                        });
                    }
                    else if (req.RequestUri?.ToString().Contains("daily") == true)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(wellnessJson, Encoding.UTF8, "application/json")
                        });
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
            };

            var client = new HttpClient(mockHandler);
            var oauthMock = new MockSuuntoOAuthService();
            var service = new SuuntoIngestionService(client, oauthMock);

            // Execute Sync
            var result = await service.SyncAsync(context);

            // Verify Results
            Assert.Empty(result.Errors);
            Assert.Equal(1, result.ActivitiesFetched);
            Assert.Equal(1, result.WorkoutsStitched);
            Assert.Equal(1, result.WellnessDaysUpdated);
            Assert.Equal(2, callCount); // Verified both workouts and daily requests were fired

            // Verify workout was successfully stitched
            var updatedWorkout = await context.Workouts.FirstOrDefaultAsync(w => w.WorkoutId == workoutId);
            Assert.NotNull(updatedWorkout);
            Assert.Equal("suunto-act-99", updatedWorkout.SuuntoActivityId);
            Assert.Equal(135, updatedWorkout.AvgHeartRate);
            Assert.Equal(175, updatedWorkout.MaxHeartRate);
            Assert.Equal(78, updatedWorkout.StrainScore); // From Tss

            // Verify daily metric safe update
            var metric = await context.DailyMetrics.AsNoTracking().FirstOrDefaultAsync(m => m.Date == "2026-05-24");
            Assert.NotNull(metric);
            Assert.Equal(650, metric.SuuntoActiveCals);
            Assert.Equal(62.5, metric.SuuntoHrv);
            Assert.Equal(8.0, metric.SuuntoSleepHours);
            Assert.Equal(54, metric.SuuntoRestingHr);
        }

        [Fact]
        public async Task TestSync_TRIMP_Fallback_Calculation()
        {
            using var context = new AppDbContext(_options);
            DbInitializer.Initialize(context);

            var subKey = context.Configs.FirstOrDefault(c => c.Key == "SuuntoSubscriptionKey");
            if (subKey != null)
            {
                subKey.Value = "test-sub-key";
            }
            else
            {
                context.Configs.Add(new Config { Key = "SuuntoSubscriptionKey", Value = "test-sub-key" });
            }

            // Ensure DailyMetric exists first
            context.DailyMetrics.Add(new DailyMetric
            {
                Date = "2026-05-24",
                TdeeCalculated = 0,
                SuuntoActiveCals = 0,
                SuuntoHrv = 0,
                SuuntoSleepHours = 0,
                SuuntoRestingHr = 0,
                CronoCalsIn = 0,
                CronoProteinG = 0,
                CronoCarbsG = 0,
                CronoFatG = 0
            });

            // Workout at 15:00:00Z
            var workoutId = "unstitched-workout-2";
            var workout = new Workout
            {
                WorkoutId = workoutId,
                Date = "2026-05-24",
                TimestampStart = "2026-05-24T15:00:00Z",
                TimestampEnd = "2026-05-24T15:30:00Z",
                HevyWorkoutName = "Back Squats",
                SuuntoActivityId = null
            };
            context.Workouts.Add(workout);
            await context.SaveChangesAsync();

            // Activity without TSS
            var activity = new SuuntoActivity
            {
                Id = "suunto-act-88",
                StartTime = "2026-05-24T15:00:00Z",
                Duration = 1800, // 30 minutes
                AverageHr = 140,
                MaxHr = 180,
                Calories = 300,
                Tss = null // Triggers TRIMP fallback
            };
            var activitiesJson = JsonSerializer.Serialize(new List<SuuntoActivity> { activity });

            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    if (req.RequestUri?.ToString().Contains("workouts") == true)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(activitiesJson, Encoding.UTF8, "application/json")
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
                }
            };

            var client = new HttpClient(mockHandler);
            var oauthMock = new MockSuuntoOAuthService();
            var service = new SuuntoIngestionService(client, oauthMock);

            await service.SyncAsync(context);

            // Verify TRIMP formula: durationMins * (AvgHR / MaxHR) * 1.5
            // 30 * (140 / 180.0) * 1.5 = 35
            var updatedWorkout = await context.Workouts.FirstOrDefaultAsync(w => w.WorkoutId == workoutId);
            Assert.NotNull(updatedWorkout);
            Assert.Equal("suunto-act-88", updatedWorkout.SuuntoActivityId);
            Assert.Equal(35, updatedWorkout.StrainScore);
        }

        [Fact]
        public async Task TestSync_Outside_Time_Window_Not_Stitched()
        {
            using var context = new AppDbContext(_options);
            DbInitializer.Initialize(context);

            var subKey = context.Configs.FirstOrDefault(c => c.Key == "SuuntoSubscriptionKey");
            if (subKey != null)
            {
                subKey.Value = "test-sub-key";
            }
            else
            {
                context.Configs.Add(new Config { Key = "SuuntoSubscriptionKey", Value = "test-sub-key" });
            }

            // Ensure DailyMetric exists first
            context.DailyMetrics.Add(new DailyMetric
            {
                Date = "2026-05-24",
                TdeeCalculated = 0,
                SuuntoActiveCals = 0,
                SuuntoHrv = 0,
                SuuntoSleepHours = 0,
                SuuntoRestingHr = 0,
                CronoCalsIn = 0,
                CronoProteinG = 0,
                CronoCarbsG = 0,
                CronoFatG = 0
            });

            // Workout at 12:00:00Z
            var workoutId = "unstitched-workout-3";
            var workout = new Workout
            {
                WorkoutId = workoutId,
                Date = "2026-05-24",
                TimestampStart = "2026-05-24T12:00:00Z",
                TimestampEnd = "2026-05-24T13:00:00Z",
                HevyWorkoutName = "Arm Day",
                SuuntoActivityId = null
            };
            context.Workouts.Add(workout);
            await context.SaveChangesAsync();

            // Suunto activity at 12:20:00Z (20 minutes off -> outside ±15 min limit!)
            var activity = new SuuntoActivity
            {
                Id = "suunto-act-77",
                StartTime = "2026-05-24T12:20:00Z",
                Duration = 2400,
                AverageHr = 130,
                MaxHr = 160
            };
            var activitiesJson = JsonSerializer.Serialize(new List<SuuntoActivity> { activity });

            var mockHandler = new MockHttpMessageHandler
            {
                SendAsyncFunc = req =>
                {
                    if (req.RequestUri?.ToString().Contains("workouts") == true)
                    {
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(activitiesJson, Encoding.UTF8, "application/json")
                        });
                    }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
                }
            };

            var client = new HttpClient(mockHandler);
            var oauthMock = new MockSuuntoOAuthService();
            var service = new SuuntoIngestionService(client, oauthMock);

            var result = await service.SyncAsync(context);

            Assert.Equal(0, result.WorkoutsStitched);

            var checkWorkout = await context.Workouts.FirstOrDefaultAsync(w => w.WorkoutId == workoutId);
            Assert.NotNull(checkWorkout);
            Assert.Null(checkWorkout.SuuntoActivityId);
            Assert.Equal(0, checkWorkout.StrainScore);
        }
    }
}
