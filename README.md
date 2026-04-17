# NeuroScholAR

Combining ancient memory techniques with modern AR and neurofeedback technologies.

## 🎯 Project Goal

NeuroScholAR is a learning system using AR glasses and EEG headbands. The solution displays virtual notes in space and anchors them to specific locations (memory palace mnemonic technique) while automatically detecting focus drops and adapting learning sessions.

## 🏗 Architecture

The project consists of:

- **Unity App** - AR visualization, EEG processing, local ML model
- **Go Backend** - REST API, user management, data synchronization
- **Python ML Service** - Cognitive state classification, knowledge verification
- **Web Dashboard** - Progress tracking, statistics, palace management

## 📚 Documentation

- [Architecture](./docs/architecture.md) - System design and data flow
- [API Specification](./docs/api.md) - Backend endpoints
- [Risk Analysis](./docs/risk-analysis.md) - Project risks and mitigation
- [Contributing](./docs/CONTRIBUTING.md) - Contributing guidelines

## 🚀 Getting Started

### Backend (local)

1. Start PostgreSQL:
   - `cd backend`
   - `docker compose up -d db`
2. Create local backend env file:
   - `copy .env.example .env`
   - Set at least:
     - `JWT_SECRET` to a long random string
     - `DB_HOST=localhost`
     - `DB_PORT=5432`
     - `DB_USER=postgres`
     - `DB_PASSWORD=postgres`
     - `DB_NAME=neuroscholar`
     - `DB_SSLMODE=disable`
3. Run database migrations explicitly:
   - `docker compose run --rm migrate`
4. Start API server:
   - `go run .`

## 📋 Prerequisites

- AR glasses (XREAL air2 ultra)
- EEG headband (Muse / Emotiv)
- XREAL Beam Pro device for development

## 🛠 Developer Guide - Logging

The app includes a comprehensive logging system for XREAL debugging.

- **Log Location (PC):** `%AppData%\LocalLow\KN Neuron\NeuroScholAR\Logs`
- **Log Location (Android):** `/Android/data/[package_name]/files/Logs`
- **Format:** `[timestamp][level][category] message`
- **Export:** Use the "Export Logs" button in the Main Menu to share logs via native Android share sheet.
