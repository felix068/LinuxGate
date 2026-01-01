using System.Collections.Generic;
using LinuxGate.Models;

namespace LinuxGate.Helpers
{
    public static class StateManager
    {
        private static readonly Dictionary<string, PageState> _states = new Dictionary<string, PageState>();

        public static void SaveState(string key, PageState state)
        {
            _states[key] = state;
        }

        public static PageState GetState(string key)
        {
            return _states.TryGetValue(key, out var state) ? state : null;
        }

        public static void ClearState(string key)
        {
            if (_states.ContainsKey(key))
            {
                _states.Remove(key);
            }
        }

        public static void ClearDependentStates(string key)
        {
            var keysToRemove = new List<string>();
            foreach (var stateKey in _states.Keys)
            {
                if (stateKey.StartsWith(key + "_"))
                {
                    keysToRemove.Add(stateKey);
                }
            }
            foreach (var keyToRemove in keysToRemove)
            {
                _states.Remove(keyToRemove);
            }
        }
    }
}
