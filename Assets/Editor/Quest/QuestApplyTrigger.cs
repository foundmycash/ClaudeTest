using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace QuestOptimization.Editor
{
    // Temporary: applies the Quest settings inside the running editor when the trigger file exists, then reports.
    [InitializeOnLoad]
    static class QuestApplyTrigger
    {
        const string Dir = @"C:\Users\cashs\AppData\Local\Temp\claude\D--Game-GithubClone-ClaudeTest\23076cd8-aaf7-40b5-a587-4d224fc4eab3\scratchpad";

        static QuestApplyTrigger()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess())
                return;
            EditorApplication.delayCall += Run;
        }

        static void Run()
        {
            string trigger = Path.Combine(Dir, "apply_quest.txt");
            if (!File.Exists(trigger))
                return;
            File.Delete(trigger);

            var log = new StringBuilder();
            Application.LogCallback capture = (message, stack, type) => log.AppendLine($"[{type}] {message}");
            Application.logMessageReceived += capture;
            try
            {
                QuestSetup.Apply();
                EditorApplication.ExecuteMenuItem("File/Save Project");
                QuestSetup.Check();
            }
            finally
            {
                Application.logMessageReceived -= capture;
                File.WriteAllText(Path.Combine(Dir, "apply_quest_result.txt"), log.ToString());
            }
        }
    }
}
