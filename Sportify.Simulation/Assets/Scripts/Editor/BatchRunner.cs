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
        // Same, for the rain and percolation analysis: -executeMethod
        // Sportify.Simulation.Editor.BatchRunner.RunPercolationAnalysis. Writes Recordings/percolation_results.json.
        public static void RunPercolationAnalysis()
        {
            Environment.SetEnvironmentVariable(AnalysisMode.EnvVar, AnalysisMode.Percolation);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }

        // Same, for the static structural load analysis: -executeMethod
        // Sportify.Simulation.Editor.BatchRunner.RunStructuralAnalysis. Writes Recordings/structure_results.json.
        public static void RunStructuralAnalysis()
        {
            Environment.SetEnvironmentVariable(AnalysisMode.EnvVar, AnalysisMode.Structural);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }

        // Same, for the dynamic structural analysis (crowds, weather, resonance): -executeMethod
        // Sportify.Simulation.Editor.BatchRunner.RunDynamicAnalysis. Writes Recordings/dynamic_results.json.
        public static void RunDynamicAnalysis()
        {
            Environment.SetEnvironmentVariable(AnalysisMode.EnvVar, AnalysisMode.Dynamic);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }

        public static void RunWindAnalysis()
        {
            Environment.SetEnvironmentVariable(AnalysisMode.EnvVar, AnalysisMode.Wind);
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }
    }
}
