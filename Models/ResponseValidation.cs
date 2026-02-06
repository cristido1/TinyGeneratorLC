using System;
using System.Collections.Generic;

namespace TinyGenerator.Models
{
    public sealed class ResponseValidation
    {
        public long LogId { get; }
        public bool Successed { get; }
        public IReadOnlyList<string> ErrorMessages { get; }

        public ResponseValidation(long logId, bool successed, IReadOnlyList<string> errorMessages)
        {
            if (logId <= 0) throw new ArgumentOutOfRangeException(nameof(logId), "LogId must be a positive value.");
            LogId = logId;
            Successed = successed;
            ErrorMessages = errorMessages ?? throw new ArgumentNullException(nameof(errorMessages));
        }
    }
}
