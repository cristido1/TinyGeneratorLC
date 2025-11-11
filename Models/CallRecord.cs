using System;

namespace TinyGenerator.Models;

public class CallRecord
{
    public long Id { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int Tokens { get; set; }
    public double Cost { get; set; }
    public string Request { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
}
