using System;
using System.IO;
using UnityEngine;

namespace ReachSurvival
{
    /// <summary>
    /// Our own log at %LOCALAPPDATA%/ReachSurvival/logs/latest.log. Melty reads the "Hosting at " line from it
    /// to give friends a join link, so that line's wording must not change.
    /// </summary>
    public static class GameLog
    {
        static StreamWriter writer;

        public static void Init()
        {
            writer = new StreamWriter(new FileStream(Paths.LogFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Application.logMessageReceived += (msg, stack, type) =>
                writer?.WriteLine($"{DateTime.Now:HH:mm:ss} {type}: {msg}" + (type == LogType.Exception ? "\n" + stack : ""));
        }

        public static void Info(string s) => Debug.Log(s);
    }
}
