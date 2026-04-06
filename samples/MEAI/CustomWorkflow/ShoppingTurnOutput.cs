using Microsoft.Extensions.AI;

namespace CustomWorkflow;

public sealed class ShoppingTurnOutput
{
    public required ChatResponse Response { get; init; }
    public IReadOnlyList<CartAction> CartActions { get; init; } = [];
}
