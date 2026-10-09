using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Editor.Workspace.Settings;

namespace PingCore.Editor.Workspace.Pipeline
{
    /// <summary>The outcome of loading the state file.</summary>
    public sealed class DeployStateLoad
    {
        public DeployStateLoad(DeployState state, string problem)
        {
            State = state;
            Problem = problem;
        }

        /// <summary>The saved state, or null when there is none or it was unusable.</summary>
        public DeployState State { get; }

        /// <summary>Why a file that existed was not used (it was moved aside), or null.</summary>
        public string Problem { get; }
    }

    /// <summary>Reads and writes <c>UserSettings/PingCoreDeployState.json</c>.</summary>
    public static class DeployStateFile
    {
        /// <summary>The path under the project root.</summary>
        public const string RelativePath = "UserSettings/PingCoreDeployState.json";

        internal static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
            Formatting = Formatting.Indented,
        };

        /// <summary>The state file of a project.</summary>
        public static string PathIn(string projectRoot) => Path.Combine(projectRoot, "UserSettings", "PingCoreDeployState.json");

        /// <summary>
        /// Loads the saved state. Never throws: a missing file is no state; a corrupt or foreign
        /// file is moved aside to <c>PingCoreDeployState.json.corrupt</c> and reported, so the
        /// next run starts clean instead of failing forever.
        /// </summary>
        public static DeployStateLoad Load(string projectRoot)
        {
            string path = PathIn(projectRoot);
            if (!File.Exists(path))
            {
                return new DeployStateLoad(null, null);
            }

            string problem;
            try
            {
                JObject parsed;
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(path, Encoding.UTF8))) { DateParseHandling = DateParseHandling.None })
                {
                    parsed = JObject.Load(reader);
                }

                if ((string)parsed["format"] == DeployState.Format)
                {
                    DeployState state = parsed.ToObject<DeployState>(JsonSerializer.Create(Json));
                    if (state != null && state.Locations != null)
                    {
                        return new DeployStateLoad(state, null);
                    }
                }

                problem = $"{Path.GetFileName(path)} is not a {DeployState.Format} file";
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is InvalidCastException || e is ArgumentException || e is FormatException || e is UnauthorizedAccessException)
            {
                problem = $"{Path.GetFileName(path)} could not be read ({e.GetType().Name})";
            }

            try
            {
                string aside = path + ".corrupt";
                if (File.Exists(aside))
                {
                    File.Delete(aside);
                }

                File.Move(path, aside);
                problem += "; it was moved to " + Path.GetFileName(aside) + " and the pipeline starts fresh";
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                problem += $"; it could not be moved aside ({e.GetType().Name})";
            }

            return new DeployStateLoad(null, problem);
        }

        /// <summary>Writes the state (UTF-8, LF, atomic replace). Refuses a credential-shaped value.</summary>
        public static void Save(string projectRoot, DeployState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            SettingsFile.RefuseSecrets(state);
            SettingsFile.Save(PathIn(projectRoot), state);
        }

        /// <summary>Removes the state (a finished run the developer dismissed). False when there was none.</summary>
        public static bool Delete(string projectRoot)
        {
            string path = PathIn(projectRoot);
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
    }
}
