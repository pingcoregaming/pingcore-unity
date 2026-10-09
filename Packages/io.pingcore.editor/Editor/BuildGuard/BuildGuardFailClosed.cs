using System;
using System.Collections.Generic;
using UnityEditor.Build;
using UnityEngine;

namespace PingCore.Editor.BuildGuard
{
    /// <summary>
    /// What a postprocess stage does when its checks cannot finish: delete what the build produced,
    /// write a failing verdict with <c>scan_error</c>, and hand back the <see cref="BuildFailedException"/>
    /// to throw. A guard that could not see the whole build never lets it through.
    /// </summary>
    internal static class BuildGuardFailClosed
    {
        internal static BuildFailedException Fail(string stage, string projectRoot, string outputPath, IEnumerable<string> reportFiles, Exception error)
        {
            try
            {
                string outputFull = BuildOutputScanner.ResolveOutputPath(projectRoot, outputPath);
                BuildOutputScanner.DeleteProduced(projectRoot, outputFull, BuildOutputScanner.ProducedPathsForDeletion(outputFull, reportFiles));
            }
            catch (Exception deleteError)
            {
                Debug.LogError("[PingCore build guard] could not delete the rejected output at " + outputPath + " (" + deleteError.GetType().Name
                    + "). Delete it by hand and never ship it.");
            }

            return BuildGuardContext.FailClosed(projectRoot, stage, outputPath, error);
        }
    }
}
