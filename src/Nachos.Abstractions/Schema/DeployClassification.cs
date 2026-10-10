namespace Nachos.Abstractions.Schema;

/// <summary>How risky it is to apply a schema deploy report without a human looking at it.</summary>
public enum DeployClassification
{
    /// <summary>No alerts, and every operation is on the explicit allowlist of additive changes.</summary>
    AutoSafe,

    /// <summary>The report has alerts, or contains a drop, rebuild, or type or nullability change.</summary>
    Unsafe,

    /// <summary>The report could not be parsed, or contains an operation or object type that is not recognised.</summary>
    Unclassifiable,
}