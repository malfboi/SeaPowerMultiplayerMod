using System;
using System.Collections.Generic;
using UnityEngine;

namespace SeapowerMultiplayer
{
    public static class OrderDelayQueue
    {
        private struct DelayedAction
        {
            public float ExecuteAtRealtime;
            public Action Action;
        }

        private static readonly List<DelayedAction> _queue = new();

        public static int PendingCount => _queue.Count;

        private const int MinRttThresholdMs = 10;
        private const int MaxDelayMs = 250;

        public static void Enqueue(Action action)
        {
            if (!Plugin.Instance.CfgPvP.Value || !NetworkManager.Instance.IsConnected)
            {
                action();
                return;
            }

            int rtt = NetworkManager.Instance.LastRttMs;
            if (rtt < MinRttThresholdMs)
            {
                action();
                return;
            }

            int delayMs = Math.Min(rtt / 2, MaxDelayMs);
            _queue.Add(new DelayedAction
            {
                ExecuteAtRealtime = Time.realtimeSinceStartup + delayMs / 1000f,
                Action = action,
            });
        }

        public static void Tick()
        {
            if (_queue.Count == 0) return;

            float now = Time.realtimeSinceStartup;
            int i = 0;
            while (i < _queue.Count)
            {
                if (_queue[i].ExecuteAtRealtime <= now)
                {
                    _queue[i].Action();
                    _queue.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
        }

        public static void Clear()
        {
            _queue.Clear();
        }
    }
}
