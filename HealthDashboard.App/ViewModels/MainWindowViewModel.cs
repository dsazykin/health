using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using HealthDashboard.Core;
using HealthDashboard.Core.CsvIngestion;
using HealthDashboard.Core.Analytics;
using HealthDashboard.Core.Models;
using HealthDashboard.App.Views;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace HealthDashboard.App.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        // Core Properties
        private string _greeting = "Welcome to your Health Dashboard!";
        public string Greeting
        {
            get => _greeting;
            set => SetProperty(ref _greeting, value);
        }

        private string _statusText = "Ready";
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        // TDEE Telemetry
        private int _latestTdeeCalculated;
        public int LatestTdeeCalculated
        {
            get => _latestTdeeCalculated;
            set => SetProperty(ref _latestTdeeCalculated, value);
        }

        private int? _latestCalorieTarget;
        public int? LatestCalorieTarget
        {
            get => _latestCalorieTarget;
            set => SetProperty(ref _latestCalorieTarget, value);
        }

        private double _goalWeightKg;
        public double GoalWeightKg
        {
            get => _goalWeightKg;
            set => SetProperty(ref _goalWeightKg, value);
        }

        private double _targetRateOfChange;
        public double TargetRateOfChange
        {
            get => _targetRateOfChange;
            set => SetProperty(ref _targetRateOfChange, value);
        }

        // Readiness Matrix
        private double _latestReadinessScore;
        public double LatestReadinessScore
        {
            get => _latestReadinessScore;
            set => SetProperty(ref _latestReadinessScore, value);
        }

        private double _latestRecoveryScore;
        public double LatestRecoveryScore
        {
            get => _latestRecoveryScore;
            set => SetProperty(ref _latestRecoveryScore, value);
        }

        private double _latestDailyStrain;
        public double LatestDailyStrain
        {
            get => _latestDailyStrain;
            set => SetProperty(ref _latestDailyStrain, value);
        }

        private string _coachingAdvice = "Please import Hevy or Cronometer data to calibrate your readiness.";
        public string CoachingAdvice
        {
            get => _coachingAdvice;
            set => SetProperty(ref _coachingAdvice, value);
        }

        // Macro Targets
        private double _proteinTarget;
        public double ProteinTarget
        {
            get => _proteinTarget;
            set => SetProperty(ref _proteinTarget, value);
        }

        private double _carbsTarget;
        public double CarbsTarget
        {
            get => _carbsTarget;
            set => SetProperty(ref _carbsTarget, value);
        }

        private double _fatTarget;
        public double FatTarget
        {
            get => _fatTarget;
            set => SetProperty(ref _fatTarget, value);
        }

        // Selected Exercise for 1RM History
        private string _selectedExercise = string.Empty;
        public string SelectedExercise
        {
            get => _selectedExercise;
            set
            {
                if (SetProperty(ref _selectedExercise, value))
                {
                    _ = LoadExercise1RmHistoryAsync();
                }
            }
        }

        private ObservableCollection<string> _exercisesList = new ObservableCollection<string>();
        public ObservableCollection<string> ExercisesList
        {
            get => _exercisesList;
            set => SetProperty(ref _exercisesList, value);
        }

        // Suunto API Compliance Configuration
        private bool _isSuuntoConsentChecked;
        public bool IsSuuntoConsentChecked
        {
            get => _isSuuntoConsentChecked;
            set
            {
                if (SetProperty(ref _isSuuntoConsentChecked, value))
                {
                    _ = SaveSuuntoConsentAsync();
                }
            }
        }

        private string _suuntoClientId = string.Empty;
        public string SuuntoClientId
        {
            get => _suuntoClientId;
            set => SetProperty(ref _suuntoClientId, value);
        }

        private string _suuntoClientSecret = string.Empty;
        public string SuuntoClientSecret
        {
            get => _suuntoClientSecret;
            set => SetProperty(ref _suuntoClientSecret, value);
        }

        private bool _isSuuntoConnected;
        public bool IsSuuntoConnected
        {
            get => _isSuuntoConnected;
            set => SetProperty(ref _isSuuntoConnected, value);
        }

        // LiveCharts2 Series
        private ISeries[] _wellnessSeries = Array.Empty<ISeries>();
        public ISeries[] WellnessSeries
        {
            get => _wellnessSeries;
            set => SetProperty(ref _wellnessSeries, value);
        }

        private ISeries[] _hypertrophyVolumeSeries = Array.Empty<ISeries>();
        public ISeries[] HypertrophyVolumeSeries
        {
            get => _hypertrophyVolumeSeries;
            set => SetProperty(ref _hypertrophyVolumeSeries, value);
        }

        private ISeries[] _exercise1RmSeries = Array.Empty<ISeries>();
        public ISeries[] Exercise1RmSeries
        {
            get => _exercise1RmSeries;
            set => SetProperty(ref _exercise1RmSeries, value);
        }

        // X-Axis Labels
        private Axis[] _wellnessXAxes = Array.Empty<Axis>();
        public Axis[] WellnessXAxes
        {
            get => _wellnessXAxes;
            set => SetProperty(ref _wellnessXAxes, value);
        }

        private Axis[] _hypertrophyXAxes = Array.Empty<Axis>();
        public Axis[] HypertrophyXAxes
        {
            get => _hypertrophyXAxes;
            set => SetProperty(ref _hypertrophyXAxes, value);
        }

        private Axis[] _exercise1RmXAxes = Array.Empty<Axis>();
        public Axis[] Exercise1RmXAxes
        {
            get => _exercise1RmXAxes;
            set => SetProperty(ref _exercise1RmXAxes, value);
        }

        // Commands
        public ICommand ImportHevyCsvCommand { get; }
        public ICommand ImportCronoCsvCommand { get; }
        public ICommand DisconnectSuuntoCommand { get; }
        public ICommand SaveSuuntoConfigCommand { get; }
        public ICommand RecalculateAllCommand { get; }

        public MainWindowViewModel()
        {
            ImportHevyCsvCommand = new AsyncRelayCommand(ImportHevyCsvAsync);
            ImportCronoCsvCommand = new AsyncRelayCommand(ImportCronoCsvAsync);
            DisconnectSuuntoCommand = new AsyncRelayCommand(DisconnectSuuntoAsync);
            SaveSuuntoConfigCommand = new AsyncRelayCommand(SaveSuuntoConfigAsync);
            RecalculateAllCommand = new AsyncRelayCommand(RecalculateAllDataAsync);

            _ = InitializeDashboardAsync();
        }

        private async Task InitializeDashboardAsync()
        {
            await LoadSuuntoConfigAsync();
            await RecalculateAllDataAsync();
        }

        private async Task LoadSuuntoConfigAsync()
        {
            try
            {
                using (var db = new AppDbContext())
                {
                    var consent = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoUserConsent");
                    IsSuuntoConsentChecked = consent != null && consent.Value.Equals("true", StringComparison.OrdinalIgnoreCase);

                    var cid = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoClientId");
                    SuuntoClientId = cid?.Value ?? string.Empty;

                    var csec = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoClientSecret");
                    SuuntoClientSecret = csec?.Value ?? string.Empty;

                    var token = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoAccessToken");
                    IsSuuntoConnected = token != null && !string.IsNullOrEmpty(token.Value);
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading Suunto config: {ex.Message}";
            }
        }

        private async Task SaveSuuntoConsentAsync()
        {
            try
            {
                using (var db = new AppDbContext())
                {
                    var config = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoUserConsent");
                    if (config == null)
                    {
                        db.Configs.Add(new Config { Key = "SuuntoUserConsent", Value = IsSuuntoConsentChecked.ToString().ToLower() });
                    }
                    else
                    {
                        config.Value = IsSuuntoConsentChecked.ToString().ToLower();
                    }
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving Suunto consent: {ex.Message}";
            }
        }

        private async Task SaveSuuntoConfigAsync()
        {
            try
            {
                using (var db = new AppDbContext())
                {
                    var cid = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoClientId");
                    if (cid == null) db.Configs.Add(new Config { Key = "SuuntoClientId", Value = SuuntoClientId });
                    else cid.Value = SuuntoClientId;

                    var csec = await db.Configs.FirstOrDefaultAsync(c => c.Key == "SuuntoClientSecret");
                    if (csec == null) db.Configs.Add(new Config { Key = "SuuntoClientSecret", Value = SuuntoClientSecret });
                    else csec.Value = SuuntoClientSecret;

                    await db.SaveChangesAsync();
                    StatusText = "Suunto configuration saved successfully.";
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error saving Suunto credentials: {ex.Message}";
            }
        }

        private async Task DisconnectSuuntoAsync()
        {
            try
            {
                using (var db = new AppDbContext())
                {
                    // Purge all tokens and configuration details
                    var keysToPurge = new[] { "SuuntoAccessToken", "SuuntoRefreshToken", "SuuntoTokenExpiry", "SuuntoClientId", "SuuntoClientSecret", "SuuntoUserConsent" };
                    var configs = await db.Configs.Where(c => keysToPurge.Contains(c.Key)).ToListAsync();
                    db.Configs.RemoveRange(configs);

                    // Wipe locally synced Suunto metrics from DailyMetrics
                    var metrics = await db.DailyMetrics.ToListAsync();
                    foreach (var m in metrics)
                    {
                        m.SuuntoActiveCals = 0;
                        m.SuuntoHrv = 0;
                        m.SuuntoSleepHours = 0;
                        m.SuuntoRestingHr = 0;
                        m.RecoveryScore = null;
                        m.ReadinessScore = null;
                        m.DailyStrain = null;
                    }

                    // Wipe activity stitching
                    var workouts = await db.Workouts.ToListAsync();
                    foreach (var w in workouts)
                    {
                        w.SuuntoActivityId = null;
                        w.AvgHeartRate = 0;
                        w.MaxHeartRate = 0;
                        w.WorkoutStrainScore = null;
                    }

                    await db.SaveChangesAsync();
                }

                IsSuuntoConnected = false;
                IsSuuntoConsentChecked = false;
                SuuntoClientId = string.Empty;
                SuuntoClientSecret = string.Empty;

                await RecalculateAllDataAsync();
                StatusText = "Suunto integration successfully disconnected. All localized metrics cleared.";
            }
            catch (Exception ex)
            {
                StatusText = $"Error during Suunto disconnect: {ex.Message}";
            }
        }

        private async Task RecalculateAllDataAsync()
        {
            StatusText = "Recalculating analytics engines...";
            try
            {
                using (var db = new AppDbContext())
                {
                    // Update TDEE history
                    await new TdeeEngine().UpdateTdeeHistoryAsync(db);
                    // Update Readiness history
                    await new ReadinessEngine().UpdateReadinessHistoryAsync(db);

                    // 1. Fetch latest physical & athletic readiness values
                    var latestReadyMetric = await db.DailyMetrics
                        .Where(m => m.ReadinessScore.HasValue && m.ReadinessScore.Value > 0)
                        .OrderByDescending(m => m.Date)
                        .FirstOrDefaultAsync();

                    if (latestReadyMetric != null)
                    {
                        LatestReadinessScore = Math.Round(latestReadyMetric.ReadinessScore.Value);
                        LatestRecoveryScore = Math.Round(latestReadyMetric.RecoveryScore.Value);
                        LatestDailyStrain = Math.Round(latestReadyMetric.DailyStrain.Value);
                        CoachingAdvice = ReadinessEngine.GetActionableAdvice(latestReadyMetric.ReadinessScore.Value);
                    }
                    else
                    {
                        LatestReadinessScore = 0;
                        LatestRecoveryScore = 0;
                        LatestDailyStrain = 0;
                        CoachingAdvice = "Import Suunto & Hevy metrics to generate daily readiness telemetry.";
                    }

                    // 2. Fetch latest TDEE details
                    var latestTdeeMetric = await db.DailyMetrics
                        .Where(m => m.TdeeCalculated > 0)
                        .OrderByDescending(m => m.Date)
                        .FirstOrDefaultAsync();

                    if (latestTdeeMetric != null)
                    {
                        LatestTdeeCalculated = latestTdeeMetric.TdeeCalculated;
                        LatestCalorieTarget = latestTdeeMetric.CalorieTarget;
                    }
                    else
                    {
                        var defaultTdeeConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "DefaultTdee");
                        LatestTdeeCalculated = defaultTdeeConfig != null && int.TryParse(defaultTdeeConfig.Value, out int parsedTdee) ? parsedTdee : 2000;
                        LatestCalorieTarget = null;
                    }

                    // 3. Load Configs for GoalWeightKg and TargetRateOfChange
                    var goalWeightConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "GoalWeightKg");
                    GoalWeightKg = goalWeightConfig != null && double.TryParse(goalWeightConfig.Value, System.Globalization.CultureInfo.InvariantCulture, out double gw) ? gw : 0.0;

                    var targetRateConfig = await db.Configs.FirstOrDefaultAsync(c => c.Key == "TargetRateOfChange");
                    TargetRateOfChange = targetRateConfig != null && double.TryParse(targetRateConfig.Value, System.Globalization.CultureInfo.InvariantCulture, out double trc) ? trc : 0.0;

                    // 4. Calculate macro distributions
                    var latestWeightMetric = await db.DailyMetrics
                        .Where(m => m.WeightKg.HasValue && m.WeightKg.Value > 0)
                        .OrderByDescending(m => m.Date)
                        .FirstOrDefaultAsync();
                    double latestWeight = latestWeightMetric?.WeightKg ?? 75.0;

                    if (LatestCalorieTarget.HasValue)
                    {
                        ProteinTarget = Math.Round(latestWeight * 2.0); // 2.0g per kg of body weight
                        FatTarget = Math.Round((LatestCalorieTarget.Value * 0.25) / 9.0); // 25% of calories
                        CarbsTarget = Math.Round((LatestCalorieTarget.Value - (ProteinTarget * 4.0 + FatTarget * 9.0)) / 4.0); // Remaining calories
                    }
                    else
                    {
                        ProteinTarget = 0;
                        FatTarget = 0;
                        CarbsTarget = 0;
                    }

                    // 5. Build Exercise selection lists
                    var exercises = await db.Exercises.OrderBy(e => e.ExerciseName).Select(e => e.ExerciseName).ToListAsync();
                    ExercisesList.Clear();
                    foreach (var ex in exercises)
                    {
                        ExercisesList.Add(ex);
                    }

                    if (ExercisesList.Count > 0 && string.IsNullOrEmpty(SelectedExercise))
                    {
                        SelectedExercise = ExercisesList[0];
                    }

                    // 6. Build Wellness Trends (HRV vs Sleep)
                    var wellnessData = await db.DailyMetrics
                        .OrderByDescending(m => m.Date)
                        .Take(30)
                        .OrderBy(m => m.Date)
                        .ToListAsync();

                    if (wellnessData.Count > 0)
                    {
                        var sleepValues = wellnessData.Select(m => m.SuuntoSleepHours).ToList();
                        var hrvValues = wellnessData.Select(m => m.SuuntoHrv).ToList();
                        var dates = wellnessData.Select(m => m.Date.Substring(5)).ToArray(); // MM-DD format

                        WellnessSeries = new ISeries[]
                        {
                            new ColumnSeries<double>
                            {
                                Name = "Sleep (Hours)",
                                Values = sleepValues,
                                Stroke = new SolidColorPaint(SKColors.DodgerBlue) { StrokeThickness = 2 },
                                Fill = new SolidColorPaint(SKColors.DodgerBlue.WithAlpha(50))
                            },
                            new LineSeries<double>
                            {
                                Name = "HRV (ms)",
                                Values = hrvValues,
                                Stroke = new SolidColorPaint(SKColor.Parse("#A6E3A1")) { StrokeThickness = 3 },
                                Fill = null
                            }
                        };
                        WellnessXAxes = new Axis[] { new Axis { Labels = dates } };
                    }

                    // 7. Build Weekly Hypertrophy volume horizontal bars
                    var tracker = new VolumeTracker();
                    var vols = await tracker.GetWeeklyHypertrophyVolumeAsync(db);

                    if (vols.Count > 0)
                    {
                        var groupedByMuscle = vols.GroupBy(v => v.MuscleGroup).ToList();
                        var muscleLabels = groupedByMuscle.Select(g => g.Key).ToArray();
                        var hardSetCounts = groupedByMuscle.Select(g => (double)g.Max(x => x.HardSetsCount)).ToList();

                        HypertrophyVolumeSeries = new ISeries[]
                        {
                            new RowSeries<double>
                            {
                                Name = "Weekly Hard Sets",
                                Values = hardSetCounts,
                                Stroke = new SolidColorPaint(SKColor.Parse("#F5C2E7")) { StrokeThickness = 2 },
                                Fill = new SolidColorPaint(SKColor.Parse("#F5C2E7").WithAlpha(80))
                            }
                        };
                        HypertrophyXAxes = new Axis[] { new Axis { Labels = muscleLabels } };
                    }

                    // 8. Populate Selected Exercise 1RM History
                    await LoadExercise1RmHistoryAsync();
                }

                StatusText = "Dashboard successfully re-calibrated!";
            }
            catch (Exception ex)
            {
                StatusText = $"Calculation finished with errors: {ex.Message}";
            }
        }

        private async Task LoadExercise1RmHistoryAsync()
        {
            if (string.IsNullOrEmpty(SelectedExercise)) return;

            try
            {
                using (var db = new AppDbContext())
                {
                    var tracker = new VolumeTracker();
                    var points = await tracker.GetExercise1RmHistoryAsync(db, SelectedExercise);

                    if (points.Count > 0)
                    {
                        // Apply LTTB downsampling to preserve responsive layout performance if history grows large
                        var dataToSample = points.Select((p, idx) => new LttbDownsampler.DataPoint
                        {
                            X = idx,
                            Y = p.OneRepMax
                        }).ToList();

                        var downsampled = LttbDownsampler.Downsample(dataToSample, 50);

                        var oneRepMaxValues = downsampled.Select(d => d.Y).ToList();
                        var dates = downsampled.Select(d => points[(int)d.X].Date.Substring(5)).ToArray();

                        Exercise1RmSeries = new ISeries[]
                        {
                            new LineSeries<double>
                            {
                                Name = "Estimated 1RM (kg)",
                                Values = oneRepMaxValues,
                                Stroke = new SolidColorPaint(SKColor.Parse("#F9E2AF")) { StrokeThickness = 3 },
                                Fill = new SolidColorPaint(SKColor.Parse("#F9E2AF").WithAlpha(30))
                            }
                        };
                        Exercise1RmXAxes = new Axis[] { new Axis { Labels = dates } };
                    }
                    else
                    {
                        Exercise1RmSeries = Array.Empty<ISeries>();
                        Exercise1RmXAxes = Array.Empty<Axis>();
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error loading 1RM history: {ex.Message}";
            }
        }

        private async Task ImportHevyCsvAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                StatusText = "Error: Desktop application lifetime not found.";
                return;
            }

            var parentWindow = desktop.MainWindow;
            if (parentWindow == null)
            {
                StatusText = "Error: Main window not found.";
                return;
            }

            StatusText = "Selecting CSV file...";

            try
            {
                var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Hevy Workouts CSV File",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("CSV Files")
                        {
                            Patterns = new[] { "*.csv" }
                        }
                    }
                });

                if (files == null || files.Count == 0)
                {
                    StatusText = "Ready";
                    return;
                }

                var csvPath = files[0].Path.LocalPath;
                if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                {
                    StatusText = "Error: Selected file does not exist.";
                    return;
                }

                StatusText = "Importing Hevy CSV...";

                ImportResult result;
                using (var db = new AppDbContext())
                {
                    result = await HevyImporter.ImportAsync(db, csvPath);
                }

                if (result.NewExercises != null && result.NewExercises.Count > 0)
                {
                    StatusText = $"{result.NewExercises.Count} new exercises detected! Waiting for classification...";
                    
                    var dialog = new NewExercisePromptWindow(result.NewExercises);
                    var promptResult = await dialog.ShowDialog<bool>(parentWindow);

                    if (promptResult)
                    {
                        StatusText = $"Success: Imported {result.Imported} workouts, resolved {result.NewExercises.Count} new exercises!";
                    }
                    else
                    {
                        StatusText = $"Imported {result.Imported} workouts. Warning: new exercises were not fully categorized.";
                    }
                }
                else
                {
                    StatusText = $"Success: Imported {result.Imported} workouts! (Skipped: {result.Skipped})";
                }

                if (result.HasErrors)
                {
                    var errorSummary = string.Join("; ", result.Errors.Take(3));
                    StatusText += $" (Errors: {result.Errors.Count} - {errorSummary})";
                }

                await RecalculateAllDataAsync();
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }

        private async Task ImportCronoCsvAsync()
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                StatusText = "Error: Desktop application lifetime not found.";
                return;
            }

            var parentWindow = desktop.MainWindow;
            if (parentWindow == null)
            {
                StatusText = "Error: Main window not found.";
                return;
            }

            StatusText = "Selecting Cronometer CSV file...";

            try
            {
                var files = await parentWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Cronometer daily_summary.csv File",
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("CSV Files")
                        {
                            Patterns = new[] { "*.csv" }
                        }
                    }
                });

                if (files == null || files.Count == 0)
                {
                    StatusText = "Ready";
                    return;
                }

                var csvPath = files[0].Path.LocalPath;
                if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                {
                    StatusText = "Error: Selected file does not exist.";
                    return;
                }

                StatusText = "Importing Cronometer CSV...";

                ImportResult result;
                using (var db = new AppDbContext())
                {
                    result = await CronoImporter.ImportAsync(db, csvPath);
                }

                StatusText = $"Success: Imported {result.Imported} daily summaries! (Skipped: {result.Skipped})";

                if (result.HasErrors)
                {
                    var errorSummary = string.Join("; ", result.Errors.Take(3));
                    StatusText += $" (Errors: {result.Errors.Count} - {errorSummary})";
                }

                await RecalculateAllDataAsync();
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }
        }
    }
}
