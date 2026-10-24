using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace NeuroScholAR.App
{
    /// <summary>
    /// Runtime settings that must not live in the code or in git (spec: no hardcoded URLs or secrets).
    /// Read from <c>{persistentDataPath}/appconfig.json</c>; the environment variable
    /// <c>NEUROSCHOLAR_BACKEND_URL</c> overrides it (handy in the Editor). See appconfig.example.json.
    /// </summary>
    public sealed class AppConfig
    {
        [JsonProperty("backend_url")] public string BackendUrl;

        /// <summary>Start with the simulator selected (demo / no headband).</summary>
        [JsonProperty("use_simulator")] public bool UseSimulator = true;

        public static string FilePath => Path.Combine(Application.persistentDataPath, "appconfig.json");

        public static AppConfig Load()
        {
            var config = new AppConfig();
            try
            {
                if (File.Exists(FilePath))
                {
                    config = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(FilePath)) ?? config;
                }
            }
            catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"appconfig.json ignored: {e.Message}");
            }

            var env = Environment.GetEnvironmentVariable("NEUROSCHOLAR_BACKEND_URL");
            if (!string.IsNullOrWhiteSpace(env))
            {
                config.BackendUrl = env;
            }

            return config;
        }
    }
}
