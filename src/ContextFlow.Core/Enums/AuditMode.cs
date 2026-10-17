namespace ContextFlow.Core.Enums;

/// <summary>
/// Determines how tool invocation payloads are recorded in audit logs.
/// </summary>
public enum AuditMode
{
    /// <summary>
    /// Persists sanitized inputs and outputs after regex redaction.
    /// </summary>
    Full = 0,

    /// <summary>
    /// Persists only sanitized inputs. Outputs are omitted (used for PII-returning tools).
    /// </summary>
    InputsOnly = 1,

    /// <summary>
    /// Persists only invocation metadata (outcome, duration, byte counts). No payloads.
    /// </summary>
    MetadataOnly = 2
}

