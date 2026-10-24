using System.Threading.Tasks;
using NeuroScholAR.Core.Eeg;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

namespace NeuroScholAR.App.Eeg
{
    /// <summary>Picks the EEG source. The single place to swap the real device adapter in or out.</summary>
    public static class EegSources
    {
        /// <summary>True when the BrainBit SDK is compiled in (NEUROSDK2 define, see README).</summary>
        public static bool DeviceAvailable =>
#if NEUROSDK2
            true;
#else
            false;
#endif

        public static IEegSource Create(bool simulated)
        {
#if NEUROSDK2
            if (!simulated)
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                // The C# SDK only P/Invokes (dlopen), which skips JNI_OnLoad, so the native BLE layer has no JavaVM
                // and no com.neurosdk2 classes: scanning silently finds nothing. MAUI's UseNeurosdk2() fixes this
                // with System.loadLibrary; initializing the SDK's Scanner class does the same (its static init loads
                // the library from the app class loader). Must run on an attached thread: this is the main thread.
                LoadNativeSdk();
#endif
                return new BrainBitEegSource();
            }
#endif
            // Demo profile: attention drifts slowly, with regular blinks: the HUD and the rules have something to show.
            return new SimulatedEegSource(new SimulatedEegOptions
            {
                RealTime = true,
                Focus = FocusProfiles.Wave(120),
                Seed = Random.Range(0, int.MaxValue),
            });
        }

#if NEUROSDK2 && UNITY_ANDROID && !UNITY_EDITOR
        private static bool _nativeLoaded;

        /// <summary>
        /// The SDK picks its native API from RuntimeInformation and by running `getprop` (Process.Start), neither of
        /// which is reliable under IL2CPP; a miss throws "platform not supported" before any scan. We know the
        /// answer, so set its cached internal value (NeuroSDK.Platform._platformType) up front.
        /// ponytail: reflection on an SDK internal; recheck the field name if XNeuroSDK is upgraded.
        /// </summary>
        private static void PinSdkPlatform()
        {
            try
            {
                var field = typeof(NeuroSDK.Scanner).Assembly.GetType("NeuroSDK.Platform")
                    ?.GetField("_platformType", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var enumType = field == null ? null : System.Nullable.GetUnderlyingType(field.FieldType);
                if (enumType == null)
                {
                    Debug.LogWarning("NeuroScholAR: NeuroSDK.Platform._platformType not found; using SDK detection");
                    return;
                }

                var abi = System.IntPtr.Size == 8 ? "AndroidARMv8" : "AndroidARMv7";
                field.SetValue(null, System.Enum.Parse(enumType, abi));
                Debug.Log("NeuroScholAR: NeuroSDK platform pinned to " + abi);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("NeuroScholAR: could not pin NeuroSDK platform: " + e.Message);
            }
        }

        private static void LoadNativeSdk()
        {
            if (_nativeLoaded)
            {
                return;
            }

            PinSdkPlatform();

            // Class.forName(name, initialize: true, appLoader). Explicit JNI signature: Unity would derive one from
            // the loader's runtime class (PathClassLoader) and miss the method.
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var loader = activity.Call<AndroidJavaObject>("getClassLoader"))
            {
                var classClass = AndroidJNI.FindClass("java/lang/Class");
                var forName = AndroidJNI.GetStaticMethodID(classClass, "forName",
                    "(Ljava/lang/String;ZLjava/lang/ClassLoader;)Ljava/lang/Class;");
                var name = AndroidJNI.NewStringUTF("com.neurosdk2.neuro.Scanner");
                var scanner = AndroidJNI.CallStaticObjectMethod(classClass, forName, new[]
                {
                    new jvalue { l = name }, new jvalue { z = true }, new jvalue { l = loader.GetRawObject() },
                });
                var error = AndroidJNI.ExceptionOccurred();
                if (error != System.IntPtr.Zero)
                {
                    AndroidJNI.ExceptionClear();
                    AndroidJNI.DeleteLocalRef(error);
                    Debug.LogError("NeuroScholAR: could not load neurosdk2 (is neurosdk2.aar in Plugins/Android?)");
                }
                else
                {
                    _nativeLoaded = true;
                    Debug.Log("NeuroScholAR: neurosdk2 loaded through Java");
                }

                AndroidJNI.DeleteLocalRef(scanner);
                AndroidJNI.DeleteLocalRef(name);
                AndroidJNI.DeleteLocalRef(classClass);
            }
        }
#endif
    }

    /// <summary>Android 12+ needs BLUETOOTH_SCAN/CONNECT at runtime; older versions need location for BLE scans.</summary>
    public static class BluetoothPermissions
    {
        public static Task<bool> EnsureAsync()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var needed = AndroidSdk() >= 31
                ? new[] { "android.permission.BLUETOOTH_SCAN", "android.permission.BLUETOOTH_CONNECT" }
                : new[] { Permission.FineLocation };
            var missing = System.Array.FindAll(needed, p => !Permission.HasUserAuthorizedPermission(p));
            if (missing.Length == 0)
            {
                return Task.FromResult(true);
            }

            var tcs = new TaskCompletionSource<bool>();
            var remaining = missing.Length;
            var granted = true;
            var callbacks = new PermissionCallbacks();
            void Done(bool ok)
            {
                granted &= ok;
                if (--remaining == 0)
                {
                    tcs.TrySetResult(granted);
                }
            }

            callbacks.PermissionGranted += _ => Done(true);
            callbacks.PermissionDenied += _ => Done(false);
            Permission.RequestUserPermissions(missing, callbacks);
            return tcs.Task;
#else
            return Task.FromResult(true);
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static int AndroidSdk()
        {
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
            {
                return version.GetStatic<int>("SDK_INT");
            }
        }
#endif
    }
}
