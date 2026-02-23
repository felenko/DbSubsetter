namespace DbSubsetter.Core;

/// <summary>
/// A candidate row for the root entry: PK value and display text for the UI.
/// </summary>
public record RootRowCandidate(string PkValue, string Display);
