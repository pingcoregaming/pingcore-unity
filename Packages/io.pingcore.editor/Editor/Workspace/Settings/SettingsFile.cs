using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PingCore.Editor.Workspace.Redaction;

namespace PingCore.Editor.Workspace.Settings
{
    /// <summary>Shared load and save for the two settings files.</summary>
    internal static class SettingsFile
    {
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            DateParseHandling = DateParseHandling.None,
            TypeNameHandling = TypeNameHandling.None,
            MetadataPropertyHandling = MetadataPropertyHandling.Ignore,
            MaxDepth = 16,
            Formatting = Formatting.Indented,
        };

        public static T Load<T>(string path, string format)
            where T : class, new()
        {
            if (!File.Exists(path))
            {
                return new T();
            }

            JObject parsed;
            try
            {
                parsed = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not valid JSON ({e.GetType().Name}).");
            }

            if ((string)parsed["format"] != format)
            {
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} is not in format {format}.");
            }

            try
            {
                return parsed.ToObject<T>(JsonSerializer.Create(Json));
            }
            catch (JsonException e)
            {
                throw new InvalidDataException($"{System.IO.Path.GetFileName(path)} has a field of the wrong type ({e.GetType().Name}).");
            }
        }

        public static void Save(string path, object settings)
        {
            string json = JsonConvert.SerializeObject(settings, Json).Replace("\r\n", "\n") + "\n";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, null);
            }
            else
            {
                File.Move(temp, path);
            }
        }

        /// <summary>
        /// Throws when any string in the settings looks like a credential (it names the field, never the value).
        /// The rule is the command line's (<see cref="Redactor.LooksLikeCredential"/>: a token prefix and eight
        /// or more letters or digits), stricter than the log mask, because a settings file is read by whoever
        /// opens the project folder.
        /// </summary>
        public static void RefuseSecrets(object settings)
        {
            JObject tree = JObject.FromObject(settings, JsonSerializer.Create(Json));
            foreach (JValue value in tree.DescendantsAndSelf().OfType<JValue>())
            {
                if (value.Type == JTokenType.String && Redactor.LooksLikeCredential((string)value))
                {
                    throw new InvalidOperationException($"Refusing to save {value.Path}: it looks like a credential, and settings files hold ids and names only.");
                }
            }
        }
    }
}
