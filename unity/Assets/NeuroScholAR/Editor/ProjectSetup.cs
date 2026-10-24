using System.IO;
using System.Linq;
using NeuroScholAR.App;
using NeuroScholAR.App.Palace;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Management;

namespace NeuroScholAR.Editor
{
    /// <summary>
    /// One-click (or batchmode) project setup: generates the scene, Android player settings and the XR loader,
    /// so nothing about the build lives in hand-edited YAML.
    /// Batchmode: Unity -batchmode -quit -buildTarget Android -projectPath unity -executeMethod NeuroScholAR.Editor.ProjectSetup.SetupAndBuild
    /// </summary>
    public static class ProjectSetup
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";
        private const string ApkPath = "Builds/NeuroScholAR.apk";

        [MenuItem("NeuroScholAR/Setup Project")]
        public static void Setup()
        {
            ConfigurePlayer();
            ConfigureXr();
            CreateScene();
            AssetDatabase.SaveAssets();
            Debug.Log("NeuroScholAR: project set up.");
        }

        [MenuItem("NeuroScholAR/Build Android APK")]
        public static void Build()
        {
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = ApkPath,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            Debug.Log($"NeuroScholAR: build {report.summary.result}, {report.summary.totalErrors} errors → {ApkPath}");
            if (Application.isBatchMode && report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                EditorApplication.Exit(1);
            }
        }

        public static void SetupAndBuild()
        {
            Setup();
            Build();
        }

        private static void ConfigurePlayer()
        {
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            PlayerSettings.companyName = "Neuron";
            PlayerSettings.productName = "NeuroScholAR";
            var android = NamedBuildTarget.Android;
            PlayerSettings.SetApplicationIdentifier(android, "com.neuron.neuroscholar");
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29; // Android 10
            PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

            // The export backend usually runs on a laptop in the same Wi-Fi, over plain HTTP.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            IncludeShaders(new[] { "NeuroScholAR/Simple", "Sprites/Default" }, new[] { "Standard" });

            // Input System only (the AR camera's TrackedPoseDriver and our input both use it).
            var settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
            if (settings != null)
            {
                var so = new SerializedObject(settings);
                var handler = so.FindProperty("activeInputHandler");
                if (handler != null)
                {
                    handler.intValue = 1;
                    so.ApplyModifiedProperties();
                }
            }

            // Compile the real BrainBit adapter when the SDK DLL has been imported (see README).
            var defines = PlayerSettings.GetScriptingDefineSymbols(android).Split(';').Where(d => d.Length > 0).ToList();
            var hasSdk = AssetDatabase.FindAssets("XNeuroSDK").Any() || Directory.Exists("Assets/Plugins/NeuroSDK");
            defines.Remove("NEUROSDK2");
            if (hasSdk)
            {
                defines.Add("NEUROSDK2");
            }

            PlayerSettings.SetScriptingDefineSymbols(android, string.Join(";", defines));
            PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget.Standalone, string.Join(";", defines));
        }

        /// <summary>
        /// Materials are created in code (Shader.Find), so nothing references these shaders and builds would strip
        /// them. <paramref name="remove"/> drops shaders an older setup added (Standard: ~80k variants to compile).
        /// </summary>
        private static void IncludeShaders(string[] names, string[] remove)
        {
            var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
            if (graphics == null)
            {
                return;
            }

            var so = new SerializedObject(graphics);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            for (var i = list.arraySize - 1; i >= 0; i--)
            {
                var included = list.GetArrayElementAtIndex(i).objectReferenceValue as Shader;
                if (included != null && System.Array.IndexOf(remove, included.name) >= 0)
                {
                    list.GetArrayElementAtIndex(i).objectReferenceValue = null;
                    list.DeleteArrayElementAtIndex(i);
                }
            }

            foreach (var name in names)
            {
                var shader = Shader.Find(name);
                var present = false;
                for (var i = 0; i < list.arraySize; i++)
                {
                    present |= list.GetArrayElementAtIndex(i).objectReferenceValue == shader;
                }

                if (shader != null && !present)
                {
                    list.InsertArrayElementAtIndex(list.arraySize);
                    list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>XREAL loader when the XREAL package is installed (glasses + Beam Pro), ARCore otherwise (phone only).</summary>
        private static void ConfigureXr()
        {
            const string folder = "Assets/XR";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                AssetDatabase.CreateFolder("Assets", "XR");
            }

            var perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(folder + "/XRGeneralSettingsPerBuildTarget.asset");
            if (perTarget == null)
            {
                perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(perTarget, folder + "/XRGeneralSettingsPerBuildTarget.asset");
            }

            EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
            if (!perTarget.HasSettingsForBuildTarget(BuildTargetGroup.Android))
            {
                perTarget.CreateDefaultSettingsForBuildTarget(BuildTargetGroup.Android);
            }

            if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            {
                perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            }

            var settings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
            settings.InitManagerOnStart = true;
            var manager = settings.AssignedSettings;
            const string xreal = "Unity.XR.XREAL.XREALXRLoader";
            const string arcore = "UnityEngine.XR.ARCore.ARCoreLoader";
            var useXreal = System.Type.GetType(xreal + ", Unity.XR.XREAL") != null;
            XRPackageMetadataStore.RemoveLoader(manager, useXreal ? arcore : xreal, BuildTargetGroup.Android);
            XRPackageMetadataStore.AssignLoader(manager, useXreal ? xreal : arcore, BuildTargetGroup.Android);
            Debug.Log("NeuroScholAR: Android XR loader = " + (useXreal ? "XREAL" : "ARCore"));
            if (useXreal)
            {
                RegisterXrealSettings(folder);
            }

            EditorUtility.SetDirty(perTarget);
        }

        /// <summary>
        /// XREAL's build step only ships its settings when they are registered under their config key, which
        /// the XR Plug-in Management UI does when you tick XREAL. Assigning the loader from code does not, and
        /// then the plugin fails at startup with "Failed to get XREAL Settings" (nothing on the glasses).
        /// </summary>
        private static void RegisterXrealSettings(string folder)
        {
            const string key = "com.unity.xr.management.xrealsettings";
            var path = folder + "/Settings/XREALSettings.asset";
            var settings = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (settings == null)
            {
                if (!AssetDatabase.IsValidFolder(folder + "/Settings"))
                {
                    AssetDatabase.CreateFolder(folder, "Settings");
                }

                settings = ScriptableObject.CreateInstance("Unity.XR.XREAL.XREALSettings");
                AssetDatabase.CreateAsset(settings, path);
            }

            // Hands (pinch, aim ray) and the Beam Pro controller at the same time. Value of Unity.XR.XREAL.InputSource.ControllerAndHands.
            var so = new SerializedObject(settings);
            var input = so.FindProperty("InitialInputSource");
            if (input != null)
            {
                input.intValue = 3;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
            }

            EditorBuildSettings.AddConfigObject(key, settings, true);
            Debug.Log("NeuroScholAR: XREAL settings registered (input: controller + hands)");
        }

        private static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);

            new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));

            var origin = new GameObject("XR Origin").AddComponent<XROrigin>();
            origin.gameObject.AddComponent<ARPlaneManager>(); // surfaces for placing the palace objects (SurfacePlacer)
            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(origin.transform, false);
            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            cameraGo.transform.SetParent(offset.transform, false);
            var cam = cameraGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear; // black = see-through on XREAL; ARCore draws the camera feed
            cam.nearClipPlane = 0.05f;
            cameraGo.AddComponent<AudioListener>();
            cameraGo.AddComponent<ARCameraManager>();
            cameraGo.AddComponent<ARCameraBackground>();
            var pose = cameraGo.AddComponent<TrackedPoseDriver>();
            pose.positionInput = new InputActionProperty(Action("Position", "Vector3",
                "<XRHMD>/centerEyePosition", "<HandheldARInputDevice>/devicePosition"));
            pose.rotationInput = new InputActionProperty(Action("Rotation", "Quaternion",
                "<XRHMD>/centerEyeRotation", "<HandheldARInputDevice>/deviceRotation"));
            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offset;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Device;

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            new GameObject("App").AddComponent<AppRoot>();
            cameraGo.AddComponent<EditorLook>();

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static InputAction Action(string name, string type, params string[] bindings)
        {
            var action = new InputAction(name, InputActionType.Value, expectedControlType: type);
            foreach (var b in bindings)
            {
                action.AddBinding(b);
            }

            return action;
        }
    }
}
