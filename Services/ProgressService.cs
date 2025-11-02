using System.Collections.Concurrent;
using System.Collections.Generic;

namespace TinyGenerator.Services
{
    public sealed class ProgressService
    {
        private readonly ConcurrentDictionary<string, List<string>> _store = new();
        private readonly ConcurrentDictionary<string, bool> _completed = new();
        private readonly ConcurrentDictionary<string, string?> _result = new();

        public void Start(string id)
        {
            _store[id] = new List<string>();
            _completed[id] = false;
            _result[id] = null;
        }

        public void Append(string id, string message)
        {
            if (!_store.ContainsKey(id)) Start(id);
            _store[id].Add(message);
        }

        public List<string> Get(string id)
        {
            if (_store.TryGetValue(id, out var list)) return new List<string>(list);
            return new List<string>();
        }

        public void MarkCompleted(string id, string? finalResult = null)
        {
            _completed[id] = true;
            _result[id] = finalResult;
        }

        public bool IsCompleted(string id) => _completed.TryGetValue(id, out var v) && v;

        public string? GetResult(string id) => _result.TryGetValue(id, out var r) ? r : null;

        public void Clear(string id)
        {
            _store.TryRemove(id, out _);
            _completed.TryRemove(id, out _);
            _result.TryRemove(id, out _);
        }
    }
}
