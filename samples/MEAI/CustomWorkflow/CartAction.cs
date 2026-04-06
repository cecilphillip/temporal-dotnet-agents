namespace CustomWorkflow;

public sealed class CartAction
{
    public required string ProductId { get; init; }
    public required string ProductName { get; init; }
    public int Quantity { get; init; } = 1;
    public required string Action { get; init; } // "add" | "remove"
}
