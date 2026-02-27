# API Specification

## Authentication
All endpoints require JWT token in Authorization header:

Authorization: Bearer <your_jwt_token>

### Authentication
| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/auth/register` | User registration |
| POST | `/auth/login` | Login (returns JWT) |
| POST | `/auth/refresh` | Refresh JWT token |
| POST | `/auth/google` | Google OAuth login |

### Memory Palaces
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/palaces` | List user's palaces |
| POST | `/palaces` | Create new palace |
| GET | `/palaces/{id}` | Get palace details |
| PUT | `/palaces/{id}` | Update palace |
| DELETE | `/palaces/{id}` | Delete palace |
| GET | `/palaces/{id}/objects` | List objects in palace |
| POST | `/palaces/{id}/objects` | Add object to palace |
| PUT | `/objects/{objectId}` | Update object |
| DELETE | `/objects/{objectId}` | Delete object |

### Learning Sets
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/sets` | List learning sets |
| POST | `/sets/generate` | Generate AI set |
| GET | `/sets/{id}` | Get specific set |
| POST | `/sets/upload` | Upload custom set |
| GET | `/sets/recommended` | Get recommended sets |

### Progress & Sync
| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/progress/summary` | Get progress summary |
| GET | `/progress/palace/{id}` | Get palace progress |
| POST | `/progress/sync` | Sync offline data |
| GET | `/stats/eeg` | Get EEG statistics |
| POST | `/stats/eeg/batch` | Batch EEG data upload |

### Knowledge Verification
| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/verify/knowledge` | Verify flashcard knowledge |
| POST | `/verify/session` | Verify entire session |
| GET | `/verify/difficulty/{objectId}` | Get suggested difficulty |

## WebSocket

### Events
| Event | Description |
|-------|-------------|
| `progress_update` | Real-time progress |
| `eeg_live` | Live EEG data |
| `focus_alert` | Focus drop alert |
| `session_start` | Start learning session |
| `session_end` | End learning session |