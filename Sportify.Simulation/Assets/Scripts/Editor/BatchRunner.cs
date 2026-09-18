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
            EditorSceneManager.OpenScene(ScenePath);
            EditorApplication.isPlaying = true;
        }
    }
}
