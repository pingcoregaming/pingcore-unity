using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>One finding as written to the verdict file.</summary>
    [Serializable]
    public sealed class BuildGuardVerdictFinding
    {
        public string reason;
        public string location;
        public string detail;
    }

    /// <summary>
    /// The verdict file, <c>Library/pingcore-build-guard.json</c>. <c>verdict</c> is <c>pending</c>
    /// while a build runs, then <c>pass</c> or <c>fail</c>; <c>reasons</c> holds the distinct reason
    /// codes. It never contains a matched token.
    /// </summary>
    [Serializable]
    public sealed class BuildGuardVerdict
    {
        public const string Pending = "pending";
        public const string Pass = "pass";
        public const string Fail = "fail";

        public string verdict;
        public string stage;
        public string outputPath;
        public string[] reasons = Array.Empty<string>();
        public BuildGuardVerdictFinding[] findings = Array.Empty<BuildGuardVerdictFinding>();
        public string[] warnings = Array.Empty<string>();

        /// <summary>Why the guard could not finish a check (it then fails closed). Empty on a normal verdict.</summary>
        public string[] errors = Array.Empty<string>();

        /// <summary>
        /// Postprocess only: <c>complete</c> when every output file was byte-scanned and the assembly
        /// list was read, <c>unavailable</c> when the verdict rests on the source-side scan. Empty at preprocess.
        /// </summary>
        public string outputScan = string.Empty;

        /// <summary>Why <see cref="outputScan"/> is <c>unavailable</c>.</summary>
        public string[] outputScanNotes = Array.Empty<string>();
        public bool HasReason(string code) => reasons != null && Array.IndexOf(reasons, code) >= 0;

        public static BuildGuardVerdict Create(string verdict, string stage, string outputPath,
            IReadOnlyList<BuildGuardFinding> findings, IReadOnlyList<string> warnings, IReadOnlyList<string> errors = null)
        {
            var reasonCodes = new List<string>();
            var written = new List<BuildGuardVerdictFinding>();
            if (findings != null)
            {
                foreach (BuildGuardFinding finding in findings)
                {
                    if (!reasonCodes.Contains(finding.Code))
                    {
                        reasonCodes.Add(finding.Code);
                    }

                    written.Add(new BuildGuardVerdictFinding
                    {
                        reason = finding.Code,
                        location = finding.Location,
                        detail = finding.Detail,
                    });
                }
            }

            return new BuildGuardVerdict
            {
                verdict = verdict,
                stage = stage,
                outputPath = outputPath ?? string.Empty,
                reasons = reasonCodes.ToArray(),
                findings = written.ToArray(),
                warnings = warnings == null ? Array.Empty<string>() : new List<string>(warnings).ToArray(),
                errors = errors == null ? Array.Empty<string>() : new List<string>(errors).ToArray(),
            };
        }
    }

    /// <summary>Reads and writes <c>Library/pingcore-build-guard.json</c>.</summary>
    public static class BuildGuardVerdictFile
    {
        public const string RelativePath = "Library/pingcore-build-guard.json";

        public static string GetPath(string projectRoot) => Path.Combine(projectRoot, RelativePath);

        public static void Write(string projectRoot, BuildGuardVerdict verdict)
        {
            string path = GetPath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(verdict, true));
        }

        /// <summary>The last verdict, or null when no build has written one.</summary>
        public static BuildGuardVerdict Read(string projectRoot)
        {
            string path = GetPath(projectRoot);
            return File.Exists(path) ? JsonUtility.FromJson<BuildGuardVerdict>(File.ReadAllText(path)) : null;
        }
    }
}
