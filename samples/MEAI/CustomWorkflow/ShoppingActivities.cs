using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Activities;
using Temporalio.Extensions.AI;

namespace CustomWorkflow;

/// <summary>
/// Temporal activities for the shopping assistant workflow.
/// Executes chat turns via the injected <see cref="IChatClient"/> and collects
/// cart mutation actions produced by tool calls during the LLM response.
/// </summary>
internal sealed class ShoppingActivities(
    IChatClient chatClient,
    ILoggerFactory? loggerFactory = null)
{
    private readonly ILogger _logger = (loggerFactory ?? NullLoggerFactory.Instance)
        .CreateLogger<ShoppingActivities>();

    /// <summary>
    /// Executes a shopping assistant chat turn.
    /// Injects cart tools into <see cref="ChatOptions"/> so the LLM can call them,
    /// then collects the resulting <see cref="CartAction"/> records and returns them
    /// alongside the <see cref="ChatResponse"/>.
    /// </summary>
    [Activity("CustomWorkflow.GetShoppingResponse")]
    public async Task<ShoppingTurnOutput> GetShoppingResponseAsync(DurableChatInput input)
    {
        var ctx = ActivityExecutionContext.Current;
        var ct = ctx.CancellationToken;

        _logger.LogDebug(
            "Executing shopping activity for conversation {ConversationId}, turn {TurnNumber}",
            input.ConversationId, input.TurnNumber);

        // Collect cart actions produced by the tools during this turn.
        var cartActions = new List<CartAction>();

        // Define cart tools that close over cartActions so mutations are captured.
        var addToCart = AIFunctionFactory.Create(
            (string productId, string productName, int quantity) =>
            {
                cartActions.Add(new CartAction
                {
                    ProductId = productId,
                    ProductName = productName,
                    Quantity = quantity,
                    Action = "add",
                });
                return $"Added {quantity}x {productName} (SKU: {productId}) to the cart.";
            },
            name: "add_to_cart",
            description: "Add a product to the shopping cart.");

        var removeFromCart = AIFunctionFactory.Create(
            (string productId) =>
            {
                var existing = cartActions.FirstOrDefault(a => a.ProductId == productId);
                cartActions.Add(new CartAction
                {
                    ProductId = productId,
                    ProductName = existing?.ProductName ?? productId,
                    Action = "remove",
                });
                return $"Removed product {productId} from the cart.";
            },
            name: "remove_from_cart",
            description: "Remove a product from the shopping cart by product ID.");

        // Merge the caller's ChatOptions with the cart tools.
        var options = new ChatOptions
        {
            Tools = [addToCart, removeFromCart],
            ToolMode = input.Options?.ToolMode ?? ChatToolMode.Auto,
            Temperature = input.Options?.Temperature,
            MaxOutputTokens = input.Options?.MaxOutputTokens,
            TopP = input.Options?.TopP,
            FrequencyPenalty = input.Options?.FrequencyPenalty,
            PresencePenalty = input.Options?.PresencePenalty,
        };

        // Heartbeat for long-running LLM calls.
        ctx.Heartbeat($"turn-{input.TurnNumber}");

        var response = await chatClient.GetResponseAsync(
            input.Messages,
            options,
            ct).ConfigureAwait(false);

        _logger.LogDebug(
            "Shopping activity completed for conversation {ConversationId}, turn {TurnNumber}. CartActions: {CartActionCount}",
            input.ConversationId, input.TurnNumber, cartActions.Count);

        return new ShoppingTurnOutput
        {
            Response = response,
            CartActions = cartActions,
        };
    }
}
