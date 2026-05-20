# Health & Fitness Native Desktop Dashboard - Development Roadmap (Avalonia UI Edition)

## Project Architecture Overview
* **Paradigm:** Native Desktop Application (100% offline-first, local storage).
* **App Framework:** Avalonia UI (Desktop-first cross-platform framework rendering via the Skia engine).
* **Backend Engine:** C# (.NET 8+).
* **Data Processing:** C# LINQ & CsvHelper (for processing flat data exports).
* **Database:** SQLite (Embedded `.db` file managed locally via Entity Framework Core or Dapper).
* **Frontend:** Avalon XAML (WPF-styled Extensible Application Markup Language) with styling selectors.
* **Data Ingestion:** Avalonia `StorageProvider` API for native file picking (Hevy and Cronometer CSVs). Suunto API via custom OAuth2 local protocol handler or embedded loop.

---

## Phase 1: Database Schema & Core Relational Design
*Establishing the local SQLite database structure to cleanly stitch together data from three disparate sources.*

### 1.1 Initialize SQLite Database
The database lives in the OS user's local application data directory (`Environment.SpecialFolder.LocalApplicationData`).

* **Table: `DailyMetrics`**
    * `Date` (Primary Key, TEXT - `YYYY-MM-DD` ISO 8601 UTC)
    * `WeightKg` (REAL)
    * `BodyFatPercent` (REAL, Nullable)
    * `TdeeCalculated` (INTEGER) - Computed via Exponential Moving Average (EMA).
    * `SuuntoActiveCals` (INTEGER)
    * `SuuntoHrv` (REAL)
    * `SuuntoSleepHours` (REAL)
    * `CronoCalsIn` (INTEGER)
    * `CronoProteinG`, `CronoCarbsG`, `CronoFatG` (INTEGER)
* **Table: `Workouts`** * `WorkoutId` (Primary Key, TEXT/UUID)
    * `Date` (TEXT, Foreign Key -> `DailyMetrics`)
    * `TimestampStart`, `TimestampEnd` (TEXT - ISO 8601 UTC)
    * `HevyWorkoutName` (TEXT)
    * `SuuntoActivityId` (TEXT, Nullable)
    * `AvgHeartRate`, `MaxHeartRate` (INTEGER)
* **Table: `WorkoutSets`** * `SetId` (Primary Key, INTEGER Auto-increment)
    * `WorkoutId` (TEXT, Foreign Key -> `Workouts`)
    * `ExerciseName` (TEXT)
    * `MuscleGroup` / `MovementPattern` (TEXT)
    * `SplitCategory` (TEXT) - e.g., Push, Pull, Legs.
    * `SetOrder` (INTEGER)
    * `WeightKg` (REAL)
    * `Reps` (INTEGER)
    * `Rpe` (REAL)
* **Table: `Config`**
    * `SuuntoAccessToken`, `SuuntoRefreshToken` (TEXT - Encrypted via Platform Secure Store)
    * `TokenExpiresAt` (INTEGER)

---

## Phase 2: C# Data Ingestion Engine

### 2.1 The Cronometer CSV Module
* **Logic:** Read `daily_summary.csv` using `CsvHelper` (configured with Source Generators to prevent AOT trimming). Filter for dates, total calories, and macronutrient targets using LINQ.
* **DB Action:** Execute an `UPSERT` statement using Dapper/EF Core to ensure existing days are overwritten.

### 2.2 The Hevy CSV Module
* **Logic:** 1. Parse the flat `hevy_workout_data.csv` using `CsvHelper`.
    2. Group rows by `TimestampStart` (UTC) to ensure uniqueness.
    3. Generate a `Guid` for each block and populate `Workouts`.
    4. Write individual rows into `WorkoutSets`, automatically tagging the `SplitCategory`.

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

---

## Phase 4: Analytics & Logic Tier

### 4.1 Dynamic TDEE Engine
* **Smoothing:** Apply an Exponential Moving Average (EMA) to daily `WeightKg` inputs to filter out short-term water fluctuations.
* **Formula:** `True TDEE = (Total Intake - (Smoothed Weight Delta * 7700)) / 14`
* **Implementation Note:** Calculation dynamically filters for days containing both valid calorie intake and a smoothed weight point to ensure accuracy despite missing logs.

### 4.2 Progressive Overload & Split Tracker
* Computes Estimated One-Rep Max (1RM) using the Epley formula.
* Generates total tonnage volume trends.
* **Split Analysis:** Automatically groups volume metrics by training split (Push, Pull, Legs) to visualize weekly recovery and workload distribution.

---

## Phase 5: Avalonia XAML Frontend UI

### 5.1 Native Window Layout
* Custom borderless window execution with a dark mode design palette. Uses Avalonia's highly precise control templates to guarantee identical rendering across Windows, macOS, and Linux.

### 5.2 Component Views
1. **Dashboard Hub:** A responsive grid showing Calculated Target Calories, sleep trends, and a training consistency calendar.
2. **The Ingestion Station:** Core action center invoking the Avalonia `TopLevel.StorageProvider.OpenFilePickerAsync` framework (async operation on background thread).
3. **Telemetry Center:** Render continuous heart rate zones mapped against specific workout blocks using `LiveCharts2`. Data is downsampled (e.g., LTTB algorithm) before rendering to maintain UI thread responsiveness.

---

## Phase 6: Compilation & Distribution

### 6.1 Native Desktop Packaging
* **Strategy:** Publish as self-contained binaries. 
* **Optimizations:** Utilize AOT and Trimming cautiously; ensure Dapper/EF Core metadata is preserved and `CsvHelper` source generators are used to avoid runtime failures.

---

## Phase 7: Data Maintenance & Portability

### 7.1 Automated SQLite Backups
* Implement an asynchronous `BackgroundService` to duplicate the `.db` file into a compressed local `backup.zip` weekly, keeping data completely secure and portable.
