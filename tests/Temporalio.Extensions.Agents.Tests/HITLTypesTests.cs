using System.Text.Json;
using Microsoft.Extensions.AI;
using Temporalio.Extensions.AI;
using Xunit;

namespace Temporalio.Extensions.Agents.Tests;

/// <summary>
/// Tests that the HITL approval types serialize/deserialize correctly.
/// MAF now uses the canonical MEAI types: DurableApprovalRequest / DurableApprovalDecision.
/// </summary>
public class HITLTypesTests
{
    [Fact]
    public void DurableApprovalRequest_RequiresExplicitRequestId()
    {
        // required keyword means we must supply RequestId at construction time.
        var request = new DurableApprovalRequest { RequestId = "req-123" };
        Assert.Equal("req-123", request.RequestId);
    }

    [Fact]
    public void DurableApprovalRequest_RoundTripsViaJson()
    {
        var original = new DurableApprovalRequest
        {
            RequestId = "req-123",
            FunctionName = "send_email",
            CallId = "call-abc",
            Description = "Send email to alice@example.com"
        };

        var json = JsonSerializer.Serialize(original, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.Equal("send_email", deserialized.FunctionName);
        Assert.Equal("call-abc", deserialized.CallId);
        Assert.Equal("Send email to alice@example.com", deserialized.Description);
    }

    [Fact]
    public void DurableApprovalDecision_RoundTripsViaJson()
    {
        var decision = new DurableApprovalDecision
        {
            RequestId = "req-123",
            Approved = true,
            Reason = "Looks good."
        };

        var json = JsonSerializer.Serialize(decision, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalDecision>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Equal("req-123", deserialized.RequestId);
        Assert.True(deserialized.Approved);
        Assert.Equal("Looks good.", deserialized.Reason);
    }

    [Fact]
    public void DurableApprovalDecision_NullOptionalFields_SerializeCorrectly()
    {
        var decision = new DurableApprovalDecision { RequestId = "req-789", Approved = false };
        var json = JsonSerializer.Serialize(decision, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalDecision>(json, AIJsonUtilities.DefaultOptions);

        Assert.Null(deserialized?.Reason);
    }

    [Fact]
    public void DurableApprovalRequest_NullOptionalFields_SerializeCorrectly()
    {
        var request = new DurableApprovalRequest { RequestId = "req-456" };
        var json = JsonSerializer.Serialize(request, AIJsonUtilities.DefaultOptions);
        var deserialized = JsonSerializer.Deserialize<DurableApprovalRequest>(json, AIJsonUtilities.DefaultOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.FunctionName);
        Assert.Null(deserialized.CallId);
        Assert.Null(deserialized.Description);
    }
}
