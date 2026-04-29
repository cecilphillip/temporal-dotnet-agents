namespace Temporalio.Extensions.Agents.State;

/// <summary>
/// Describes a registered agent by name and a short human-readable description.
/// Used by routing activities to build context-aware dispatch prompts.
/// </summary>
/// <param name="Name">The registered agent name (case-insensitive).</param>
/// <param name="Description">A concise description of what this agent does.</param>
public sealed record AgentDescriptor(string Name, string Description);
