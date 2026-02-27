# 🧠 NeuroScholAR – Commit Guidelines

To keep the repository clean, readable and scalable across App (Unity), Backend (Go), Web (Next.js) and AI/ML (Python), we follow a structured commit convention.

---

# 📌 1. Commit Message Format

We use a simplified Conventional Commits format:

```
<type>(<scope>): <short description>
```

Example:

```
feat(app): add EEG signal quality validation
fix(backend): prevent race condition in progress sync
refactor(ai): simplify feature extraction pipeline
docs(project): update architecture diagram
```

---

# 📦 2. Commit Types

| Type        | When to use it |
|------------|----------------|
| `feat`     | New feature |
| `fix`      | Bug fix |
| `refactor` | Code change without changing behavior |
| `perf`     | Performance improvement |
| `docs`     | Documentation only |
| `test`     | Adding or modifying tests |
| `chore`    | Build/config/dependency changes |
| `ci`       | CI/CD related changes |
| `style`    | Formatting only (no logic change) |

---

# 🧩 3. Scope Naming (VERY IMPORTANT)

Scope must match one of the system components:

| Scope      | Description |
|------------|------------|
| `app`      | Unity / Mobile App |
| `backend`  | Go API |
| `web`      | Web Dashboard (Next.js) |
| `ai`       | ML model / Python service |
| `eeg`      | EEG processing logic |
| `infra`    | Deployment / Docker / DevOps |
| `db`       | Database changes |
| `project`  | General project files |
| `experiment` | Research / model experiments |

Example:

```
feat(ai): add flashcard knowledge verification model
feat(app): implement AR flashcard interaction
perf(eeg): optimize sliding window buffer
```

---

# 🧠 4. Commit Rules

### ✅ Keep commits:
- Small
- Focused
- Atomic (one logical change per commit)
- Reversible

### ❌ Avoid:
- "update stuff"
- "fix"
- "changes"
- Multiple unrelated changes in one commit

---

# 🚀 5. Branch Naming Convention

Format:

```
<feature>
```

Examples:

```
eeg-preprocessing
backend-session-race-fix
unity-main-thread-perf-upgrade
focus-model-v2
```

---

# 🔬 6. AI / Experiment Commits

For model experiments, ALWAYS include:

- Dataset version
- Model version
- Accuracy metrics
- Feature description

Example:

```
experiment(ai): test LSTM model on EEG v0.3 dataset

Dataset: eeg_sessions_v0.3
Features: alpha/beta ratio + blink frequency
Accuracy: 76.2%
```

This is critical for reproducibility.

---

# 🛑 7. DO NOT

- Commit large EEG raw datasets
- Commit trained model weights > 50MB
- Commit secrets (.env, API keys)
- Push directly to main (use PR)

---

# 🎯 Goal

Clean commit history =  
✔ Easier debugging  
✔ Easier paper writing  
✔ Easier experiment tracking  
✔ Easier scaling to contributors  
✔ Professional research-grade repository  

---

NeuroScholAR is a research-grade system.  
Treat the repository like a lab notebook.