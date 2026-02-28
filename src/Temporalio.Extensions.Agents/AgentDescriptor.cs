namespace Temporalio.Extensions.Agents;

/// <summary>
/// Describes a registered agent for use by <see cref="IAgentRouter"/>.
/// </summary>
/// <param name="Name">The registered agent name (case-insensitive).</param>
/// <param name="Description">A concise description of what this agent does, used in the routing prompt.</param>
public sealed record AgentDescriptor(string Name, string Description);
