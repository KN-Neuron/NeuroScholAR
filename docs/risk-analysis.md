# 🚧 Potential Bottlenecks in NeuroScholAR

## 1️⃣ EEG Data Acquisition & Signal Processing (Unity – On Device)

### 🔹 High-frequency EEG stream
- EEG sampling rate (e.g., 128–512 Hz × multiple channels)
- Continuous Bluetooth transmission
- Risk of:
  - Packet loss
  - Latency spikes
  - CPU overhead from parsing raw data

**Impact:** Delayed or unstable focus/fatigue metrics.

---

### 🔹 Real-time Signal Filtering
- Band-pass filtering
- Artifact removal (eye blinks, movement)
- Signal normalization
- Window segmentation (sliding window inference)

**Risks:**
- Heavy CPU usage on mobile device
- Increased GC allocations in Unity (if not optimized)
- Frame drops below 30 FPS

**Critical because:**
- Reaction time must stay under 3 seconds.

---

### 🔹 Local AI Inference (On-Device Model)
- Running inference every X milliseconds
- ONNX / TensorFlow Lite execution

**Risks:**
- Model too large
- CPU inference blocking Unity main thread
- No hardware acceleration (if not using NNAPI)

**Constraints:**
- 75% accuracy for attention drop detection
- UI reaction time < 3 seconds

---

## 2️⃣ Unity AR Rendering

### 🔹 AR + EEG Processing in Parallel
Unity must handle simultaneously:
- AR spatial tracking
- Gesture recognition
- UI rendering
- EEG processing
- AI inference

**Risks:**
- Main thread congestion
- Frame drops (<30 FPS requirement)
- Thermal throttling on mobile device

---

### 🔹 Dynamic UI Adaptation
- Frequent color/animation changes based on EEG
- Shader updates
- Transparency & animation effects

**Risks:**
- Overdraw
- Expensive shaders
- Excessive UI rebuilds

---

## 3️⃣ Bluetooth & WebSocket Communication

### 🔹 Bluetooth Instability
- Signal interference
- Temporary disconnects
- Inconsistent sampling rate

**Risks:**
- Corrupted time windows
- Invalid model input
- User frustration

---

### 🔹 WebSocket EEG Streaming to Backend
- Real-time EEG session streaming
- Large data payloads
- High frequency JSON serialization

**Risks:**
- Serialization overhead
- Network congestion
- Backend overload during experiments

---

## 4️⃣ Backend API (Go)

### 🔹 High-Frequency Verification Calls (/verify)
If verification is triggered often:
- Rapid API calls during test mode
- Concurrent user sessions

**Risks:**
- Increased latency
- Database locking
- ML model response delay

---

### 🔹 AI Content Generation
- AI-generated flashcard sets
- External LLM usage (if added later)

**Risks:**
- Slow response times
- Cost scaling issues
- Blocking API threads

---

## 5️⃣ ML Model (Python Service)

### 🔹 Real-time Knowledge Verification
- Verifying if user knows phrase/flashcard
- Processing EEG-derived features + session data

**Risks:**
- Slow inference time
- Model too complex for real-time use
- High memory usage

---

### 🔹 Model Training Pipeline
- Training on recorded EEG sessions
- Large dataset preprocessing

**Risks:**
- Long retraining cycles
- Overfitting
- Dataset imbalance

---

## 6️⃣ Database (PostgreSQL)

### 🔹 EEG Session Logging
- Storing raw EEG streams
- High write frequency
- Time-series data growth

**Risks:**
- Large storage usage
- Slow queries
- Index bloat

---

### 🔹 Progress Synchronization
- Frequent sync between:
  - Mobile app
  - Web dashboard
  - Backend

**Risks:**
- Race conditions
- Conflict resolution complexity
- Increased DB load

---

## 7️⃣ Data Synchronization & Offline Mode

### 🔹 Conflict Resolution
- Offline progress
- Simultaneous updates from multiple devices

**Risks:**
- Inconsistent state
- Data loss
- Duplicate entries

---

## 8️⃣ Thermal & Power Constraints

### 🔹 Mobile Device Overheating
Caused by:
- Continuous AR rendering
- Continuous EEG processing
- Continuous AI inference
- Network communication

**Risks:**
- CPU throttling
- Frame rate drops
- Session crashes
- Battery drain

---

# 🎯 Most Critical Bottlenecks (Priority Order)

1. On-device EEG preprocessing + AI inference
2. Unity main thread overload (AR + AI)
3. Bluetooth stability
4. Backend verification latency
5. Database growth from EEG logs
6. Thermal throttling

---

# 🛠 Areas That Require Early Optimization

- Move EEG processing to background threads
- Use circular buffers instead of dynamic lists
- Reduce model size (quantization / pruning)
- Store EEG logs in compressed format