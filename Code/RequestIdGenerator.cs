using System;

namespace TinyGenerator.Services
{
    /// <summary>
    /// Genera identificatori univoci per tracciare request/response nei log.
    /// Formato: {timestamp}_{guid_short}
    /// Esempio: 20260209_142345_a3f2e1d4
    /// </summary>
    public static class RequestIdGenerator
    {
        /// <summary>
        /// Genera un nuovo request ID univoco
        /// </summary>
        public static string Generate()
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var guidShort = Guid.NewGuid().ToString("N")[..8];
            return $"{timestamp}_{guidShort}";
        }
    }
}
