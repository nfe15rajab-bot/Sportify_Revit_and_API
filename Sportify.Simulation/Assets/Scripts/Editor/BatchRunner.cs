using System;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Sportify.Simulation.Editor
{
    public static class BatchRunner
    {
        const string ScenePath = "Assets/Scenes/CollisionAnalysis.unity";

        // Invoked headlessly via:
        //   Unity.exe -batchmode -projectPath <proj> -layoutFile=<json>
        //     -executeMethod Sportify.Simulation.Editor.BatchRunner.RunCollisionAnalysis
        //     -logFile <log>
        // No -quit: entering play mode is asynchronous, and the process must stay
        // alive until CollisionAnalysisRunner's own WriteReport() calls
        // EditorApplication.Exit once the simulation actually finishes.
        public static void RunCollisionAnalysis()
        {
            Environment.SetEnvironmentVariable(AnalysisMode.EnvVar, null);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }

        // Same, for the garden analysis (wind uplift and erosion): -executeMethod
        // Sportify.Simulation.Editor.BatchRunner.RunWindAnalysis. Writes Recordings/wind_results.json.
        public static void RunWindAnalysis()
        {
            Environment.SetEnvironmentVariable(AnalysisMode.EnvVar, AnalysisMode.Wind);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }
    }
}
