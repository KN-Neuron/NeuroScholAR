# Testing Guide: Running XREAL Apps on Real Devices

## Important Note
XREAL Beam has only one USB-C port that supports debugging (the glasses port). This means **wireless debugging is required** since the glasses occupy the debugging port.

## Wireless Debugging Setup

### 1. On XREAL Beam
- Connect Beam to same WiFi network as your computer
- Enable Developer Options:
Settings > About > Tap "Build Number" 7 times

- Go to **Settings > Developer Options** and enable:
- ✅ Wireless Debugging
- ✅ USB Debugging (still enable even though using wireless)
- ✅ Tap the wireless debugging option and note the IP address

### 2. On Your Computer
Connect to Beam via ADB:
```bash
# Connect using Beam's IP address
adb connect [BEAM_IP_ADDRESS]:[BEAM_PORT]

# Verify connection
adb devices
# Expected output: [IP_ADDRESS]:[PORT] device
```

### 3. In Unity
- Build Settings > Android
- Click "Build and Run"
- Unity will deploy directly to Beam wirelessly
- App should appear in glasses automatically (if not, you can run it manually, look at the end of this document)

## Testing Workflow
- Ensure Beam and computer on same network
- Connect to Beam via ADB (one-time per session)
- Make code changes in Unity
- Build and Run - deploys wirelessly
- Test in glasses immediately

## Common Issues & Fixes
| Problem                             | Solution                                       |
|-------------------------------------|------------------------------------------------|
| `adb connect` fails                 | Check both devices on same WiFi                |
| Connection refused                  | Enable Wireless Debugging on Beam              |
| Build succeeds but app won't launch | Launch manually from Beam home screen          |
| Can't see logs                      | Run `adb logcat -s Unity` in separate terminal | 

## Quick Test Checklist
- Beam and computer on same WiFi network
- Wireless Debugging enabled on Beam
- adb connect [IP]:[PORT] successful
- adb devices shows your Beam
- Unity Build and Run deploys successfully

## Useful ADB Commands
```bash
# View live logs from your app
adb logcat -s Unity

# Manually start your app
adb shell am start -n com.UnityTechnologies.com.unity.template.urpblank/com.unity3d.player.UnityPlayerActivity

# Force stop your app
adb shell am force-stop com.yourcompany.yourapp

# Reconnect if disconnected
adb connect [BEAM_IP]:5555

# List connected devices
adb devices
```