# Suunto Cloud API Agreement - Legal-Technical Compliance Review

This document outlines the key legal-technical obligations, constraints, and actionable development tasks identified during the review of the **Suunto Cloud API Agreement for Commercial Use** (found in [Suunto Terms.txt](file:///C:/Users/yikes/Code/health/Suunto%20Terms.txt)). These requirements have been mapped directly to our offline-first Health & Fitness Desktop Dashboard architecture.

---

## 🚨 Critical Developer Mandates & UI Obligations

The following obligations are legally required by Suunto under **Clause 5 (Licensee's General Rights and Obligations)** and must be implemented as part of our dashboard’s user interface:

### 1. Suunto Compatibility Branding (Clause 5(h) & 5(i))
*   **The Obligation:** The application must explicitly state that it is compatible with Suunto products, display a Suunto compatible logo on the integration views, and provide a clickable hyperlink to `Suunto.com`.
*   **Action Item:** In the dashboard's connection screen or Suunto settings card, we must display:
    *   The text label: `"Compatible with Suunto"` (or official compatible logo).
    *   A clickable, active hyperlink pointing to `https://www.suunto.com`.

### 2. Express User Consent (Clause 5(k))
*   **The Obligation:** Users must always provide express consent before the application initiates any automated data-fetching or sending operations on their behalf.
*   **Action Item:** Prior to launching the browser-based OAuth2 handshake or starting background synchronization, the UI must present an interactive prompt or checkbox stating:
    > *"By enabling this integration, you expressly consent to this application securely connecting to your Suunto account to fetch your workouts, cardiovascular metrics, sleep telemetry, and heart rate variability (HRV) logs to store them locally."*

### 3. Immediate De-authorization and Data Erasure (Clause 5(d) & 5(j))
*   **The Obligation:** If a user chooses to disconnect from Suunto, the application must immediately sever the connection and reflect this state. Additionally, we must timely remove user content upon request.
*   **Action Item:** The application settings must include a **"Disconnect Suunto"** action that:
    1.  Immediately purges and deletes `SuuntoAccessToken`, `SuuntoRefreshToken`, and `SuuntoTokenExpiresAt` from the local database configurations.
    2.  Presents the user with a prompt: *"Would you also like to delete all synced Suunto telemetry from your local database?"* If confirmed, cascade deletes all synced heart rate, sleep, active calorie, and recovery parameters associated with Suunto.

### 4. Direct Password Ban (Clause 5(g))
*   **The Obligation:** The application must never request or store Suunto App user passwords.
*   **Action Item:** Handled completely. Our desktop architecture leverages standard OAuth2 Authorization Code flow via a local `HttpListener` callback redirect, ensuring the user only enters credentials on Suunto's official domain.

---

## 🔒 Architectural Compliance Analysis

Our current desktop design is fully aligned with Suunto’s strict data privacy and distribution restrictions under **Clause 7 (Restrictions and Limitations)**:

| Suunto Agreement Restriction | Clause | Technical Compliance Strategy |
| :--- | :--- | :--- |
| **No Password Harvesting** | **Clause 5(g)** | Full compliance. We utilize OAuth2 loopback browser redirection, capturing only the short-lived authorization code. |
| **Protected API Keys** | **Clause 7.1(h)** | Full compliance. Client IDs, Secrets, Access, and Refresh tokens are encrypted locally using **Windows DPAPI** (`CurrentUser` scope) and never stored in plain text. |
| **No Out-of-App Data Sharing** | **Clause 7.1(f)** | Full compliance. The database is 100% local (`health_dashboard.db` stored in the user's special local AppData folder). No biometrics ever leave the host machine. |
| **No Ad Networks or Monetization** | **Clause 7.1(j)** | Full compliance. There are absolutely no ad SDKs, analytics trackers, or commercial telemetry engines in our codebase. Data is strictly personal. |
| **No Web Scraping** | **Clause 7.1(g)** | Full compliance. Ingestion relies exclusively on official Suunto REST endpoints. |

---

## ⚖️ Administrative Details

*   **Governing Law (Clause 17.1):** The agreement is governed by the laws of **Finland**.
*   **Disputes (Clause 17.2):** Resolved by arbitration under the rules of the Central Chamber of Commerce of Finland. The arbitration takes place in **Helsinki, Finland**, and is conducted in English (for non-Finnish companies).
*   **Right of Revocation (Clause 14.5):** Suunto reserves the right to suspend the Licensed Technology, disable our client ID, or terminate the agreement at any time with or without cause.
