namespace CodeLensAI.Core;

/// <summary>
/// All five AI documentation sections for a method, produced by a single merged model call
/// (<see cref="ExplanationService.GenerateMethodDocumentation"/>) instead of five round-trips.
/// </summary>
/// <param name="Brief">One-sentence description of the method.</param>
/// <param name="PrePostConditions">Bulleted pre- and post-conditions.</param>
/// <param name="DesignConstraints">Bulleted design constraints.</param>
/// <param name="ErrorAnalysis">Bulleted potential errors.</param>
/// <param name="Explanation">2-3 sentence prose explanation.</param>
public readonly record struct MethodAiDocumentation(
    string Brief,
    string PrePostConditions,
    string DesignConstraints,
    string ErrorAnalysis,
    string Explanation);
