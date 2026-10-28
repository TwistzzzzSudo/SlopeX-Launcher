using System;
using System.Collections.Generic;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModBase
{
    /// <summary>游戏进程输出中枢：落盘由调用方负责，这里只做广播与短缓冲。</summary>
    public static class GameLogHub
    {
        private static readonly object Gate = new object();
        private static readonly List<string> Buffer = new List<string>(400);
        private const int Max = 400;

        public static event Action<string> Line;

        public static void Push(string line)
        {
            lock (Gate)
            {
                Buffer.Add(line);
                if (Buffer.Count > Max) Buffer.RemoveRange(0, Buffer.Count - Max);
            }
            try { Line?.Invoke(line); } catch { }
        }

        public static IReadOnlyList<string> Snapshot()
        {
            lock (Gate) return Buffer.ToArray();
        }

        public static void Clear()
        {
            lock (Gate) Buffer.Clear();
        }
    }
}
