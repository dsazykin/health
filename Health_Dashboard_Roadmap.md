# Health & Fitness Native Desktop Dashboard - Development Roadmap (Avalonia UI Edition)

## Project Architecture Overview
* **Paradigm:** Native Desktop Application (100% offline-first, local storage).
* **App Framework:** Avalonia UI (Desktop-first cross-platform framework rendering via the Skia engine).
* **Backend Engine:** C# (.NET 8+).
* **Data Processing:** C# LINQ & CsvHelper (for processing flat data exports).
* **Database:** SQLite (Embedded `.db` file managed locally via Entity Framework Core or Dapper).
* **Frontend:** Avalon XAML (WPF-styled Extensible Application Markup Language) with styling selectors. Glassmorphic acrylic styling with Skia-based graphics.
* **Data Ingestion:** Avalonia `StorageProvider` API for native file picking (Hevy and Cronometer CSVs). Suunto API via custom OAuth2 local protocol handler or embedded loop.

---

## Phase 1: Database Schema & Core Relational Design
*Establishing the normalized, local SQLite database structure to cleanly stitch together data from three disparate sources without data-loss during ingestion.*

### 1.1 Initialize SQLite Database
The database lives in the OS user's local application data directory (`Environment.SpecialFolder.LocalApplicationData`).

* **Table: `Exercises`** (Ensures clean database normalization)
    * `ExerciseName` (Primary Key, TEXT)
    * `TargetMuscleGroup` (TEXT)
    * `MovementPattern` (TEXT)

* **Table: `DailyMetrics`**
    * `Date` (Primary Key, TEXT - `YYYY-MM-DD` ISO 8601 UTC)
    * `WeightKg` (REAL)
    * `BodyFatPercent` (REAL, Nullable)
    * `TdeeCalculated` (INTEGER) - Computed via adaptive Exponential Moving Average (EMA).
    * `CalorieTarget` (INTEGER, Nullable) - Automatically calculated based on goal rate and TDEE.
    * `SuuntoActiveCals` (INTEGER)
    * `SuuntoHrv` (REAL)
    * `SuuntoSleepHours` (REAL)
    * `SuuntoRestingHr` (INTEGER)
    * `CronoCalsIn` (INTEGER)
    * `CronoProteinG`, `CronoCarbsG`, `CronoFatG` (INTEGER)

* **Table: `Workouts`**
    * `WorkoutId` (Primary Key, TEXT) - Deterministic hash generated to ensure idempotency.
    * `Date` (TEXT, Foreign Key -> `DailyMetrics`)
    * `TimestampStart`, `TimestampEnd` (TEXT - ISO 8601 UTC)
    * `HevyWorkoutName` (TEXT)
    * `SuuntoActivityId` (TEXT, Nullable)
    * `AvgHeartRate`, `MaxHeartRate` (INTEGER)
    * `StrainScore` (INTEGER) - Calculated from workout duration, active heart rate, and training volume.

* **Table: `WorkoutSets`**
    * `SetId` (Primary Key, INTEGER Auto-increment)
    * `WorkoutId` (TEXT, Foreign Key -> `Workouts`)
    * `ExerciseName` (TEXT, Foreign Key -> `Exercises`)
    * `SetOrder` (INTEGER)
    * `WeightKg` (REAL)
    * `Reps` (INTEGER)
    * `Rpe` (REAL) - Rate of Perceived Exertion.
    * `IsHardSet` (INTEGER - 0 or 1) - Set automatically if Reps are close to failure or RPE >= 7.

* **Table: `Config`**
    * `SuuntoAccessToken`, `SuuntoRefreshToken` (TEXT - Encrypted via Platform Secure Store)
    * `TokenExpiresAt` (INTEGER)
    * `GoalWeightKg` (REAL)
    * `TargetRateOfChange` (REAL) - kg per week (positive for bulk, negative for cut).

---

## Phase 2: C# Robust Data Ingestion Engine

### 2.1 The Cronometer CSV Module
* **Logic:** Read `daily_summary.csv` using `CsvHelper` (configured with Source Generators to prevent AOT trimming). Filter for dates, total calories, and macronutrient targets using LINQ.
* **DB Safe Upsert Action:** To prevent overwriting weights or Suunto telemetry synced previously, execute an explicit column-level upsert:
  ```sql
  INSERT INTO DailyMetrics (Date, CronoCalsIn, CronoProteinG, CronoCarbsG, CronoFatG)
  VALUES (@Date, @Cals, @Protein, @Carbs, @Fat)
  ON CONFLICT(Date) DO UPDATE SET
      CronoCalsIn = excluded.CronoCalsIn,
      CronoProteinG = excluded.CronoProteinG,
      CronoCarbsG = excluded.CronoCarbsG,
      CronoFatG = excluded.CronoFatG;
  ```

### 2.2 The Hevy CSV Module
* **Logic:** 
    1. Parse the flat `hevy_workout_data.csv` using `CsvHelper`.
    2. Dynamically populate the `Exercises` reference table for any newly encountered exercise names.
    3. Generate a **deterministic unique ID** based on immutable attributes (e.g., hashing `TimestampStart` + `HevyWorkoutName`) to guarantee idempotency and avoid duplicates on subsequent re-imports.
    4. Group sets to form `Workouts`, calculating `WorkoutId`.
    5. Write individual rows into `WorkoutSets`, resolving foreign keys dynamically.

---

## Phase 3: Native Suunto API Pipeline

### 3.1 Native OAuth2 Handshake & Lifecycle
* Trigger the native default browser using `Process.Start` with the Suunto authorization URL.
* Listen on a lightweight, temporary local HTTP listener (`http://127.0.0.1:port/`) to capture the callback parameter.
* Tokens are stored securely using OS-native APIs (DPAPI on Windows, Keychain on macOS).
* **Refresh Logic:** The engine checks `TokenExpiresAt` before any sync and silently executes a refresh token grant if needed.

### 3.2 The Stitching Algorithm
* Query `Workouts` entries where `SuuntoActivityId` is NULL.
* Match `TimestampStart` (UTC) against new Suunto activities within a ±15-minute window. Link them and pull in telemetry.
* Store the combined telemetry in `Workouts` and calculate a recovery/strain profile.

---

## Phase 4: Analytics & Logic Tier

### 4.1 Robust Adaptive TDEE Engine
* **Smoothing:** Apply an Exponential Moving Average (EMA) to daily `WeightKg` inputs to filter out short-term water fluctuations.
* **Intake Completion Threshold:** Only calculate/update TDEE if at least 11 of the last 14 days have logged calories ($>500 \text{ kcal}$).
* **Dynamic Scale Formula:** Scale actual calorie average dynamically to avoid missing log penalties:
  $$\text{Average Daily Intake} = \frac{\sum \text{Logged Calories}}{\text{Number of Logged Days}}$$
  $$\text{True TDEE} = \text{Average Daily Intake} - \left( \frac{\text{Smoothed Weight Delta} \times 7700}{\text{Days Elapsed}} \right)$$

### 4.2 Dynamic Caloric Recommendations (Feedback Loop)
* Calculate `Target Calories` dynamically:
  $$\text{Target Calories} = \text{Calculated TDEE} + \left( \frac{\text{Target Rate of Change} \times 7700}{7} \right)$$
* Apply weekly check-ins to shift the target calorie target gradually, preventing wild swings.

### 4.3 Recovery, Strain, & Readiness Matrix
* **Strain:** Calculate daily training stress using Hevy volume, relative intensity, and Suunto workout heart rate zones.
* **Recovery:** Utilize Suunto sleep duration and sleep HRV relative to a 60-day running HRV average.
* **Daily Readiness Score:** Construct a 0-100% score to provide actionable daily advice (e.g., green-light heavy volume or recommend recovery days).

### 4.4 Volume & Hard Sets Tracker
* Computes Estimated One-Rep Max (1RM) using the Epley formula, capped at $\leq 10$ reps for accuracy.
* **Hypertrophy Volume:** Track **Hard Sets** (defined as sets where $\text{RPE} \geq 7$ or within 3 reps of failure) per muscle group per week for superior volume analysis.

---

## Phase 5: Avalonia XAML Frontend UI

### 5.1 Native Window Layout
* Borderless mica/acrylic glassmorphism layout with smooth glowing gradients indicating zones (Strain vs. Recovery).
* Avalonia highly precise control templates to guarantee identical rendering across Windows, macOS, and Linux.

### 5.2 Component Views
1. **Dashboard Hub:** A responsive grid highlighting the Daily Readiness Score, Calculated TDEE, Dynamic target macro budget, and a training consistency calendar.
2. **The Ingestion Station:** Core action center invoking the Avalonia `TopLevel.StorageProvider.OpenFilePickerAsync` framework, showing real-time, smooth progress transitions.
3. **Telemetry Center:** Render continuous heart rate zones mapped against specific workout blocks using `LiveCharts2`. Data is downsampled via the LTTB (Largest-Triangle-Three-Buckets) algorithm to preserve UI responsiveness.

---

## Phase 6: Compilation & Distribution

### 6.1 Native Desktop Packaging
* **Strategy:** Publish as self-contained binaries. 
* **Optimizations:** Utilize AOT and Trimming cautiously; ensure Dapper/EF Core metadata is preserved and `CsvHelper` source generators are used to avoid runtime failures.

---

## Phase 7: Data Maintenance & Portability

### 7.1 Automated SQLite Backups & Portability
* Implement an asynchronous `BackgroundService` to duplicate the `.db` file into a compressed local `backup.zip` weekly.
* Provide an "Export Unified Data" action within the UI allowing the user to export unified records to CSV or JSON format for absolute data ownership.
